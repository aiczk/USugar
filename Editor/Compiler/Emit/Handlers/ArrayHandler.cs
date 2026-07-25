using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class ArrayHandler : IExpressionHandler
{
    readonly LoweringServices _lowering;
    public ArrayHandler(LoweringServices lowering) => _lowering = lowering;

    public OperationKind[] HandledKinds { get; } = new[] { OperationKind.ArrayCreation, OperationKind.ArrayElementReference };

    public CLeaf Handle(IOperation expression) => expression switch
    {
        IArrayCreationOperation op => VisitArrayCreation(op),
        IArrayElementReferenceOperation op => VisitArrayElementReference(op),
        _ => throw new System.NotSupportedException(expression.GetType().Name),
    };

    CLeaf VisitArrayCreation(IArrayCreationOperation op)
    {
        if (NdimArrayAbi.IsNdimArray(op.Type)) return _lowering.Ndim.EmitNdimArrayCreation(op);

        var arrayType = _lowering.GetStorageTypeName(op.Type);
        var elementType = _lowering.GetArrayElemType((IArrayTypeSymbol)op.Type);
        var elemSym = ((IArrayTypeSymbol)op.Type).ElementType;
        bool aggElem = elemSym is INamedTypeSymbol && TypeClassifier.IsAggregateValue(elemSym);

        var sizeVal = _lowering.EmitArrayDimension(op.DimensionSizes[0]);
        var arrSlot = _lowering.State.Builder.AllocScratch(new StorageType(arrayType));
        _lowering.EmitAssign(arrSlot, _lowering.ExternCall(UdonAbi.ArrayConstructor(arrayType),
            new List<CLeaf> { sizeVal }, new StorageType(arrayType)));

        if (op.Initializer != null)
        {
            for (int i = 0; i < op.Initializer.ElementValues.Length; i++)
            {
                _lowering.EmitExternVoid(UdonAbi.ArraySet(arrayType, elementType),
                    new List<CLeaf> { _lowering.SlotRef(arrSlot), _lowering.Const(i, StorageTypes.Int32), _lowering.VisitExpression(op.Initializer.ElementValues[i]) });
            }
        }
        else if (aggElem)
        {
            // struct[]/tuple[]: C# zero-init means each element is a fresh default struct (not a null slot),
            // so `arr[i].field = x` works on a freshly allocated array. Fill via a runtime loop.
            var iSlot = _lowering.State.Builder.AllocScratch(StorageTypes.Int32);
            _lowering.EmitAssign(iSlot, _lowering.Const(0, StorageTypes.Int32));
            _lowering.Builder.EmitWhile(
                () => _lowering.ExternCall(UdonAbi.Int32LessThan,
                    new List<CLeaf> { _lowering.SlotRef(iSlot), sizeVal }, StorageTypes.Boolean),
                _ =>
                {
                    _lowering.EmitExternVoid(UdonAbi.ArraySet(arrayType, elementType),
                        new List<CLeaf> { _lowering.SlotRef(arrSlot), _lowering.SlotRef(iSlot),
                            AggregateAbi.MintDefault(_lowering.Builder, _lowering.State.Aggregates.GetLayout((INamedTypeSymbol)elemSym),
                                _lowering.State.Aggregates.GetLayout, _lowering.GetStorageTypeName) });
                    _lowering.EmitAssign(iSlot, _lowering.ExternCall(UdonAbi.Int32Add,
                        new List<CLeaf> { _lowering.SlotRef(iSlot), _lowering.Const(1, StorageTypes.Int32) }, StorageTypes.Int32));
                });
        }

        return _lowering.SlotRef(arrSlot);
    }

    CLeaf VisitArrayElementReference(IArrayElementReferenceOperation op)
    {
        if (op.Indices.Length > 1) return _lowering.Ndim.EmitNdimElementRead(op);

        var index = op.Indices[0];

        // Range slicing: arr[1..3]
        if (index is IRangeOperation rangeOp)
            return VisitRangeSlice(op.ArrayReference, rangeOp);

        var arrayVal = _lowering.VisitExpression(op.ArrayReference);
        var arrSymbol = op.ArrayReference.Type as IArrayTypeSymbol;
        var elementType = _lowering.GetArrayElemType(arrSymbol);
        var arrayType = _lowering.GetArrayType(arrSymbol);

        // Index from end: arr[^1] → arr[arr.Length - 1]
        var indexVal = _lowering.ResolveArrayIndex(arrayVal, arrayType, index);

        var resultVal = _lowering.ExternCall(UdonAbi.ArrayGet(arrayType, elementType), new List<CLeaf> { arrayVal, indexVal }, _lowering.GetStorageType(op.Type));
        // A struct/tuple element read AS A VALUE is copied (value semantics). Receiver access (arr[i].x =)
        // goes through LoadInstanceRaw → ReadArrayElementRaw, which does NOT clone.
        return op.Type is INamedTypeSymbol elemAggT && TypeClassifier.IsAggregateValue(elemAggT)
            ? AggregateAbi.DeepClone(_lowering.Builder, resultVal, elemAggT, _lowering.State.Aggregates.GetLayout) : resultVal;
    }

    CLeaf ResolveRangeOperand(CLeaf arrayVal, string arrayType, IOperation operand, bool isEnd)
    {
        if (operand == null)
            return isEnd ? EmitArrayLength(arrayVal, arrayType) : _lowering.Const(0, StorageTypes.Int32);
        // Unwrap conversion (int → System.Index)
        var inner = operand;
        while (inner is IConversionOperation conv) inner = conv.Operand;
        // Check for from-end (^n) within range
        if (inner is IUnaryOperation unary && unary.Type?.Name == "Index")
            return _lowering.EmitIndexFromEnd(arrayVal, arrayType, unary.Operand);
        return _lowering.VisitExpression(inner);
    }

    CLeaf EmitArrayLength(CLeaf arrayVal, string arrayType)
    {
        var lenVal = _lowering.ExternCall(UdonAbi.ArrayLength(arrayType),
            new List<CLeaf> { arrayVal }, StorageTypes.Int32);
        return lenVal;
    }

    CLeaf VisitRangeSlice(IOperation arrayRef, IRangeOperation rangeOp)
    {
        var arrayVal = _lowering.VisitExpression(arrayRef);
        var arrSymbol = arrayRef.Type as IArrayTypeSymbol;
        var elementType = _lowering.GetArrayElemType(arrSymbol);
        var arrayType = _lowering.GetArrayType(arrSymbol);
        var udonElemType = _lowering.GetStorageTypeName(arrSymbol.ElementType);
        var udonArrType = _lowering.GetStorageTypeName(arrayRef.Type);

        // arrayVal / startVal / lenVal / resultVal are already single-assignment scratch leaves under ANF,
        // stable across the loop — no extra snapshot slot needed.
        var startVal = ResolveRangeOperand(arrayVal, arrayType, rangeOp.LeftOperand, false);

        var endVal = ResolveRangeOperand(arrayVal, arrayType, rangeOp.RightOperand, true);

        // len = end - start
        var lenVal = _lowering.ExternCall(UdonAbi.Int32Subtract,
            new List<CLeaf> { endVal, startVal }, StorageTypes.Int32);

        // result = new T[len]
        var resultVal = _lowering.ExternCall(UdonAbi.ArrayConstructor(udonArrType),
            new List<CLeaf> { lenVal }, new StorageType(udonArrType));

        // for (i = 0; i < len; i++) result[i] = arr[start + i]
        var iSlot = _lowering.State.Builder.AllocScratch(StorageTypes.Int32);

        _lowering.Builder.EmitFor(
            // init: i = 0
            b => { _lowering.EmitAssign(iSlot, _lowering.Const(0, StorageTypes.Int32)); },
            // cond: i < len — MUST use the Func overload so it re-evaluates each iteration. The CLeaf overload
            // evaluates the comparison once (before init runs, with i uninitialized), so the copy loop never
            // iterates and the slice returns a correct-length but all-zero array.
            () => _lowering.ExternCall(UdonAbi.Int32LessThan,
                new List<CLeaf> { _lowering.SlotRef(iSlot), lenVal }, StorageTypes.Boolean),
            // update: i++
            b =>
            {
                var nextVal = _lowering.ExternCall(UdonAbi.Int32Add,
                    new List<CLeaf> { _lowering.SlotRef(iSlot), _lowering.Const(1, StorageTypes.Int32) }, StorageTypes.Int32);
                _lowering.EmitAssign(iSlot, nextVal);
            },
            // body
            b =>
            {
                // srcIdx = start + i
                var srcIdxVal = _lowering.ExternCall(UdonAbi.Int32Add,
                    new List<CLeaf> { startVal, _lowering.SlotRef(iSlot) }, StorageTypes.Int32);

                // val = arr[srcIdx]
                CLeaf valVal = _lowering.ExternCall(UdonAbi.ArrayGet(arrayType, elementType),
                    new List<CLeaf> { arrayVal, srcIdxVal }, new StorageType(udonElemType));

                // CW25 (closed-world audit): a struct/tuple element crossing into the slice is a VALUE
                // copy in C# (GetSubArray copies element values) — clone the bundle exactly like the
                // single-element read above, or the slice's elements alias the source array's.
                if (_lowering.ResolveType(arrSymbol.ElementType) is INamedTypeSymbol sliceElemAggT && TypeClassifier.IsAggregateValue(sliceElemAggT))
                    valVal = AggregateAbi.DeepClone(_lowering.Builder, valVal, sliceElemAggT, _lowering.State.Aggregates.GetLayout);

                // result[i] = val
                _lowering.EmitExternVoid(UdonAbi.ArraySet(arrayType, elementType),
                    new List<CLeaf> { resultVal, _lowering.SlotRef(iSlot), valVal });
            }
        );

        return resultVal;
    }
}

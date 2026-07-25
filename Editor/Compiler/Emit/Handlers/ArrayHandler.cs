using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class ArrayHandler : HandlerBase, IExpressionHandler
{
    public ArrayHandler(EmitContext ctx) : base(ctx) { }

    public OperationKind[] HandledKinds { get; } = new[] { OperationKind.ArrayCreation, OperationKind.ArrayElementReference };

    public CLeaf Handle(IOperation expression) => expression switch
    {
        IArrayCreationOperation op => VisitArrayCreation(op),
        IArrayElementReferenceOperation op => VisitArrayElementReference(op),
        _ => throw new System.NotSupportedException(expression.GetType().Name),
    };

    CLeaf VisitArrayCreation(IArrayCreationOperation op)
    {
        if (NdimArrayAbi.IsNdimArray(op.Type)) return EmitNdimArrayCreation(op);

        var arrayType = GetStorageTypeName(op.Type);
        var elementType = GetArrayElemType((IArrayTypeSymbol)op.Type);
        var elemSym = ((IArrayTypeSymbol)op.Type).ElementType;
        bool aggElem = elemSym is INamedTypeSymbol && TypeClassifier.IsAggregateValue(elemSym);

        var sizeVal = EmitArrayDimension(op.DimensionSizes[0]);
        var arrSlot = _ctx.Builder.AllocScratch(new StorageType(arrayType));
        EmitAssign(arrSlot, ExternCall(UdonAbi.ArrayConstructor(arrayType),
            new List<CLeaf> { sizeVal }, new StorageType(arrayType)));

        if (op.Initializer != null)
        {
            for (int i = 0; i < op.Initializer.ElementValues.Length; i++)
            {
                EmitExternVoid(UdonAbi.ArraySet(arrayType, elementType),
                    new List<CLeaf> { SlotRef(arrSlot), Const(i, StorageTypes.Int32), VisitExpression(op.Initializer.ElementValues[i]) });
            }
        }
        else if (aggElem)
        {
            // struct[]/tuple[]: C# zero-init means each element is a fresh default struct (not a null slot),
            // so `arr[i].field = x` works on a freshly allocated array. Fill via a runtime loop.
            var iSlot = _ctx.Builder.AllocScratch(StorageTypes.Int32);
            EmitAssign(iSlot, Const(0, StorageTypes.Int32));
            _builder.EmitWhile(
                () => ExternCall(UdonAbi.Int32LessThan,
                    new List<CLeaf> { SlotRef(iSlot), sizeVal }, StorageTypes.Boolean),
                _ =>
                {
                    EmitExternVoid(UdonAbi.ArraySet(arrayType, elementType),
                        new List<CLeaf> { SlotRef(arrSlot), SlotRef(iSlot),
                            AggregateAbi.MintDefault(_builder, _ctx.Aggregates.GetLayout((INamedTypeSymbol)elemSym),
                                _ctx.Aggregates.GetLayout, GetStorageTypeName) });
                    EmitAssign(iSlot, ExternCall(UdonAbi.Int32Add,
                        new List<CLeaf> { SlotRef(iSlot), Const(1, StorageTypes.Int32) }, StorageTypes.Int32));
                });
        }

        return SlotRef(arrSlot);
    }

    CLeaf VisitArrayElementReference(IArrayElementReferenceOperation op)
    {
        if (op.Indices.Length > 1) return EmitNdimElementRead(op);

        var index = op.Indices[0];

        // Range slicing: arr[1..3]
        if (index is IRangeOperation rangeOp)
            return VisitRangeSlice(op.ArrayReference, rangeOp);

        var arrayVal = VisitExpression(op.ArrayReference);
        var arrSymbol = op.ArrayReference.Type as IArrayTypeSymbol;
        var elementType = GetArrayElemType(arrSymbol);
        var arrayType = GetArrayType(arrSymbol);

        // Index from end: arr[^1] → arr[arr.Length - 1]
        var indexVal = ResolveArrayIndex(arrayVal, arrayType, index);

        var resultVal = ExternCall(UdonAbi.ArrayGet(arrayType, elementType), new List<CLeaf> { arrayVal, indexVal }, GetStorageType(op.Type));
        // A struct/tuple element read AS A VALUE is copied (value semantics). Receiver access (arr[i].x =)
        // goes through LoadInstanceRaw → ReadArrayElementRaw, which does NOT clone.
        return op.Type is INamedTypeSymbol elemAggT && TypeClassifier.IsAggregateValue(elemAggT)
            ? AggregateAbi.DeepClone(_builder, resultVal, elemAggT, _ctx.Aggregates.GetLayout) : resultVal;
    }

    CLeaf ResolveRangeOperand(CLeaf arrayVal, string arrayType, IOperation operand, bool isEnd)
    {
        if (operand == null)
            return isEnd ? EmitArrayLength(arrayVal, arrayType) : Const(0, StorageTypes.Int32);
        // Unwrap conversion (int → System.Index)
        var inner = operand;
        while (inner is IConversionOperation conv) inner = conv.Operand;
        // Check for from-end (^n) within range
        if (inner is IUnaryOperation unary && unary.Type?.Name == "Index")
            return EmitIndexFromEnd(arrayVal, arrayType, unary.Operand);
        return VisitExpression(inner);
    }

    CLeaf EmitArrayLength(CLeaf arrayVal, string arrayType)
    {
        var lenVal = ExternCall(UdonAbi.ArrayLength(arrayType),
            new List<CLeaf> { arrayVal }, StorageTypes.Int32);
        return lenVal;
    }

    CLeaf VisitRangeSlice(IOperation arrayRef, IRangeOperation rangeOp)
    {
        var arrayVal = VisitExpression(arrayRef);
        var arrSymbol = arrayRef.Type as IArrayTypeSymbol;
        var elementType = GetArrayElemType(arrSymbol);
        var arrayType = GetArrayType(arrSymbol);
        var udonElemType = GetStorageTypeName(arrSymbol.ElementType);
        var udonArrType = GetStorageTypeName(arrayRef.Type);

        // arrayVal / startVal / lenVal / resultVal are already single-assignment scratch leaves under ANF,
        // stable across the loop — no extra snapshot slot needed.
        var startVal = ResolveRangeOperand(arrayVal, arrayType, rangeOp.LeftOperand, false);

        var endVal = ResolveRangeOperand(arrayVal, arrayType, rangeOp.RightOperand, true);

        // len = end - start
        var lenVal = ExternCall(UdonAbi.Int32Subtract,
            new List<CLeaf> { endVal, startVal }, StorageTypes.Int32);

        // result = new T[len]
        var resultVal = ExternCall(UdonAbi.ArrayConstructor(udonArrType),
            new List<CLeaf> { lenVal }, new StorageType(udonArrType));

        // for (i = 0; i < len; i++) result[i] = arr[start + i]
        var iSlot = _ctx.Builder.AllocScratch(StorageTypes.Int32);

        _builder.EmitFor(
            // init: i = 0
            b => { EmitAssign(iSlot, Const(0, StorageTypes.Int32)); },
            // cond: i < len — MUST use the Func overload so it re-evaluates each iteration. The CLeaf overload
            // evaluates the comparison once (before init runs, with i uninitialized), so the copy loop never
            // iterates and the slice returns a correct-length but all-zero array.
            () => ExternCall(UdonAbi.Int32LessThan,
                new List<CLeaf> { SlotRef(iSlot), lenVal }, StorageTypes.Boolean),
            // update: i++
            b =>
            {
                var nextVal = ExternCall(UdonAbi.Int32Add,
                    new List<CLeaf> { SlotRef(iSlot), Const(1, StorageTypes.Int32) }, StorageTypes.Int32);
                EmitAssign(iSlot, nextVal);
            },
            // body
            b =>
            {
                // srcIdx = start + i
                var srcIdxVal = ExternCall(UdonAbi.Int32Add,
                    new List<CLeaf> { startVal, SlotRef(iSlot) }, StorageTypes.Int32);

                // val = arr[srcIdx]
                CLeaf valVal = ExternCall(UdonAbi.ArrayGet(arrayType, elementType),
                    new List<CLeaf> { arrayVal, srcIdxVal }, new StorageType(udonElemType));

                // CW25 (closed-world audit): a struct/tuple element crossing into the slice is a VALUE
                // copy in C# (GetSubArray copies element values) — clone the bundle exactly like the
                // single-element read above, or the slice's elements alias the source array's.
                if (ResolveType(arrSymbol.ElementType) is INamedTypeSymbol sliceElemAggT && TypeClassifier.IsAggregateValue(sliceElemAggT))
                    valVal = AggregateAbi.DeepClone(_builder, valVal, sliceElemAggT, _ctx.Aggregates.GetLayout);

                // result[i] = val
                EmitExternVoid(UdonAbi.ArraySet(arrayType, elementType),
                    new List<CLeaf> { resultVal, SlotRef(iSlot), valVal });
            }
        );

        return resultVal;
    }
}

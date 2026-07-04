using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class ArrayHandler : HandlerBase, IExpressionHandler
{
    public ArrayHandler(EmitContext ctx) : base(ctx) { }

    public bool CanHandle(IOperation expression)
        => expression is IArrayCreationOperation or IArrayElementReferenceOperation;

    public CLeaf Handle(IOperation expression) => expression switch
    {
        IArrayCreationOperation op => VisitArrayCreation(op),
        IArrayElementReferenceOperation op => VisitArrayElementReference(op),
        _ => throw new System.NotSupportedException(expression.GetType().Name),
    };

    CLeaf VisitArrayCreation(IArrayCreationOperation op)
    {
        if (IsNdimArray(op.Type)) return EmitNdimArrayCreation(op);

        var arrayType = GetUdonType(op.Type);
        var elementType = GetArrayElemType((IArrayTypeSymbol)op.Type);
        var elemSym = ((IArrayTypeSymbol)op.Type).ElementType;
        bool aggElem = elemSym is INamedTypeSymbol && EmitContext.IsAggregateType(elemSym);

        var sizeVal = VisitExpression(op.DimensionSizes[0]);
        var arrSlot = _ctx.AllocTemp(arrayType);
        EmitAssign(arrSlot, ExternCall(ExternResolver.BuildArrayCtorSignature(arrayType),
            new List<CLeaf> { sizeVal }, arrayType));

        if (op.Initializer != null)
        {
            for (int i = 0; i < op.Initializer.ElementValues.Length; i++)
            {
                EmitExternVoid(ExternResolver.BuildArraySetSignature(arrayType, elementType),
                    new List<CLeaf> { SlotRef(arrSlot), Const(i, "SystemInt32"), VisitExpression(op.Initializer.ElementValues[i]) });
            }
        }
        else if (aggElem)
        {
            // struct[]/tuple[]: C# zero-init means each element is a fresh default struct (not a null slot),
            // so `arr[i].field = x` works on a freshly allocated array. Fill via a runtime loop.
            var iSlot = _ctx.AllocTemp("SystemInt32");
            EmitAssign(iSlot, Const(0, "SystemInt32"));
            _builder.EmitWhile(
                () => ExternCall("SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
                    new List<CLeaf> { SlotRef(iSlot), sizeVal }, "SystemBoolean"),
                _ =>
                {
                    EmitExternVoid(ExternResolver.BuildArraySetSignature(arrayType, elementType),
                        new List<CLeaf> { SlotRef(arrSlot), SlotRef(iSlot), EmitNewAggregate((INamedTypeSymbol)elemSym) });
                    EmitAssign(iSlot, ExternCall("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                        new List<CLeaf> { SlotRef(iSlot), Const(1, "SystemInt32") }, "SystemInt32"));
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

        var resultVal = ExternCall(ExternResolver.BuildArrayGetSignature(arrayType, elementType), new List<CLeaf> { arrayVal, indexVal }, GetUdonType(op.Type));
        // A struct/tuple element read AS A VALUE is copied (value semantics). Receiver access (arr[i].x =)
        // goes through LoadInstanceRaw → ReadArrayElementRaw, which does NOT clone.
        return op.Type is INamedTypeSymbol elemAggT && EmitContext.IsAggregateType(elemAggT)
            ? EmitDeepCloneAggregate(resultVal, elemAggT) : resultVal;
    }

    CLeaf ResolveRangeOperand(CLeaf arrayVal, string arrayType, IOperation operand, bool isEnd)
    {
        if (operand == null)
            return isEnd ? EmitArrayLength(arrayVal, arrayType) : Const(0, "SystemInt32");
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
        var lenVal = ExternCall($"{arrayType}.__get_Length__SystemInt32", new List<CLeaf> { arrayVal }, "SystemInt32");
        return lenVal;
    }

    CLeaf VisitRangeSlice(IOperation arrayRef, IRangeOperation rangeOp)
    {
        var arrayVal = VisitExpression(arrayRef);
        var arrSymbol = arrayRef.Type as IArrayTypeSymbol;
        var elementType = GetArrayElemType(arrSymbol);
        var arrayType = GetArrayType(arrSymbol);
        var udonElemType = GetUdonType(arrSymbol.ElementType);
        var udonArrType = GetUdonType(arrayRef.Type);

        // arrayVal / startVal / lenVal / resultVal are already single-assignment scratch leaves under ANF,
        // stable across the loop — no extra snapshot slot needed.
        var startVal = ResolveRangeOperand(arrayVal, arrayType, rangeOp.LeftOperand, false);

        var endVal = ResolveRangeOperand(arrayVal, arrayType, rangeOp.RightOperand, true);

        // len = end - start
        var lenVal = ExternCall("SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32",
            new List<CLeaf> { endVal, startVal }, "SystemInt32");

        // result = new T[len]
        var resultVal = ExternCall(ExternResolver.BuildArrayCtorSignature(udonArrType),
            new List<CLeaf> { lenVal }, udonArrType);

        // for (i = 0; i < len; i++) result[i] = arr[start + i]
        var iSlot = _ctx.AllocTemp("SystemInt32");

        _builder.EmitFor(
            // init: i = 0
            b => { EmitAssign(iSlot, Const(0, "SystemInt32")); },
            // cond: i < len — MUST use the Func overload so it re-evaluates each iteration. The CLeaf overload
            // evaluates the comparison once (before init runs, with i uninitialized), so the copy loop never
            // iterates and the slice returns a correct-length but all-zero array.
            () => ExternCall("SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
                new List<CLeaf> { SlotRef(iSlot), lenVal }, "SystemBoolean"),
            // update: i++
            b =>
            {
                var nextVal = ExternCall("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                    new List<CLeaf> { SlotRef(iSlot), Const(1, "SystemInt32") }, "SystemInt32");
                EmitAssign(iSlot, nextVal);
            },
            // body
            b =>
            {
                // srcIdx = start + i
                var srcIdxVal = ExternCall("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                    new List<CLeaf> { startVal, SlotRef(iSlot) }, "SystemInt32");

                // val = arr[srcIdx]
                var valVal = ExternCall(ExternResolver.BuildArrayGetSignature(arrayType, elementType),
                    new List<CLeaf> { arrayVal, srcIdxVal }, udonElemType);

                // result[i] = val
                EmitExternVoid(ExternResolver.BuildArraySetSignature(arrayType, elementType),
                    new List<CLeaf> { resultVal, SlotRef(iSlot), valVal });
            }
        );

        return resultVal;
    }
}

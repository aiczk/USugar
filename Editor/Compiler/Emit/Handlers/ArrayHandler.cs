using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class ArrayHandler : HandlerBase, IExpressionHandler
{
    public ArrayHandler(EmitContext ctx) : base(ctx) { }

    public bool CanHandle(IOperation expression)
        => expression is IArrayCreationOperation or IArrayElementReferenceOperation;

    public HExpr Handle(IOperation expression) => expression switch
    {
        IArrayCreationOperation op => VisitArrayCreation(op),
        IArrayElementReferenceOperation op => VisitArrayElementReference(op),
        _ => throw new System.NotSupportedException(expression.GetType().Name),
    };

    HExpr VisitArrayCreation(IArrayCreationOperation op)
    {
        var arrayType = GetUdonType(op.Type);
        var elementType = GetArrayElemType((IArrayTypeSymbol)op.Type);
        var sizeVal = VisitExpression(op.DimensionSizes[0]);
        var resultVal = ExternCall($"{arrayType}.__ctor__SystemInt32__{arrayType}", new List<HExpr> { sizeVal }, arrayType);

        if (op.Initializer == null)
            return resultVal;

        // Store array in a scratch slot so initializer element sets reference the same array
        var arrSlot = _ctx.AllocTemp(arrayType);
        EmitAssign(arrSlot, resultVal);

        for (int i = 0; i < op.Initializer.ElementValues.Length; i++)
        {
            var valVal = VisitExpression(op.Initializer.ElementValues[i]);
            var idxConst = Const(i, "SystemInt32");
            EmitExternVoid($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid", new List<HExpr> { SlotRef(arrSlot), idxConst, valVal });
        }

        return SlotRef(arrSlot);
    }

    HExpr VisitArrayElementReference(IArrayElementReferenceOperation op)
    {
        var index = op.Indices[0];

        // Range slicing: arr[1..3]
        if (index is IRangeOperation rangeOp)
            return VisitRangeSlice(op.ArrayReference, rangeOp);

        var arrayVal = VisitExpression(op.ArrayReference);
        var arrSymbol = op.ArrayReference.Type as IArrayTypeSymbol;
        var elementType = GetArrayElemType(arrSymbol);
        var arrayType = GetArrayType(arrSymbol);

        // Index from end: arr[^1] → arr[arr.Length - 1]
        HExpr indexVal;
        indexVal = index is IUnaryOperation { Type: { Name: "Index" } } unary
            ? EmitIndexFromEnd(arrayVal, arrayType, unary.Operand)
            : VisitExpression(index);

        var resultVal = ExternCall($"{arrayType}.__Get__SystemInt32__{elementType}", new List<HExpr> { arrayVal, indexVal }, GetUdonType(op.Type));
        return resultVal;
    }

    HExpr EmitIndexFromEnd(HExpr arrayVal, string arrayType, IOperation operand)
    {
        var lenVal = ExternCall($"{arrayType}.__get_Length__SystemInt32", new List<HExpr> { arrayVal }, "SystemInt32");
        var nVal = VisitExpression(operand);
        var resultVal = ExternCall("SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32", new List<HExpr> { lenVal, nVal }, "SystemInt32");
        return resultVal;
    }

    HExpr ResolveRangeOperand(HExpr arrayVal, string arrayType, IOperation operand, bool isEnd)
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

    HExpr EmitArrayLength(HExpr arrayVal, string arrayType)
    {
        var lenVal = ExternCall($"{arrayType}.__get_Length__SystemInt32", new List<HExpr> { arrayVal }, "SystemInt32");
        return lenVal;
    }

    HExpr VisitRangeSlice(IOperation arrayRef, IRangeOperation rangeOp)
    {
        var arrayVal = VisitExpression(arrayRef);
        var arrSymbol = arrayRef.Type as IArrayTypeSymbol;
        var elementType = GetArrayElemType(arrSymbol);
        var arrayType = GetArrayType(arrSymbol);
        var udonElemType = GetUdonType(arrSymbol.ElementType);
        var udonArrType = GetUdonType(arrayRef.Type);

        // Store array in scratch slot to avoid re-evaluation
        var arrSlot = _ctx.AllocTemp(udonArrType);
        EmitAssign(arrSlot, arrayVal);

        var startVal = ResolveRangeOperand(SlotRef(arrSlot), arrayType, rangeOp.LeftOperand, false);
        var startSlot = _ctx.AllocTemp("SystemInt32");
        EmitAssign(startSlot, startVal);

        var endVal = ResolveRangeOperand(SlotRef(arrSlot), arrayType, rangeOp.RightOperand, true);

        // len = end - start
        var lenVal = ExternCall("SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32",
            new List<HExpr> { endVal, SlotRef(startSlot) }, "SystemInt32");
        var lenSlot = _ctx.AllocTemp("SystemInt32");
        EmitAssign(lenSlot, lenVal);

        // result = new T[len]
        var resultVal = ExternCall($"{udonArrType}.__ctor__SystemInt32__{udonArrType}",
            new List<HExpr> { SlotRef(lenSlot) }, udonArrType);
        var resultSlot = _ctx.AllocTemp(udonArrType);
        EmitAssign(resultSlot, resultVal);

        // for (i = 0; i < len; i++) result[i] = arr[start + i]
        var iSlot = _ctx.AllocTemp("SystemInt32");

        _builder.EmitFor(
            // init: i = 0
            b => { EmitAssign(iSlot, Const(0, "SystemInt32")); },
            // cond: i < len
            ExternCall("SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
                new List<HExpr> { SlotRef(iSlot), SlotRef(lenSlot) }, "SystemBoolean"),
            // update: i++
            b =>
            {
                var nextVal = ExternCall("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                    new List<HExpr> { SlotRef(iSlot), Const(1, "SystemInt32") }, "SystemInt32");
                EmitAssign(iSlot, nextVal);
            },
            // body
            b =>
            {
                // srcIdx = start + i
                var srcIdxVal = ExternCall("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                    new List<HExpr> { SlotRef(startSlot), SlotRef(iSlot) }, "SystemInt32");

                // val = arr[srcIdx]
                var valVal = ExternCall($"{arrayType}.__Get__SystemInt32__{elementType}",
                    new List<HExpr> { SlotRef(arrSlot), srcIdxVal }, udonElemType);

                // result[i] = val
                EmitExternVoid($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid",
                    new List<HExpr> { SlotRef(resultSlot), SlotRef(iSlot), valVal });
            }
        );

        return SlotRef(resultSlot);
    }
}

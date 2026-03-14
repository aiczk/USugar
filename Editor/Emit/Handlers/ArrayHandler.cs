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

        // Store array in a temp field so initializer element sets reference the same array
        var arrField = _ctx.DeclareTemp(arrayType);
        EmitStoreField(arrField, resultVal);

        for (int i = 0; i < op.Initializer.ElementValues.Length; i++)
        {
            var valVal = VisitExpression(op.Initializer.ElementValues[i]);
            var idxConst = Const(i, "SystemInt32");
            var arrRef = LoadField(arrField, arrayType);
            EmitExternVoid($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid", new List<HExpr> { arrRef, idxConst, valVal });
        }

        return LoadField(arrField, arrayType);
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

        // Store array in temp to avoid re-evaluation
        var arrField = _ctx.DeclareTemp(udonArrType);
        EmitStoreField(arrField, arrayVal);

        var arrRef = LoadField(arrField, udonArrType);
        var startVal = ResolveRangeOperand(arrRef, arrayType, rangeOp.LeftOperand, false);
        var startField = _ctx.DeclareTemp("SystemInt32");
        EmitStoreField(startField, startVal);

        arrRef = LoadField(arrField, udonArrType);
        var endVal = ResolveRangeOperand(arrRef, arrayType, rangeOp.RightOperand, true);

        // len = end - start
        var lenVal = ExternCall("SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32",
            new List<HExpr> { endVal, LoadField(startField, "SystemInt32") }, "SystemInt32");
        var lenField = _ctx.DeclareTemp("SystemInt32");
        EmitStoreField(lenField, lenVal);

        // result = new T[len]
        var resultVal = ExternCall($"{udonArrType}.__ctor__SystemInt32__{udonArrType}",
            new List<HExpr> { LoadField(lenField, "SystemInt32") }, udonArrType);
        var resultField = _ctx.DeclareTemp(udonArrType);
        EmitStoreField(resultField, resultVal);

        // for (i = 0; i < len; i++) result[i] = arr[start + i]
        var iField = _ctx.DeclareTemp("SystemInt32");

        _builder.EmitFor(
            // init: i = 0
            b => { EmitStoreField(iField, Const(0, "SystemInt32")); },
            // cond: i < len
            ExternCall("SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean",
                new List<HExpr> { LoadField(iField, "SystemInt32"), LoadField(lenField, "SystemInt32") }, "SystemBoolean"),
            // update: i++
            b =>
            {
                var nextVal = ExternCall("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                    new List<HExpr> { LoadField(iField, "SystemInt32"), Const(1, "SystemInt32") }, "SystemInt32");
                EmitStoreField(iField, nextVal);
            },
            // body
            b =>
            {
                // srcIdx = start + i
                var srcIdxVal = ExternCall("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32",
                    new List<HExpr> { LoadField(startField, "SystemInt32"), LoadField(iField, "SystemInt32") }, "SystemInt32");

                // val = arr[srcIdx]
                var valVal = ExternCall($"{arrayType}.__Get__SystemInt32__{elementType}",
                    new List<HExpr> { LoadField(arrField, udonArrType), srcIdxVal }, udonElemType);

                // result[i] = val
                EmitExternVoid($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid",
                    new List<HExpr> { LoadField(resultField, udonArrType), LoadField(iField, "SystemInt32"), valVal });
            }
        );

        return LoadField(resultField, udonArrType);
    }
}

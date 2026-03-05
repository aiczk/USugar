using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class ArrayHandler : HandlerBase, IExpressionHandler
{
    public ArrayHandler(EmitContext ctx) : base(ctx) { }

    public bool CanHandle(IOperation expression)
        => expression is IArrayCreationOperation or IArrayElementReferenceOperation;

    public string Handle(IOperation expression) => expression switch
    {
        IArrayCreationOperation op => VisitArrayCreation(op),
        IArrayElementReferenceOperation op => VisitArrayElementReference(op),
        _ => throw new System.NotSupportedException(expression.GetType().Name),
    };

    string VisitArrayCreation(IArrayCreationOperation op)
    {
        var arrayType = GetUdonType(op.Type);
        var elementType = GetArrayElemType((IArrayTypeSymbol)op.Type);
        var savedHint = _ctx.TargetHint;
        _ctx.TargetHint = null;
        var sizeId = VisitExpression(op.DimensionSizes[0]);
        _ctx.TargetHint = savedHint;
        var resultId = ConsumeTargetHintOrTemp(arrayType);
        _module.AddPush(sizeId);
        _module.AddPush(resultId);
        AddExternChecked($"{arrayType}.__ctor__SystemInt32__{arrayType}");

        if (op.Initializer == null) 
            return resultId;
        
        for (int i = 0; i < op.Initializer.ElementValues.Length; i++)
        {
            var valId = VisitExpression(op.Initializer.ElementValues[i]);
            var idxConst = _vars.DeclareConst("SystemInt32", i.ToString());
            _module.AddPush(resultId);
            _module.AddPush(idxConst);
            _module.AddPush(valId);
            AddExternChecked($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid");
        }

        return resultId;
    }

    string VisitArrayElementReference(IArrayElementReferenceOperation op)
    {
        var index = op.Indices[0];

        // Range slicing: arr[1..3]
        if (index is IRangeOperation rangeOp)
            return VisitRangeSlice(op.ArrayReference, rangeOp);

        var savedHint = _ctx.TargetHint;
        _ctx.TargetHint = null;

        var arrayId = VisitExpression(op.ArrayReference);
        var arrSymbol = op.ArrayReference.Type as IArrayTypeSymbol;
        var elementType = GetArrayElemType(arrSymbol);
        var arrayType = GetArrayType(arrSymbol);

        // Index from end: arr[^1] → arr[arr.Length - 1]
        string indexId;
        indexId = index is IUnaryOperation { Type: { Name: "Index" } } unary 
            ? EmitIndexFromEnd(arrayId, arrayType, unary.Operand) 
            : VisitExpression(index);

        _ctx.TargetHint = savedHint;
        var resultId = ConsumeTargetHintOrTemp(GetUdonType(op.Type));
        _module.AddPush(arrayId);
        _module.AddPush(indexId);
        _module.AddPush(resultId);
        AddExternChecked($"{arrayType}.__Get__SystemInt32__{elementType}");
        return resultId;
    }

    string EmitIndexFromEnd(string arrayId, string arrayType, IOperation operand)
    {
        var lenId = _vars.DeclareTemp("SystemInt32");
        _module.AddPush(arrayId);
        _module.AddPush(lenId);
        AddExternChecked($"{arrayType}.__get_Length__SystemInt32");
        var nId = VisitExpression(operand);
        var resultId = _vars.DeclareTemp("SystemInt32");
        _module.AddPush(lenId);
        _module.AddPush(nId);
        _module.AddPush(resultId);
        AddExternChecked("SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32");
        return resultId;
    }

    string ResolveRangeOperand(IOperation operand, string arrayId, string arrayType, bool isEnd)
    {
        if (operand == null)
            return isEnd ? EmitArrayLength(arrayId, arrayType) : _vars.DeclareConst("SystemInt32", "0");
        // Unwrap conversion (int → System.Index)
        var inner = operand;
        while (inner is IConversionOperation conv) inner = conv.Operand;
        // Check for from-end (^n) within range
        if (inner is IUnaryOperation unary && unary.Type?.Name == "Index")
            return EmitIndexFromEnd(arrayId, arrayType, unary.Operand);
        return VisitExpression(inner);
    }

    string EmitArrayLength(string arrayId, string arrayType)
    {
        var lenId = _vars.DeclareTemp("SystemInt32");
        _module.AddPush(arrayId);
        _module.AddPush(lenId);
        AddExternChecked($"{arrayType}.__get_Length__SystemInt32");
        return lenId;
    }

    string VisitRangeSlice(IOperation arrayRef, IRangeOperation rangeOp)
    {
        var arrayId = VisitExpression(arrayRef);
        var arrSymbol = arrayRef.Type as IArrayTypeSymbol;
        var elementType = GetArrayElemType(arrSymbol);
        var arrayType = GetArrayType(arrSymbol);
        var udonElemType = GetUdonType(arrSymbol.ElementType);
        var udonArrType = GetUdonType(arrayRef.Type);

        var startId = ResolveRangeOperand(rangeOp.LeftOperand, arrayId, arrayType, false);
        var endId = ResolveRangeOperand(rangeOp.RightOperand, arrayId, arrayType, true);

        // len = end - start
        var lenId = _vars.DeclareTemp("SystemInt32");
        _module.AddPush(endId);
        _module.AddPush(startId);
        _module.AddPush(lenId);
        AddExternChecked("SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32");

        // result = new T[len]
        var resultId = _vars.DeclareTemp(udonArrType);
        _module.AddPush(lenId);
        _module.AddPush(resultId);
        AddExternChecked($"{udonArrType}.__ctor__SystemInt32__{udonArrType}");

        // for (i = 0; i < len; i++) result[i] = arr[start + i]
        var iId = _vars.DeclareTemp("SystemInt32");
        var zeroConst = _vars.DeclareConst("SystemInt32", "0");
        _module.AddCopy(zeroConst, iId);
        var loopLabel = _module.DefineLabel("__range_loop");
        var endLabel = _module.DefineLabel("__range_end");
        _module.MarkLabel(loopLabel);

        // i < len
        var condId = _vars.DeclareTemp("SystemBoolean");
        _module.AddPush(iId);
        _module.AddPush(lenId);
        _module.AddPush(condId);
        AddExternChecked("SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean");
        _module.AddPush(condId);
        _module.AddJumpIfFalse(endLabel);

        // srcIdx = start + i
        var srcIdxId = _vars.DeclareTemp("SystemInt32");
        _module.AddPush(startId);
        _module.AddPush(iId);
        _module.AddPush(srcIdxId);
        AddExternChecked("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");

        // val = arr[srcIdx]
        var valId = _vars.DeclareTemp(udonElemType);
        _module.AddPush(arrayId);
        _module.AddPush(srcIdxId);
        _module.AddPush(valId);
        AddExternChecked($"{arrayType}.__Get__SystemInt32__{elementType}");

        // result[i] = val
        _module.AddPush(resultId);
        _module.AddPush(iId);
        _module.AddPush(valId);
        AddExternChecked($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid");

        // i++
        var oneConst = _vars.DeclareConst("SystemInt32", "1");
        var nextId = _vars.DeclareTemp("SystemInt32");
        _module.AddPush(iId);
        _module.AddPush(oneConst);
        _module.AddPush(nextId);
        AddExternChecked("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        _module.AddCopy(nextId, iId);
        _module.AddJump(loopLabel);
        _module.MarkLabel(endLabel);

        return resultId;
    }
}

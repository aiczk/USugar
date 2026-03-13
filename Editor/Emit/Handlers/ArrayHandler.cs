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
        _ctx.Module.AddPush(sizeId);
        _ctx.Module.AddPush(resultId);
        AddExternChecked($"{arrayType}.__ctor__SystemInt32__{arrayType}");

        if (op.Initializer == null) 
            return resultId;
        
        for (int i = 0; i < op.Initializer.ElementValues.Length; i++)
        {
            var valId = VisitExpression(op.Initializer.ElementValues[i]);
            var idxConst = _ctx.Vars.DeclareConst("SystemInt32", i.ToString());
            _ctx.Module.AddPush(resultId);
            _ctx.Module.AddPush(idxConst);
            _ctx.Module.AddPush(valId);
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
        _ctx.Module.AddPush(arrayId);
        _ctx.Module.AddPush(indexId);
        _ctx.Module.AddPush(resultId);
        AddExternChecked($"{arrayType}.__Get__SystemInt32__{elementType}");
        return resultId;
    }

    string EmitIndexFromEnd(string arrayId, string arrayType, IOperation operand)
    {
        var lenId = _ctx.Vars.DeclareTemp("SystemInt32");
        _ctx.Module.AddPush(arrayId);
        _ctx.Module.AddPush(lenId);
        AddExternChecked($"{arrayType}.__get_Length__SystemInt32");
        var nId = VisitExpression(operand);
        var resultId = _ctx.Vars.DeclareTemp("SystemInt32");
        _ctx.Module.AddPush(lenId);
        _ctx.Module.AddPush(nId);
        _ctx.Module.AddPush(resultId);
        AddExternChecked("SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32");
        return resultId;
    }

    string ResolveRangeOperand(IOperation operand, string arrayId, string arrayType, bool isEnd)
    {
        if (operand == null)
            return isEnd ? EmitArrayLength(arrayId, arrayType) : _ctx.Vars.DeclareConst("SystemInt32", "0");
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
        var lenId = _ctx.Vars.DeclareTemp("SystemInt32");
        _ctx.Module.AddPush(arrayId);
        _ctx.Module.AddPush(lenId);
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
        var lenId = _ctx.Vars.DeclareTemp("SystemInt32");
        _ctx.Module.AddPush(endId);
        _ctx.Module.AddPush(startId);
        _ctx.Module.AddPush(lenId);
        AddExternChecked("SystemInt32.__op_Subtraction__SystemInt32_SystemInt32__SystemInt32");

        // result = new T[len]
        var resultId = _ctx.Vars.DeclareTemp(udonArrType);
        _ctx.Module.AddPush(lenId);
        _ctx.Module.AddPush(resultId);
        AddExternChecked($"{udonArrType}.__ctor__SystemInt32__{udonArrType}");

        // for (i = 0; i < len; i++) result[i] = arr[start + i]
        var iId = _ctx.Vars.DeclareTemp("SystemInt32");
        var zeroConst = _ctx.Vars.DeclareConst("SystemInt32", "0");
        _ctx.Module.AddCopy(zeroConst, iId);
        var loopLabel = _ctx.Module.DefineLabel("__range_loop");
        var endLabel = _ctx.Module.DefineLabel("__range_end");
        _ctx.Module.MarkLabel(loopLabel);

        // i < len
        var condId = _ctx.Vars.DeclareTemp("SystemBoolean");
        _ctx.Module.AddPush(iId);
        _ctx.Module.AddPush(lenId);
        _ctx.Module.AddPush(condId);
        AddExternChecked("SystemInt32.__op_LessThan__SystemInt32_SystemInt32__SystemBoolean");
        _ctx.Module.AddPush(condId);
        _ctx.Module.AddJumpIfFalse(endLabel);

        // srcIdx = start + i
        var srcIdxId = _ctx.Vars.DeclareTemp("SystemInt32");
        _ctx.Module.AddPush(startId);
        _ctx.Module.AddPush(iId);
        _ctx.Module.AddPush(srcIdxId);
        AddExternChecked("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");

        // val = arr[srcIdx]
        var valId = _ctx.Vars.DeclareTemp(udonElemType);
        _ctx.Module.AddPush(arrayId);
        _ctx.Module.AddPush(srcIdxId);
        _ctx.Module.AddPush(valId);
        AddExternChecked($"{arrayType}.__Get__SystemInt32__{elementType}");

        // result[i] = val
        _ctx.Module.AddPush(resultId);
        _ctx.Module.AddPush(iId);
        _ctx.Module.AddPush(valId);
        AddExternChecked($"{arrayType}.__Set__SystemInt32_{elementType}__SystemVoid");

        // i++
        var oneConst = _ctx.Vars.DeclareConst("SystemInt32", "1");
        var nextId = _ctx.Vars.DeclareTemp("SystemInt32");
        _ctx.Module.AddPush(iId);
        _ctx.Module.AddPush(oneConst);
        _ctx.Module.AddPush(nextId);
        AddExternChecked("SystemInt32.__op_Addition__SystemInt32_SystemInt32__SystemInt32");
        _ctx.Module.AddCopy(nextId, iId);
        _ctx.Module.AddJump(loopLabel);
        _ctx.Module.MarkLabel(endLabel);

        return resultId;
    }
}

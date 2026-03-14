using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class ExpressionHandler : HandlerBase, IExpressionHandler
{
    public ExpressionHandler(EmitContext ctx) : base(ctx) { }

    public bool CanHandle(IOperation expression)
        => expression is ILiteralOperation
            or ILocalReferenceOperation
            or IFieldReferenceOperation
            or IParameterReferenceOperation
            or IInstanceReferenceOperation
            or IConversionOperation
            or IDefaultValueOperation
            or ITypeOfOperation
            or INameOfOperation
            or IDeclarationExpressionOperation
            or IDiscardOperation
            or IDelegateCreationOperation;

    public HExpr Handle(IOperation expression) => expression switch
    {
        ILiteralOperation op => VisitLiteral(op),
        ILocalReferenceOperation localRef => _localVarIds.TryGetValue(localRef.Local, out var capturedId)
                                                 ? LoadField(capturedId, GetUdonType(localRef.Type))
                                                 : throw new InvalidOperationException($"Cannot resolve local variable '{localRef.Local.Name}' in method '{_currentMethod?.Name ?? "(none)"}'."),
        IFieldReferenceOperation op => VisitFieldReference(op),
        IParameterReferenceOperation paramRef => LoadParam(paramRef.Parameter),
        IInstanceReferenceOperation => LoadField(_ctx.DeclareThisOnce(GetUdonType(_classSymbol)), GetUdonType(_classSymbol)),
        IConversionOperation op => VisitConversion(op),
        IDefaultValueOperation op => VisitDefaultValue(op),
        ITypeOfOperation typeOf => Const(GetUdonType(typeOf.TypeOperand), "SystemType"),
        INameOfOperation nameOf => Const(nameOf.ConstantValue.Value.ToString(), "SystemString"),
        IDeclarationExpressionOperation op => VisitDeclarationExpression(op),
        IDiscardOperation discard => SlotRef(_ctx.AllocTemp(GetUdonType(discard.Type))),
        IDelegateCreationOperation op => VisitDelegateCreation(op),
        _ => throw new NotSupportedException(expression.GetType().Name),
    };

    // ── Literal ──

    HExpr VisitLiteral(ILiteralOperation lit)
    {
        // null literal has no type
        if (lit.Type == null)
            return Const(null, "SystemObject");
        var udonType = GetUdonType(lit.Type);
        if (!lit.ConstantValue.HasValue)
            return Const(null, udonType);
        var value = lit.ConstantValue.Value;
        return Const(value, udonType);
    }

    // ── Field Reference ──

    HExpr VisitFieldReference(IFieldReferenceOperation fieldRef)
    {
        if (fieldRef.Field.HasConstantValue)
        {
            var constType = GetUdonType(fieldRef.Field.Type);
            var constVal = fieldRef.Field.ConstantValue;
            return Const(constVal, constType);
        }
        if (fieldRef.Field.IsStatic)
        {
            // UdonSharpBehaviour static field → compile error (Udon VM has no shared static storage)
            if (ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType))
                throw new NotSupportedException("Static fields are not supported on UdonSharpBehaviour types. " + $"Use 'const' for compile-time constants or convert '{fieldRef.Field.Name}' to an instance field.");
            // Unity/System static field → extern getter
            var fldType = GetUdonType(fieldRef.Field.Type);
            var containingType = GetUdonType(fieldRef.Field.ContainingType);
            return ExternCall(
                ExternResolver.BuildPropertyGetSignature(containingType, fieldRef.Field.Name, fldType),
                new List<HExpr>(),
                fldType);
        }
        // this.field → direct variable name → LoadField
        if (fieldRef.Instance is IInstanceReferenceOperation)
            return LoadField(fieldRef.Field.Name, GetUdonType(fieldRef.Field.Type));
        // cross-behaviour field → GetProgramVariable
        if (ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType))
        {
            var fldType = GetUdonType(fieldRef.Field.Type);
            var instanceVal = VisitExpression(fieldRef.Instance);
            var nameConst = Const(fieldRef.Field.Name, "SystemString");
            return ExternCall(
                "VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject",
                new List<HExpr> { instanceVal, nameConst },
                "SystemObject");
        }
        // other.field → extern getter (same pattern as VisitPropertyReference)
        {
            var fldType = GetUdonType(fieldRef.Field.Type);
            var containingType = GetUdonType(fieldRef.Field.ContainingType);
            var instanceVal = VisitExpression(fieldRef.Instance);
            return ExternCall(
                ExternResolver.BuildPropertyGetSignature(containingType, fieldRef.Field.Name, fldType),
                new List<HExpr> { instanceVal },
                fldType);
        }
    }

    // ── Conversion ──

    HExpr VisitConversion(IConversionOperation conv)
    {
        var srcVal = VisitExpression(conv.Operand);

        // Numeric conversions (int→float, etc.) via System.Convert
        if (conv.Operand.Type != null && conv.Type != null
            && ExternResolver.IsNumericType(conv.Operand.Type)
            && ExternResolver.IsNumericType(conv.Type)
            && !SymbolEqualityComparer.Default.Equals(conv.Operand.Type, conv.Type))
        {
            var methodName = ExternResolver.GetConvertMethodName(conv.Type);
            if (methodName != null)
            {
                // C# truncates float→int; SystemConvert rounds. Insert Math.Truncate first.
                if (ExternResolver.IsFloatType(conv.Operand.Type) && ExternResolver.IsIntegerType(conv.Type))
                {
                    var isDecimal = conv.Operand.Type.SpecialType == SpecialType.System_Decimal;
                    var truncType = isDecimal ? "SystemDecimal" : "SystemDouble";

                    if (!isDecimal && conv.Operand.Type.SpecialType == SpecialType.System_Single)
                    {
                        // float → double promotion
                        srcVal = ExternCall(
                            "SystemConvert.__ToDouble__SystemSingle__SystemDouble",
                            new List<HExpr> { srcVal },
                            "SystemDouble");
                    }

                    // Math.Truncate(double) or Math.Truncate(decimal)
                    srcVal = ExternCall(
                        $"SystemMath.__Truncate__{truncType}__{truncType}",
                        new List<HExpr> { srcVal },
                        truncType);

                    // Convert truncated value → target integer type
                    var dstType = GetUdonType(conv.Type);
                    return ExternCall(
                        $"SystemConvert.__{methodName}__{truncType}__{dstType}",
                        new List<HExpr> { srcVal },
                        dstType);
                }

                // Non-truncation numeric conversions (existing code)
                var srcType = GetUdonType(conv.Operand.Type);
                var dstType2 = GetUdonType(conv.Type);
                return ExternCall(
                    $"SystemConvert.__{methodName}__{srcType}__{dstType2}",
                    new List<HExpr> { srcVal },
                    dstType2);
            }
        }

        // User-defined implicit/explicit conversions (e.g. Vector2→Vector3)
        if (conv.OperatorMethod != null && conv.Operand.Type != null && conv.Type != null && !SymbolEqualityComparer.Default.Equals(conv.Operand.Type, conv.Type))
        {
            var dstType = GetUdonType(conv.Type);
            return ExternCall(
                ExternResolver.ResolveConversionExtern(
                    conv.OperatorMethod, ResolveType(conv.Operand.Type), ResolveType(conv.Type)),
                new List<HExpr> { srcVal },
                dstType);
        }

        // Enum ↔ underlying type conversions (int→enum, enum→int)
        if (conv.Operand.Type != null && conv.Type != null
                                      && !SymbolEqualityComparer.Default.Equals(conv.Operand.Type, conv.Type)
                                      && (conv.Operand.Type.TypeKind == TypeKind.Enum || conv.Type.TypeKind == TypeKind.Enum))
        {
            var dstType = GetUdonType(conv.Type);
            // Prefer const: avoids COPY type-tag corruption
            var constVal = conv.ConstantValue.HasValue ? conv.ConstantValue
                         : conv.Operand.ConstantValue.HasValue ? conv.Operand.ConstantValue
                         : default;
            if (constVal.HasValue)
                return Const(constVal.Value, dstType);

            // Runtime int→enum: use object[] array lookup to preserve type tags
            if (conv.Type.TypeKind == TypeKind.Enum && conv.Type is INamedTypeSymbol enumTarget)
            {
                var arrId = GetOrCreateEnumArray(enumTarget);
                return ExternCall(
                    "SystemObjectArray.__Get__SystemInt32__SystemObject",
                    new List<HExpr> { LoadField(arrId, "SystemObjectArray"), srcVal },
                    "SystemObject");
            }

            // enum→int: store/load through a scratch slot to re-type
            var tmpSlot = _ctx.AllocTemp(dstType);
            EmitAssign(tmpSlot, srcVal);
            return SlotRef(tmpSlot);
        }

        // Identity conversion: pass through
        return srcVal;
    }

    // ── Default Value ──

    HExpr VisitDefaultValue(IDefaultValueOperation defaultVal)
    {
        var dvType = GetUdonType(defaultVal.Type);
        if (!defaultVal.Type.IsValueType)
            return Const(null, dvType);

        var defVal = defaultVal.Type.SpecialType switch
        {
            SpecialType.System_Boolean => (object)false,
            SpecialType.System_Int32 => (object)0,
            SpecialType.System_Byte => (object)(byte)0,
            SpecialType.System_SByte => (object)(sbyte)0,
            SpecialType.System_Int16 => (object)(short)0,
            SpecialType.System_UInt16 => (object)(ushort)0,
            SpecialType.System_UInt32 => (object)0u,
            SpecialType.System_Int64 => (object)0L,
            SpecialType.System_UInt64 => (object)0UL,
            SpecialType.System_Single => (object)0f,
            SpecialType.System_Double => (object)0d,
            SpecialType.System_Char => (object)'\0',
            _ => null, // struct types (Vector3, etc.) — assembler uses default
        };
        return Const(defVal, dvType);
    }

    // ── Declaration Expression ──

    HExpr VisitDeclarationExpression(IDeclarationExpressionOperation declExpr)
    {
        if (declExpr.Expression is not ILocalReferenceOperation localRef2)
            return VisitExpression(declExpr.Expression);

        var udonType = GetUdonType(localRef2.Type);
        var localId = _ctx.DeclareLocal(localRef2.Local.Name, udonType);
        _localVarIds[localRef2.Local] = localId;
        return LoadField(localId, udonType);
    }

    // ── Delegate Creation ──

    HExpr VisitDelegateCreation(IDelegateCreationOperation op)
    {
        switch (op.Target)
        {
            case IAnonymousFunctionOperation lambda:
            {
                var hoisted = HoistLambdaToMethod(lambda);
                return FuncRef(_methodFunctions[hoisted].Name);
            }
            case IMethodReferenceOperation methodRef
                when _methodFunctions.TryGetValue(methodRef.Method, out var func):
                return FuncRef(func.Name);
            default:
                throw new NotSupportedException($"Unsupported delegate target: {op.Target.GetType().Name}");
        }
    }

}

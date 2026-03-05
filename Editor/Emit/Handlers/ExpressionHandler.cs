using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    public string Handle(IOperation expression) => expression switch
    {
        ILiteralOperation op => VisitLiteral(op),
        ILocalReferenceOperation localRef => _vars.Lookup(localRef.Local.Name) ?? (_localVarIds.TryGetValue(localRef.Local, out var capturedId) 
                                                 ? capturedId
                                                 : throw new InvalidOperationException($"Cannot resolve local variable '{localRef.Local.Name}' in method '{_currentMethod?.Name ?? "(none)"}'.")),
        IFieldReferenceOperation op => VisitFieldReference(op),
        IParameterReferenceOperation paramRef => GetParamVarId(paramRef.Parameter),
        IInstanceReferenceOperation => _vars.DeclareThisOnce(GetUdonType(_classSymbol)),
        IConversionOperation op => VisitConversion(op),
        IDefaultValueOperation op => VisitDefaultValue(op),
        ITypeOfOperation typeOf => _vars.DeclareConst("SystemType", GetUdonType(typeOf.TypeOperand)),
        INameOfOperation nameOf => _vars.DeclareConst("SystemString", nameOf.ConstantValue.Value.ToString()),
        IDeclarationExpressionOperation op => VisitDeclarationExpression(op),
        IDiscardOperation discard => _vars.DeclareTemp(GetUdonType(discard.Type)),
        IDelegateCreationOperation op => VisitDelegateCreation(op),
        _ => throw new NotSupportedException(expression.GetType().Name),
    };

    // ── Literal ──

    string VisitLiteral(ILiteralOperation lit)
    {
        // null literal has no type
        if (lit.Type == null)
            return _vars.DeclareConst("SystemObject", "null");
        var udonType = GetUdonType(lit.Type);
        var value = lit.ConstantValue.HasValue ? ToInvariantString(lit.ConstantValue.Value) : "null";
        return _vars.DeclareConst(udonType, value);
    }

    // ── Field Reference ──

    string VisitFieldReference(IFieldReferenceOperation fieldRef)
    {
        if (fieldRef.Field.HasConstantValue)
        {
            var constType = GetUdonType(fieldRef.Field.Type);
            var constVal = ToInvariantString(fieldRef.Field.ConstantValue);
            return _vars.DeclareConst(constType, constVal);
        }
        if (fieldRef.Field.IsStatic)
        {
            // UdonSharpBehaviour static field → compile error (Udon VM has no shared static storage)
            if (ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType))
                throw new NotSupportedException("Static fields are not supported on UdonSharpBehaviour types. " + $"Use 'const' for compile-time constants or convert '{fieldRef.Field.Name}' to an instance field.");
            // Unity/System static field → extern getter
            var fldType = GetUdonType(fieldRef.Field.Type);
            var tempId = ConsumeTargetHintOrTemp(fldType);
            var containingType = GetUdonType(fieldRef.Field.ContainingType);
            _module.AddPush(tempId);
            AddExternChecked(ExternResolver.BuildPropertyGetSignature(containingType, fieldRef.Field.Name, fldType));
            return tempId;
        }
        // this.field → direct variable name
        if (fieldRef.Instance is IInstanceReferenceOperation)
            return fieldRef.Field.Name;
        // cross-behaviour field → GetProgramVariable
        if (ExternResolver.IsUdonSharpBehaviour(fieldRef.Field.ContainingType))
        {
            var fldType = GetUdonType(fieldRef.Field.Type);
            var savedHint = _ctx.TargetHint;
            _ctx.TargetHint = null;
            var instanceId = VisitExpression(fieldRef.Instance);
            _ctx.TargetHint = savedHint;
            var tempId = ConsumeTargetHintOrTemp(fldType);
            var nameConst = _vars.DeclareConst("SystemString", fieldRef.Field.Name);
            _module.AddPush(instanceId);
            _module.AddPush(nameConst);
            _module.AddPush(tempId);
            AddExternChecked("VRCUdonCommonInterfacesIUdonEventReceiver.__GetProgramVariable__SystemString__SystemObject");
            return tempId;
        }
        // other.field → extern getter (same pattern as VisitPropertyReference)
        {
            var fldType = GetUdonType(fieldRef.Field.Type);
            var containingType = GetUdonType(fieldRef.Field.ContainingType);
            var savedHint = _ctx.TargetHint;
            _ctx.TargetHint = null;
            var instanceId = VisitExpression(fieldRef.Instance);
            _ctx.TargetHint = savedHint;
            var tempId = ConsumeTargetHintOrTemp(fldType);
            _module.AddPush(instanceId);
            _module.AddPush(tempId);
            AddExternChecked(ExternResolver.BuildPropertyGetSignature(containingType, fieldRef.Field.Name, fldType));
            return tempId;
        }
    }

    // ── Conversion ──

    string VisitConversion(IConversionOperation conv)
    {
        var savedHint = _ctx.TargetHint;
        _ctx.TargetHint = null;
        var srcId = VisitExpression(conv.Operand);

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
                        var promotedId = _vars.DeclareTemp("SystemDouble");
                        _module.AddPush(srcId);
                        _module.AddPush(promotedId);
                        AddExternChecked("SystemConvert.__ToDouble__SystemSingle__SystemDouble");
                        srcId = promotedId;
                    }

                    // Math.Truncate(double) or Math.Truncate(decimal)
                    var truncId = _vars.DeclareTemp(truncType);
                    _module.AddPush(srcId);
                    _module.AddPush(truncId);
                    AddExternChecked($"SystemMath.__Truncate__{truncType}__{truncType}");
                    srcId = truncId;

                    // Convert truncated value → target integer type
                    var dstType = GetUdonType(conv.Type);
                    _ctx.TargetHint = savedHint;
                    var resultId = ConsumeTargetHintOrTemp(dstType);
                    _module.AddPush(srcId);
                    _module.AddPush(resultId);
                    AddExternChecked($"SystemConvert.__{methodName}__{truncType}__{dstType}");
                    return resultId;
                }

                // Non-truncation numeric conversions (existing code)
                var srcType = GetUdonType(conv.Operand.Type);
                var dstType2 = GetUdonType(conv.Type);
                _ctx.TargetHint = savedHint;
                var resultId2 = ConsumeTargetHintOrTemp(dstType2);
                _module.AddPush(srcId);
                _module.AddPush(resultId2);
                AddExternChecked($"SystemConvert.__{methodName}__{srcType}__{dstType2}");
                return resultId2;
            }
        }

        // User-defined implicit/explicit conversions (e.g. Vector2→Vector3)
        if (conv.OperatorMethod != null && conv.Operand.Type != null && conv.Type != null && !SymbolEqualityComparer.Default.Equals(conv.Operand.Type, conv.Type))
        {
            var dstType = GetUdonType(conv.Type);
            _ctx.TargetHint = savedHint;
            var resultId = ConsumeTargetHintOrTemp(dstType);
            _module.AddPush(srcId);
            _module.AddPush(resultId);
            AddExternChecked(ExternResolver.ResolveConversionExtern(
                conv.OperatorMethod, ResolveType(conv.Operand.Type), ResolveType(conv.Type)));
            return resultId;
        }

        // Enum ↔ underlying type conversions (int→enum, enum→int)
        //
        // Udon VM heap slots carry a type tag alongside the value. COPY transfers
        // the raw bytes but also overwrites the destination's type tag with the source's.
        // For int→enum, this means the destination slot becomes tagged as "int" even
        // though the program expects "enum". Later operations that inspect the type tag
        // (e.g., serialization, SendCustomEvent argument checks) will fail.
        //
        // Workarounds:
        //   - Constants: declare a const with the correct enum type directly (no COPY needed).
        //   - Runtime: use an object[] pre-filled with enum values and index into it.
        //     The array elements already have the correct type tag.
        //   - enum→int: safe with COPY (int is the target, tag mismatch is harmless).
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
                return _vars.DeclareConst(dstType, constVal.Value?.ToString() ?? "null");

            // Runtime int→enum: use object[] array lookup to preserve type tags
            if (conv.Type.TypeKind == TypeKind.Enum && conv.Type is INamedTypeSymbol enumTarget)
            {
                var arrId = GetOrCreateEnumArray(enumTarget);
                var resultId = _vars.DeclareTemp("SystemObject");
                _module.AddPush(arrId);
                _module.AddPush(srcId);
                _module.AddPush(resultId);
                AddExternChecked("SystemObjectArray.__Get__SystemInt32__SystemObject");
                return resultId;
            }

            // enum→int: COPY is safe (same underlying type)
            var copyResult = _vars.DeclareTemp(dstType);
            _module.AddCopy(srcId, copyResult);
            return copyResult;
        }

        // Identity conversion: restore hint for caller to consume
        _ctx.TargetHint = savedHint;
        return srcId;
    }

    // ── Default Value ──

    string VisitDefaultValue(IDefaultValueOperation defaultVal)
    {
        var dvType = GetUdonType(defaultVal.Type);
        if (!defaultVal.Type.IsValueType) 
            return _vars.DeclareConst(dvType, "null");
        
        var defVal = defaultVal.Type.SpecialType switch
        {
            SpecialType.System_Boolean => "False",
            SpecialType.System_Int32 or SpecialType.System_Byte
                or SpecialType.System_SByte or SpecialType.System_Int16
                or SpecialType.System_UInt16 or SpecialType.System_UInt32
                or SpecialType.System_Int64 or SpecialType.System_UInt64 => "0",
            SpecialType.System_Single => "0",
            SpecialType.System_Double => "0",
            SpecialType.System_Char => "0",
            _ => "null", // struct types (Vector3, etc.) — assembler uses default
        };
        return _vars.DeclareConst(dvType, defVal);
    }

    // ── Declaration Expression ──

    string VisitDeclarationExpression(IDeclarationExpressionOperation declExpr)
    {
        if (declExpr.Expression is not ILocalReferenceOperation localRef2) 
            return VisitExpression(declExpr.Expression);
        
        var udonType = GetUdonType(localRef2.Type);
        var localId = _vars.DeclareLocal(localRef2.Local.Name, udonType);
        _localVarIds[localRef2.Local] = localId;
        return localId;
    }

    // ── Delegate Creation ──

    string VisitDelegateCreation(IDelegateCreationOperation op)
    {
        switch (op.Target)
        {
            case IAnonymousFunctionOperation lambda:
            {
                var hoisted = HoistLambdaToMethod(lambda);
                return _vars.DeclareConst("SystemUInt32",
                    _methodLabels[hoisted].ToString());
            }
            case IMethodReferenceOperation methodRef
                when _methodLabels.TryGetValue(methodRef.Method, out var label):
                return _vars.DeclareConst("SystemUInt32", label.ToString());
            default:
                throw new NotSupportedException($"Unsupported delegate target: {op.Target.GetType().Name}");
        }
    }

}

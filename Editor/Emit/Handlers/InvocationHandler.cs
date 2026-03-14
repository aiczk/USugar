using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public partial class InvocationHandler : HandlerBase, IExpressionHandler
{
    public InvocationHandler(EmitContext ctx) : base(ctx) { }

    public bool CanHandle(IOperation expression)
        => expression is IInvocationOperation
            or IObjectCreationOperation
            or IPropertyReferenceOperation
            or IInterpolatedStringOperation;

    public string Handle(IOperation expression) => expression switch
    {
        IInvocationOperation op => VisitInvocation(op),
        IObjectCreationOperation op => VisitObjectCreation(op),
        IPropertyReferenceOperation op => VisitPropertyReference(op),
        IInterpolatedStringOperation op => VisitInterpolatedString(op),
        _ => throw new System.NotSupportedException(expression.GetType().Name),
    };

    // ── VisitInvocation ──

    string VisitInvocation(IInvocationOperation op)
    {
        var target = op.TargetMethod;

        // Resolve type parameters in generic method type arguments (e.g., Min<T> → Min<int>)
        if (target.IsGenericMethod && _ctx.TypeParamMap != null)
        {
            var needsSub = false;
            foreach (var ta in target.TypeArguments)
            {
                if (ta is not ITypeParameterSymbol tp || !_ctx.TypeParamMap.ContainsKey(tp)) 
                    continue;
                
                needsSub = true;
                break;
            }

            if (needsSub)
            {
                var newTypeArgs = target.TypeArguments.Select(ta => ta is ITypeParameterSymbol tp2 && _ctx.TypeParamMap.TryGetValue(tp2, out var sub) ? sub : ta).ToArray();
                target = target.OriginalDefinition.Construct(newTypeArgs);
            }
        }

        switch (target.MethodKind)
        {
            // Delegate invocation: a() where a is Action/Func
            case MethodKind.DelegateInvoke:
                return VisitDelegateInvocation(op);
            // Local function call
            case MethodKind.LocalFunction
                when _ctx.MethodLabels.TryGetValue(target, out var localLabel):
                return EmitUserMethodCall(op, target, localLabel);
        }

        // User-defined generic method → monomorphize
        if (target.IsGenericMethod && SymbolEqualityComparer.Default.Equals(target.OriginalDefinition.ContainingType, _ctx.ClassSymbol))
        {
            RegisterGenericSpecialization(target);
            var specLabel = _ctx.MethodLabels[target];
            return EmitUserMethodCall(op, target, specLabel);
        }

        // User-defined method in the same class
        if (SymbolEqualityComparer.Default.Equals(target.ContainingType, _ctx.ClassSymbol) && _ctx.MethodLabels.TryGetValue(target, out var targetLabel))
        {
            return EmitUserMethodCall(op, target, targetLabel);
        }

        // Base class instance method (emitted locally)
        if (_ctx.MethodLabels.TryGetValue(target, out var baseLabel) && IsBaseInstanceMethod(target))
            return EmitUserMethodCall(op, target, baseLabel);

        // Generic foreign static method → monomorphize and emit as JUMP call
        if (target.IsGenericMethod && IsForeignStatic(target))
        {
            var constructed = target.ReducedFrom != null
                ? target.ReducedFrom.OriginalDefinition.Construct(target.TypeArguments.ToArray())
                : target.OriginalDefinition.Construct(target.TypeArguments.ToArray());
            RegisterGenericSpecialization(constructed);
            var specLabel = _ctx.MethodLabels[constructed];
            var specParamIds = _ctx.MethodParamVarIds[constructed];
            var savedCurrentParams = SaveMethodParameterState(_ctx.CurrentMethod);
            var paramOffset = 0;
            if (target.ReducedFrom != null && op.Instance != null)
            {
                CopyArgumentToMethodParameter(constructed, 0, op.Instance, specParamIds[0]);
                paramOffset = 1;
            }
            for (var i = 0; i < op.Arguments.Length; i++)
                CopyArgumentToMethodParameter(constructed, i + paramOffset, op.Arguments[i].Value, specParamIds[i + paramOffset]);
            var result = EmitCallByLabel(constructed, specLabel);
            RestoreMethodParameterState(savedCurrentParams);
            return result;
        }

        // Foreign static method → inlined as JUMP call (resolve extension method original form)
        {
            var original = target.ReducedFrom ?? target;
            if (IsForeignStatic(target) && _ctx.MethodLabels.TryGetValue(original, out var foreignLabel))
            {
                var origParamIds = _ctx.MethodParamVarIds[original];
                var savedCurrentParams = SaveMethodParameterState(_ctx.CurrentMethod);
                var paramOffset = 0;
                // Extension method: instance is the first (this) parameter
                if (target.ReducedFrom != null && op.Instance != null)
                {
                    CopyArgumentToMethodParameter(original, 0, op.Instance, origParamIds[0]);
                    paramOffset = 1;
                }
                for (var i = 0; i < op.Arguments.Length; i++)
                    CopyArgumentToMethodParameter(original, i + paramOffset, op.Arguments[i].Value, origParamIds[i + paramOffset]);
                var result = EmitCallByLabel(original, foreignLabel);
                RestoreMethodParameterState(savedCurrentParams);
                return result;
            }
        }

        // Cross-class UdonSharpBehaviour call → SetProgramVariable + SendCustomEvent
        // Only for calls on other instances (fields), not on 'this' (base class methods like RequestSerialization).
        // Exclude methods declared on UdonSharpBehaviour itself (SendCustomEvent, SetProgramVariable, etc.)
        // — those are Udon VM interface methods that must be compiled as externs.
        if (ExternResolver.IsUdonSharpBehaviour(target.ContainingType)
            && op.Instance is not IInstanceReferenceOperation
            && target.ContainingType.Name != "UdonSharpBehaviour")
            return EmitCrossClassCall(op, target);

        // Interface method call → SendCustomEvent dispatch
        // Skip when instance is a type parameter resolved to a concrete non-UdonBehaviour type
        // (e.g., IComparable<T>.CompareTo with T=int → use extern, not SendCustomEvent)
        if (target.ContainingType.TypeKind == TypeKind.Interface
            && op.Instance != null
            && !IsResolvedConcreteNonBehaviour(op.Instance?.Type))
            return EmitInterfaceCall(op, target);

        // Virtual methods on UdonSharpBehaviour (OnDeserialization, Interact, etc.)
        // have no Udon VM implementation. base.X() or direct calls should be no-op.
        if (target.ContainingType.Name == "UdonSharpBehaviour"
            && (target.IsVirtual || target.IsOverride || target.IsAbstract))
            return null;

        // Extern method call
        return EmitExternMethodCall(op, target);
    }

    // ── Delegate Invocation ──

    string VisitDelegateInvocation(IInvocationOperation op)
    {
        // Delegate parameter invocation via JUMP_INDIRECT
        if (op.Instance is IParameterReferenceOperation paramRef2
            && _ctx.CurrentMethod != null
            && _ctx.MethodIndices.TryGetValue(_ctx.CurrentMethod, out var currentIdx)
            && _ctx.DelegateParamConventions.TryGetValue((currentIdx, paramRef2.Parameter.Ordinal), out var convention))
        {
            // Copy args to convention vars
            for (int i = 0; i < op.Arguments.Length; i++)
            {
                var argId = VisitExpression(op.Arguments[i].Value);
                _ctx.Module.AddCopy(argId, convention.ArgVarIds[i]);
            }

            // Get the label var (param var holding the lambda's address)
            var labelVarId = GetParamVarId(paramRef2.Parameter);

            // Stack-based: push return addr onto VM stack, JUMP_INDIRECT to delegate
            var returnLabel = _ctx.Module.DefineLabel("__dlg_return");
            _ctx.Module.AddPushLabel(returnLabel);
            _ctx.Module.AddJumpIndirect(labelVarId);
            _ctx.Module.MarkLabel(returnLabel);

            return convention.RetVarId;
        }

        // op.Instance is the delegate local reference (e.g., 'a' in a())
        if (op.Instance is ILocalReferenceOperation localRef
            && _ctx.DelegateVarMap.TryGetValue(localRef.Local, out var targetMethod))
        {
            var label = _ctx.MethodLabels[targetMethod];
            var targetParamIds = _ctx.MethodParamVarIds[targetMethod];

            for (int i = 0; i < op.Arguments.Length; i++)
            {
                var argId = VisitExpression(op.Arguments[i].Value);
                _ctx.Module.AddCopy(argId, targetParamIds[i]);
            }
            return EmitCallByLabel(targetMethod, label);
        }
        throw new System.NotSupportedException("Cannot resolve delegate target");
    }

    // ── Generic Monomorphization ──

    void RegisterGenericSpecialization(IMethodSymbol constructed)
    {
        if (_ctx.MethodLabels.ContainsKey(constructed)) return;

        var idx = _ctx.NextMethodIndex++;
        _ctx.MethodIndices[constructed] = idx;
        _ctx.MethodVarPrefix[constructed] = idx.ToString();

        var typeArgPart = string.Join("_", constructed.TypeArguments.Select(ExternResolver.GetUdonTypeName));
        var name = $"__{idx}_{SanitizeId(constructed.Name)}_{typeArgPart}";
        var label = _ctx.Module.DefineLabel(name);
        _ctx.MethodLabels[constructed] = label;

        var gsParamIds = new string[constructed.Parameters.Length];
        for (int pi = 0; pi < constructed.Parameters.Length; pi++)
        {
            var param = constructed.Parameters[pi];
            if (param.Type.IsTupleType && param.Type is INamedTypeSymbol tupleParamType)
            {
                var elements = tupleParamType.TupleElements;
                var tupleParamIds = new string[elements.Length];
                for (int ei = 0; ei < elements.Length; ei++)
                {
                    var elemType = GetUdonType(elements[ei].Type);
                    var tupleParamId = $"__{idx}_{param.Name}__param_{ei}";
                    _ctx.Vars.DeclareVar(tupleParamId, elemType);
                    tupleParamIds[ei] = tupleParamId;
                }
                _ctx.MethodTupleParamVarIds[(constructed, pi)] = tupleParamIds;
                continue;
            }

            var isDelegateParam = param.Type is INamedTypeSymbol nt2 && nt2.DelegateInvokeMethod != null;
            var udonType = isDelegateParam ? "SystemUInt32" : GetUdonType(param.Type);
            var paramId = $"__{idx}_{param.Name}__param";
            _ctx.Vars.DeclareVar(paramId, udonType);
            gsParamIds[pi] = paramId;
        }
        _ctx.MethodParamVarIds[constructed] = gsParamIds;

        if (!constructed.ReturnsVoid)
        {
            if (constructed.ReturnType.IsTupleType && constructed.ReturnType is INamedTypeSymbol tupleType)
            {
                var elements = tupleType.TupleElements;
                var tupleRetIds = new string[elements.Length];
                for (int ei = 0; ei < elements.Length; ei++)
                {
                    var elemType = GetUdonType(elements[ei].Type);
                    var retId = $"__{idx}_{SanitizeId(constructed.Name)}__ret_{ei}";
                    _ctx.Vars.DeclareVar(retId, elemType);
                    tupleRetIds[ei] = retId;
                }
                _ctx.MethodTupleRetVars[constructed] = tupleRetIds;
            }
            else
            {
                var retType = GetUdonType(constructed.ReturnType);
                var retId = $"__{idx}_{SanitizeId(constructed.Name)}__ret";
                _ctx.Vars.DeclareVar(retId, retType);
                _ctx.MethodRetVars[constructed] = retId;
                _ctx.MethodRetTypes[constructed] = retType;
            }
        }

        _ctx.PendingGenericSpecs.Add(constructed);
        DeclareDelegateConventionVars(constructed, idx);
    }

    void DeclareDelegateConventionVars(IMethodSymbol method, int idx)
    {
        foreach (var param in method.Parameters)
        {
            if (param.Type is not INamedTypeSymbol namedType || namedType.DelegateInvokeMethod == null)
                continue;

            var invoke = namedType.DelegateInvokeMethod;
            var argVarIds = new string[invoke.Parameters.Length];
            for (int j = 0; j < invoke.Parameters.Length; j++)
            {
                var argType = GetUdonType(invoke.Parameters[j].Type);
                argVarIds[j] = _ctx.Vars.DeclareVar($"__dlg_{idx}_{param.Name}_a{j}", argType);
            }
            string retVarId = null;
            if (!invoke.ReturnsVoid)
            {
                var retType = GetUdonType(invoke.ReturnType);
                retVarId = _ctx.Vars.DeclareVar($"__dlg_{idx}_{param.Name}_ret", retType);
            }
            _ctx.DelegateParamConventions[(idx, param.Ordinal)] = new DelegateConvention
            {
                ArgVarIds = argVarIds, RetVarId = retVarId
            };
        }
    }

    // ── Lambda / Delegate Helpers ──

    static bool UnwrapLambdaFromArg(IOperation op, out IAnonymousFunctionOperation lambda)
    {
        while (true)
        {
            lambda = null;
            if (op is IDelegateCreationOperation { Target: IAnonymousFunctionOperation l })
            {
                lambda = l;
                return true;
            }

            if (op is not IConversionOperation conv) 
                return false;
            op = conv.Operand;
        }
    }

    void HoistLambdaForDelegateParam(IAnonymousFunctionOperation lambda, DelegateConvention convention)
    {
        var symbol = lambda.Symbol;
        if (_ctx.MethodLabels.ContainsKey(symbol)) return;

        var idx = _ctx.NextMethodIndex++;
        _ctx.MethodIndices[symbol] = idx;
        _ctx.MethodVarPrefix[symbol] = idx.ToString();
        var label = _ctx.Module.DefineLabel($"__{idx}_lambda");
        _ctx.MethodLabels[symbol] = label;

        // Convention vars are used instead of standard param/ret vars.
        // Store convention arg var IDs as param IDs for consistency.
        _ctx.MethodParamVarIds[symbol] = convention.ArgVarIds ?? System.Array.Empty<string>();

        // Override: use convention vars instead of standard param/ret vars
        _ctx.LambdaConventionOverrides[symbol] = convention;
        if (convention.RetVarId != null)
        {
            _ctx.MethodRetVars[symbol] = convention.RetVarId;
            _ctx.MethodRetTypes[symbol] = _ctx.Vars.GetDeclaredType(convention.RetVarId);
        }

        _ctx.PendingLocalFunctions.Add((symbol, label));
    }

    // ── Classification helpers ──

    bool IsForeignStatic(IMethodSymbol method) => _ctx.IsForeignStatic(method);
    bool IsBaseInstanceMethod(IMethodSymbol method) => _ctx.IsBaseInstanceMethod(method);

    bool IsResolvedConcreteNonBehaviour(ITypeSymbol type)
    {
        switch (type)
        {
            case null:
            // Type parameter: resolve via TypeParamMap
            case ITypeParameterSymbol when _ctx.TypeParamMap == null:
                return false;
            case ITypeParameterSymbol tp:
            {
                if (!_ctx.TypeParamMap.TryGetValue(tp, out var concrete)) return false;
                return !ExternResolver.IsUdonSharpBehaviour(concrete);
            }
        }

        // Concrete type: if not a UdonSharpBehaviour, interface calls should use extern
        if (type.TypeKind == TypeKind.Interface) return false; // can't determine yet
        return !ExternResolver.IsUdonSharpBehaviour(type);
    }

}

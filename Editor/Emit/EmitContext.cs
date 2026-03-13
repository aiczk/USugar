using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public struct DelegateConvention
{
    public string[] ArgVarIds;
    public string RetVarId; // null for void delegates
}

public struct EmitDiagnostic
{
    public string Severity; // "Warning" or "Error"
    public string Message;
    public string FilePath;
    public int Line;
    public int Character;
}

public class EmitContext
{
    // Core dependencies
    public readonly Compilation Compilation;
    public readonly INamedTypeSymbol ClassSymbol;
    public readonly UasmModule Module;
    public readonly VariableTable Vars;
    public readonly LayoutPlanner Planner;

    // Method bookkeeping — populated by RegisterMethod():
    //   MethodLabels:      method → UASM label index (entry point for JUMP)
    //   MethodIndices:     method → sequential index (used in variable naming: __{idx}_{name})
    //   MethodVarPrefix:   method → index string (prefix for param/ret variable IDs)
    //   MethodRetVars:     method → return variable ID (null for void)
    //   MethodRetTypes:    method → return Udon type name
    //   MethodParamVarIds: method → ordered array of parameter variable IDs
    public readonly Dictionary<IMethodSymbol, int> MethodLabels = new(SymbolEqualityComparer.Default);
    public readonly Dictionary<IMethodSymbol, int> MethodIndices = new(SymbolEqualityComparer.Default);
    public readonly Dictionary<IMethodSymbol, string> MethodVarPrefix = new(SymbolEqualityComparer.Default);
    public readonly Dictionary<IMethodSymbol, string> MethodRetVars = new(SymbolEqualityComparer.Default);
    public readonly Dictionary<IMethodSymbol, string> MethodRetTypes = new(SymbolEqualityComparer.Default);
    public readonly Dictionary<IMethodSymbol, string[]> MethodParamVarIds = new(SymbolEqualityComparer.Default);
    public IMethodSymbol CurrentMethod;
    public int NextMethodIndex;
    public readonly List<(IMethodSymbol symbol, int label)> PendingLocalFunctions = new();
    public readonly Dictionary<ILocalSymbol, IMethodSymbol> DelegateVarMap = new(SymbolEqualityComparer.Default);

    // Generic monomorphization: pending specialized method bodies to emit
    public readonly List<IMethodSymbol> PendingGenericSpecs = new();

    // Type parameter substitution map for generic monomorphization (instance-scoped for thread safety)
    public Dictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap;

    // Delegate calling convention:
    //   DelegateParamConventions: (methodIdx, paramOrdinal) → shared arg/ret variable IDs.
    //     Used when a method accepts a delegate parameter — caller writes args here, callee reads them.
    //   LambdaConventionOverrides: lambda method → convention to use instead of normal param vars.
    //     Set when a lambda is passed as an argument, so the lambda body reads from the shared vars.
    public readonly Dictionary<(int methodIdx, int paramOrdinal), DelegateConvention> DelegateParamConventions = new();
    public readonly Dictionary<IMethodSymbol, DelegateConvention> LambdaConventionOverrides = new(SymbolEqualityComparer.Default);

    // Body labels for exported methods (skip re-entrance preamble on internal JUMP calls)
    public readonly Dictionary<IMethodSymbol, int> MethodBodyLabels = new(SymbolEqualityComparer.Default);

    // Persistent local symbol → variable ID mapping (survives scope pop, for capture resolution)
    public readonly Dictionary<ILocalSymbol, string> LocalVarIds = new(SymbolEqualityComparer.Default);

    // Field initializers to emit at _start
    public readonly List<(string fieldId, IOperation initOp, ITypeSymbol fieldType)> FieldInitOps = new();

    // FieldChangeCallback: fieldName → propertyName
    public readonly Dictionary<string, string> FieldChangeCallbacks = new();

    // Enum array lookup: enum type → object[] variable ID for int→enum runtime conversions
    public readonly Dictionary<ITypeSymbol, string> EnumArrayVars = new(SymbolEqualityComparer.Default);

    // Conditional access target stack (for ?. operator)
    public readonly Stack<string> ConditionalAccessTargets = new();

    // Loop break/continue label stacks
    public readonly Stack<int> BreakLabels = new();
    public readonly Stack<int> ContinueLabels = new();

    // goto label mapping (per method, cleared on each EmitMethod)
    public readonly Dictionary<ILabelSymbol, int> GotoLabels = new(SymbolEqualityComparer.Default);

    // using declaration Dispose tracking (per block scope)
    public readonly Stack<List<(string varId, ITypeSymbol type)>> UsingDisposableStack = new();


    // Target hint for direct assignment optimization: when an extern result
    // is assigned to a simple variable, the extern can write directly to it.
    public string TargetHint;

    // Diagnostics collected during emission
    public readonly List<EmitDiagnostic> Diagnostics = new();
    public readonly HashSet<string> ReportedExterns = new();

    // Dispatch delegates (initialized by UasmEmitter via InitializeDispatchers)
    Action<IOperation> _visitOperation;
    Func<IOperation, string> _visitExpression;
    Func<string, ITypeSymbol, IPatternOperation, string> _emitPatternCheck;

    public Action<IOperation> VisitOperation => _visitOperation
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");
    public Func<IOperation, string> VisitExpression => _visitExpression
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");
    public Func<string, ITypeSymbol, IPatternOperation, string> EmitPatternCheck => _emitPatternCheck
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");

    public void InitializeDispatchers(
        Action<IOperation> visitOp,
        Func<IOperation, string> visitExpr,
        Func<string, ITypeSymbol, IPatternOperation, string> emitPattern)
    {
        _visitOperation = visitOp ?? throw new ArgumentNullException(nameof(visitOp));
        _visitExpression = visitExpr ?? throw new ArgumentNullException(nameof(visitExpr));
        _emitPatternCheck = emitPattern ?? throw new ArgumentNullException(nameof(emitPattern));
    }

    public EmitContext(Compilation compilation, INamedTypeSymbol classSymbol, LayoutPlanner planner)
    {
        Compilation = compilation;
        ClassSymbol = classSymbol;
        Module = new UasmModule();
        Vars = new VariableTable();
        Planner = planner;
    }

    // ── Method Registration ──

    /// <summary>
    /// Registers a method (local function, foreign static, or base instance) with label, params, and return var.
    /// Returns the assigned method index. Idempotent — returns existing index if already registered.
    /// </summary>
    public int RegisterMethod(IMethodSymbol method, string nameHint = null)
    {
        if (MethodLabels.ContainsKey(method))
            return MethodIndices[method];

        var idx = NextMethodIndex++;
        MethodIndices[method] = idx;
        MethodVarPrefix[method] = idx.ToString();
        var name = nameHint ?? method.Name.Replace('.', '_');
        if (string.IsNullOrEmpty(name)) name = "lambda";
        var label = Module.DefineLabel($"__{idx}_{name}");
        MethodLabels[method] = label;

        var paramIds = new string[method.Parameters.Length];
        for (int pi = 0; pi < method.Parameters.Length; pi++)
        {
            var param = method.Parameters[pi];
            var isDlg = param.Type is INamedTypeSymbol nt && nt.DelegateInvokeMethod != null;
            var udonType = isDlg ? "SystemUInt32" : ExternResolver.GetUdonTypeName(param.Type, TypeParamMap);
            var paramId = $"__{idx}_{param.Name}__param";
            Vars.DeclareVar(paramId, udonType);
            paramIds[pi] = paramId;
        }
        MethodParamVarIds[method] = paramIds;

        if (!method.ReturnsVoid)
        {
            var retType = ExternResolver.GetUdonTypeName(method.ReturnType, TypeParamMap);
            var retId = $"__{idx}_{name}__ret";
            Vars.DeclareVar(retId, retType);
            MethodRetVars[method] = retId;
            MethodRetTypes[method] = retType;
        }

        return idx;
    }

    // ── Extern Resolution ──

    // Known base type chains for extern fallback resolution.
    // When a specific type's extern isn't registered, try its base types.
    static readonly string[] FallbackBaseTypes = new[]
    {
        "UnityEngineComponent", "UnityEngineBehaviour",
        "UnityEngineMonoBehaviour", "UnityEngineObject",
    };

    public static string ResolveExtern(string externSig)
    {
        if (ExternResolver.IsExternValid == null || ExternResolver.IsExternValid(externSig))
            return externSig;

        var dotIdx = externSig.IndexOf(".__");
        if (dotIdx < 0) return externSig;
        var containingType = externSig.Substring(0, dotIdx);
        var rest = externSig.Substring(dotIdx);

        foreach (var baseType in FallbackBaseTypes)
        {
            if (baseType == containingType) continue;
            var alt = baseType + rest;
            if (ExternResolver.IsExternValid(alt))
                return alt;
        }

        return externSig;
    }

    public void AddExternChecked(string externSig)
    {
        Module.AddExtern(ResolveExtern(externSig));
    }

    // ── Classification helpers (shared between UasmEmitter and handlers) ──

    public bool IsForeignStatic(IMethodSymbol method)
    {
        var resolved = method.ReducedFrom ?? method;
        if (!resolved.IsStatic) return false;
        if (resolved.ContainingType.DeclaringSyntaxReferences.Length == 0) return false;
        if (ExternResolver.IsUdonSharpBehaviour(resolved.ContainingType)) return false;
        if (SymbolEqualityComparer.Default.Equals(resolved.ContainingType, ClassSymbol)) return false;
        if (USugarCompilerHelper.IsExternNamespace(resolved.ContainingType.ContainingNamespace)) return false;
        return true;
    }

    public bool IsBaseInstanceMethod(IMethodSymbol method)
    {
        if (method.IsStatic) return false;
        if (method.ContainingType.DeclaringSyntaxReferences.Length == 0) return false;
        if (SymbolEqualityComparer.Default.Equals(method.ContainingType, ClassSymbol)) return false;
        if (USugarCompilerHelper.IsFrameworkNamespace(method.ContainingType.ContainingNamespace)) return false;
        if (method.ContainingType.Name == "UdonSharpBehaviour") return false;
        var bt = ClassSymbol.BaseType;
        while (bt != null)
        {
            if (SymbolEqualityComparer.Default.Equals(bt, method.ContainingType)) return true;
            bt = bt.BaseType;
        }
        return false;
    }
}

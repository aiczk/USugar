using System;
using System.Collections.Generic;
using System.Linq;
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
    public readonly HModule HirModule;
    public readonly HirBuilder Builder;
    public readonly LayoutPlanner Planner;

    // Method bookkeeping
    public readonly Dictionary<IMethodSymbol, HFunction> MethodFunctions = new(SymbolEqualityComparer.Default);
    public readonly Dictionary<IMethodSymbol, int> MethodIndices = new(SymbolEqualityComparer.Default);
    public readonly Dictionary<IMethodSymbol, string> MethodVarPrefix = new(SymbolEqualityComparer.Default);
    public readonly Dictionary<IMethodSymbol, string> MethodRetVars = new(SymbolEqualityComparer.Default);
    public readonly Dictionary<IMethodSymbol, string> MethodRetTypes = new(SymbolEqualityComparer.Default);
    public readonly Dictionary<IMethodSymbol, string[]> MethodParamVarIds = new(SymbolEqualityComparer.Default);
    public IMethodSymbol CurrentMethod;
    public int NextMethodIndex;
    public readonly List<(IMethodSymbol symbol, HFunction func)> PendingLocalFunctions = new();
    public readonly Dictionary<ILocalSymbol, IMethodSymbol> DelegateVarMap = new(SymbolEqualityComparer.Default);

    // Generic monomorphization
    public readonly List<IMethodSymbol> PendingGenericSpecs = new();
    public Dictionary<ITypeParameterSymbol, ITypeSymbol> TypeParamMap;

    // Delegate parameter convention variables
    public readonly Dictionary<(int methodIdx, int paramOrdinal), DelegateConvention> DelegateParamConventions = new();
    public readonly Dictionary<IMethodSymbol, DelegateConvention> LambdaConventionOverrides = new(SymbolEqualityComparer.Default);

    // Persistent local symbol → field name mapping (survives scope pop, for capture resolution)
    public readonly Dictionary<ILocalSymbol, string> LocalVarIds = new(SymbolEqualityComparer.Default);

    // Field initializers to emit at _start
    public readonly List<(string fieldName, IOperation initOp, ITypeSymbol fieldType)> FieldInitOps = new();

    // FieldChangeCallback: fieldName → propertyName
    public readonly Dictionary<string, string> FieldChangeCallbacks = new();

    // Enum array lookup: enum type → field name for int→enum runtime conversions
    public readonly Dictionary<ITypeSymbol, string> EnumArrayVars = new(SymbolEqualityComparer.Default);

    // Conditional access target stack (for ?. operator)
    public readonly Stack<HExpr> ConditionalAccessTargets = new();

    // using declaration Dispose tracking
    public readonly Stack<List<(HExpr val, ITypeSymbol type)>> UsingDisposableStack = new();

    // Diagnostics collected during emission
    public readonly List<EmitDiagnostic> Diagnostics = new();
    public readonly HashSet<string> ReportedExterns = new();

    // Dispatch delegates (HIR-based)
    Action<IOperation> _visitOperation;
    Func<IOperation, HExpr> _visitExpression;
    Func<HExpr, ITypeSymbol, IPatternOperation, HExpr> _emitPatternCheck;

    public Action<IOperation> VisitOperation => _visitOperation
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");
    public Func<IOperation, HExpr> VisitExpression => _visitExpression
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");
    public Func<HExpr, ITypeSymbol, IPatternOperation, HExpr> EmitPatternCheck => _emitPatternCheck
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");

    public void InitializeDispatchers(
        Action<IOperation> visitOp,
        Func<IOperation, HExpr> visitExpr,
        Func<HExpr, ITypeSymbol, IPatternOperation, HExpr> emitPattern)
    {
        _visitOperation = visitOp ?? throw new ArgumentNullException(nameof(visitOp));
        _visitExpression = visitExpr ?? throw new ArgumentNullException(nameof(visitExpr));
        _emitPatternCheck = emitPattern ?? throw new ArgumentNullException(nameof(emitPattern));
    }

    public EmitContext(Compilation compilation, INamedTypeSymbol classSymbol, LayoutPlanner planner)
    {
        Compilation = compilation;
        ClassSymbol = classSymbol;
        HirModule = new HModule { ClassName = classSymbol.ToDisplayString() };
        Builder = new HirBuilder(HirModule);
        Planner = planner;
    }

    // ══════════════════════════════════════════════════════════════════
    // Variable naming utilities (replaces VariableTable)
    // ══════════════════════════════════════════════════════════════════

    readonly Dictionary<string, int> _counters = new();
    readonly HashSet<string> _declaredFieldNames = new();
    readonly Dictionary<string, string> _thisVars = new();
    readonly Dictionary<string, string> _structConstIds = new();

    int NextIndex(string key)
    {
        _counters.TryGetValue(key, out var n);
        _counters[key] = n + 1;
        return n;
    }

    /// <summary>Declare a field in HirModule. Idempotent — returns existing name if already declared.</summary>
    public string DeclareField(string name, string type, FieldFlags flags = FieldFlags.None,
        object defaultValue = null, string syncMode = null)
    {
        if (_declaredFieldNames.Contains(name)) return name;
        var field = new FieldDecl(name, type) { Flags = flags, DefaultValue = defaultValue, SyncMode = syncMode };
        HirModule.Fields.Add(field);
        _declaredFieldNames.Add(name);
        return name;
    }

    /// <summary>Declare a named variable field. Idempotent.</summary>
    public string DeclareVar(string id, string type)
    {
        if (_declaredFieldNames.Contains(id)) return id;
        HirModule.Fields.Add(new FieldDecl(id, type));
        _declaredFieldNames.Add(id);
        return id;
    }

    /// <summary>Try to declare a variable. Returns true if newly declared.</summary>
    public bool TryDeclareVar(string id, string type)
    {
        if (_declaredFieldNames.Contains(id)) return false;
        HirModule.Fields.Add(new FieldDecl(id, type));
        _declaredFieldNames.Add(id);
        return true;
    }

    /// <summary>Declare a local variable with unique field name.</summary>
    public string DeclareLocal(string name, string type)
    {
        var idx = NextIndex($"lcl_{name}_{type}");
        var id = $"__lcl_{name}_{type}_{idx}";
        HirModule.Fields.Add(new FieldDecl(id, type));
        _declaredFieldNames.Add(id);
        return id;
    }

    /// <summary>Declare a "this" reference field with type remapping for Udon heap.</summary>
    public string DeclareThis(string udonType)
    {
        var heapType = SupportedThisTypes.Contains(udonType) ? udonType : "VRCUdonUdonBehaviour";
        var idx = NextIndex($"this_{heapType}");
        var id = $"__this_{heapType}_{idx}";
        HirModule.Fields.Add(new FieldDecl(id, heapType) { DefaultValue = "this" });
        _declaredFieldNames.Add(id);
        return id;
    }

    /// <summary>Declare or reuse a "this" reference for the given type.</summary>
    public string DeclareThisOnce(string udonType)
    {
        if (_thisVars.TryGetValue(udonType, out var existing)) return existing;
        var id = DeclareThis(udonType);
        _thisVars[udonType] = id;
        return id;
    }

    static readonly HashSet<string> SupportedThisTypes = new()
    {
        "UnityEngineGameObject", "UnityEngineTransform", "VRCUdonUdonBehaviour",
    };

    /// <summary>Declare an enum array field with const value.</summary>
    public string DeclareEnumArray(string id, object[] values)
    {
        if (_declaredFieldNames.Contains(id)) return id;
        HirModule.Fields.Add(new FieldDecl(id, "SystemObjectArray") { DefaultValue = values });
        _declaredFieldNames.Add(id);
        return id;
    }

    /// <summary>Declare reflection type IDs array.</summary>
    public void DeclareReflTypeIds(long[] typeIds)
    {
        DeclareField("__refl_typeids", "SystemInt64Array", defaultValue: typeIds);
    }

    /// <summary>Set const value on an existing field.</summary>
    public void SetFieldConstValue(string name, object value)
    {
        var field = HirModule.Fields.FirstOrDefault(f => f.Name == name);
        if (field != null) field.DefaultValue = value;
    }

    /// <summary>Check if a field name has been declared.</summary>
    public bool IsFieldDeclared(string name) => _declaredFieldNames.Contains(name);

    /// <summary>Declare a temporary variable field.</summary>
    public string DeclareTemp(string type)
    {
        return DeclareLocal("tmp", type);
    }

    /// <summary>Declare a struct constant field with deduplication (e.g., Vector3.zero).</summary>
    public string DeclareStructConst(string type, object value)
    {
        var key = $"{type}_{value}";
        if (_structConstIds.TryGetValue(key, out var existing)) return existing;
        var idx = NextIndex($"structconst_{type}");
        var id = $"__const_{type}_{idx}";
        HirModule.Fields.Add(new FieldDecl(id, type) { DefaultValue = value });
        _declaredFieldNames.Add(id);
        _structConstIds[key] = id;
        return id;
    }

    /// <summary>Get the Udon type of a declared field by its ID.</summary>
    public string GetFieldType(string id)
    {
        return HirModule.Fields.FirstOrDefault(f => f.Name == id)?.Type;
    }

    // ── Constant parsing (moved from VariableTable) ──

    /// <summary>Parse a string constant value to a typed CLR object.</summary>
    public static object ParseConstValue(string udonType, string value)
    {
        if (value == "null") return null;
        return udonType switch
        {
            "SystemInt32" => value.StartsWith("0x") ? Convert.ToInt32(value, 16) : int.Parse(value),
            "SystemUInt32" => value.StartsWith("0x") ? Convert.ToUInt32(value, 16) : uint.Parse(value),
            "SystemInt64" => long.Parse(value),
            "SystemUInt64" => ulong.Parse(value),
            "SystemInt16" => short.Parse(value),
            "SystemUInt16" => ushort.Parse(value),
            "SystemSByte" => sbyte.Parse(value),
            "SystemSingle" => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            "SystemDouble" => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            "SystemBoolean" => bool.Parse(value),
            "SystemString" => value,
            "SystemByte" => byte.Parse(value),
            "SystemChar" => value[0],
            "SystemType" => value, // Udon type name, resolved to CLR Type at apply time
            _ => long.TryParse(value, out var longVal)
                ? (longVal is >= int.MinValue and <= int.MaxValue ? (object)(int)longVal : longVal)
                : ulong.TryParse(value, out var ulongVal) ? (object)ulongVal : null,
        };
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class AggregateLayout
{
    public readonly struct FieldInfo
    {
        public readonly string Name;
        public readonly int Index;
        public readonly ITypeSymbol Type;
        public FieldInfo(string name, int index, ITypeSymbol type)
        { Name = name; Index = index; Type = type; }
    }

    public readonly IReadOnlyList<FieldInfo> Fields;
    readonly Dictionary<string, int> _nameToIndex;

    public int Count => Fields.Count;

    public bool TryGetIndex(string fieldName, out int index)
        => _nameToIndex.TryGetValue(fieldName, out index);

    public bool TryGetIndex(IFieldSymbol field, out int index)
    {
        if (_nameToIndex.TryGetValue(field.Name, out index)) return true;
        if (field.CorrespondingTupleField != null
            && _nameToIndex.TryGetValue(field.CorrespondingTupleField.Name, out index)) return true;
        // Reverse: check if any layout field's CorrespondingTupleField matches
        return false;
    }

    AggregateLayout(IReadOnlyList<FieldInfo> fields, Dictionary<string, int> nameToIndex)
    { Fields = fields; _nameToIndex = nameToIndex; }

    public static AggregateLayout Build(INamedTypeSymbol type)
    {
        if (!type.IsTupleType)
            throw new InvalidOperationException(
                $"AggregateLayout.Build called on non-tuple type '{type.Name}'");

        var fields = new List<FieldInfo>();
        var nameToIndex = new Dictionary<string, int>();

        var elements = type.TupleElements;
        for (int i = 0; i < elements.Length; i++)
        {
            var name = elements[i].Name;
            fields.Add(new FieldInfo(name, i, elements[i].Type));
            nameToIndex[name] = i;
            var itemName = $"Item{i + 1}";
            if (name != itemName) nameToIndex[itemName] = i;
            // Also map CorrespondingTupleField name if different
            if (elements[i].CorrespondingTupleField != null)
            {
                var corrName = elements[i].CorrespondingTupleField.Name;
                if (!nameToIndex.ContainsKey(corrName)) nameToIndex[corrName] = i;
            }
        }

        return new AggregateLayout(fields.AsReadOnly(), nameToIndex);
    }
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
    public readonly struct MethodSlot
    {
        public readonly int Index;
        public readonly string VarPrefix;
        public MethodSlot(int index, string varPrefix) { Index = index; VarPrefix = varPrefix; }
    }

    public readonly Dictionary<IMethodSymbol, MethodSlot> MethodSlots = new(SymbolEqualityComparer.Default);

    public MethodSlot RegisterMethod(IMethodSymbol method, Func<int, string> prefixFactory)
    {
        var idx = NextMethodIndex++;
        var slot = new MethodSlot(idx, prefixFactory(idx));
        MethodSlots[method] = slot;
        return slot;
    }
    /// <summary>Per-method return slots. Empty array for void. Length 1 for scalar. Length N for tuple.</summary>
    public readonly Dictionary<IMethodSymbol, ReturnSlot[]> MethodReturns = new(SymbolEqualityComparer.Default);
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

    // Persistent local symbol → field name mapping (survives scope pop, for capture resolution).
    //
    // KNOWN LIMITATION (v2.2): All lambdas within the same UdonSharpBehaviour share this flat
    // mapping. A captured local is hoisted to a single module-level field. When two distinct
    // lambdas / delegate fields capture the SAME local, they alias — reassigning one delegate
    // overwrites the other's captured value. v2.2 detects this structurally via
    // LambdaCaptureAnalyzer + AllLambdaCaptures aggregation and raises an emit-time Error
    // (was a Warning in v2.1). Full cure requires a closure-object emulation layer
    // (long-term Phase F); see docs/known-bugs.md.
    public readonly struct LocalBinding
    {
        public readonly string Id;
        public LocalBinding(string id) { Id = id; }
    }

    public readonly Dictionary<ILocalSymbol, LocalBinding> LocalBindings = new(SymbolEqualityComparer.Default);

    // Lambda capture analysis (replaces HandlerBase.HasCaptures pre-v2.2).
    // See LambdaCaptureAnalyzer for rationale on manual walker vs Roslyn AnalyzeDataFlow.
    public readonly LambdaCaptureAnalyzer CaptureAnalyzer;

    // Aliasing detection: per captured symbol, list of lambdas (delegate creations) that captured it.
    // Populated by SimpleAssignmentHandler when a lambda is assigned to a delegate field.
    // UasmEmitter inspects this after emit and raises an Error if any captured symbol has > 1 lambda.
    public readonly Dictionary<ISymbol, List<IAnonymousFunctionOperation>> AllLambdaCaptures
        = new(SymbolEqualityComparer.Default);

    /// <summary>
    /// Record that <paramref name="lambda"/> was assigned to a delegate field (or otherwise
    /// stored long-lived). Each captured symbol is appended to AllLambdaCaptures so post-emit
    /// aliasing detection can flag multiple lambdas sharing the same captured local.
    /// </summary>
    public void RecordLambdaCaptures(IAnonymousFunctionOperation lambda)
    {
        var captures = CaptureAnalyzer.GetCaptures(lambda);
        foreach (var sym in captures)
        {
            // 'this' is always the same instance — captures of `this` (or instance-method receiver)
            // never alias in the problematic sense. Skip to avoid false positives when multiple
            // lambdas merely access this.field.
            if (sym is IParameterSymbol p && p.IsThis) continue;
            if (!AllLambdaCaptures.TryGetValue(sym, out var list))
            {
                list = new List<IAnonymousFunctionOperation>();
                AllLambdaCaptures[sym] = list;
            }
            if (!list.Contains(lambda)) list.Add(lambda);
        }
    }

    // Aggregate type support
    public static bool IsAggregateType(ITypeSymbol type)
    {
        if (type == null) return false;
        return type.IsTupleType;
    }

    readonly Dictionary<ITypeSymbol, AggregateLayout> _aggregateLayoutCache = new(SymbolEqualityComparer.Default);

    public AggregateLayout GetAggregateLayout(INamedTypeSymbol type)
    {
        if (_aggregateLayoutCache.TryGetValue(type, out var cached)) return cached;
        var layout = AggregateLayout.Build(type);
        _aggregateLayoutCache[type] = layout;
        return layout;
    }

    // Field initializers to emit at _start
    public readonly List<(string fieldName, IOperation initOp, ITypeSymbol fieldType)> FieldInitOps = new();

    // FieldChangeCallback: fieldName → propertyName
    public readonly Dictionary<string, string> FieldChangeCallbacks = new();

    // Enum array lookup: enum type → field name for int→enum runtime conversions
    public readonly struct EnumArrayInfo
    {
        public readonly string ArrayId;
        public readonly long MinOffset;
        public EnumArrayInfo(string arrayId, long minOffset) { ArrayId = arrayId; MinOffset = minOffset; }
    }

    public readonly Dictionary<ITypeSymbol, EnumArrayInfo> EnumArrayVars = new(SymbolEqualityComparer.Default);

    // Conditional access stack (for ?. operator)
    // Target is the evaluated instance; DelegateFieldName is non-null for delegate ?.Invoke().
    public readonly Stack<(HExpr Target, string DelegateFieldName)> ConditionalAccessStack = new();

    // using declaration Dispose tracking
    public readonly Stack<List<(HExpr val, ITypeSymbol type)>> UsingDisposableStack = new();

    /// <summary>Stack of using-stack depths at loop/switch entry points.
    /// Used to limit Dispose emission for break/continue to scopes inside the loop.</summary>
    public readonly Stack<int> LoopUsingDepthStack = new();

    // Switch break label stack — top is non-null inside switch body, null sentinel inside loop body.
    // StatementHandler.VisitBranch reads top to distinguish switch breaks (goto end label) from loop breaks (HBreak).
    public readonly Stack<string> SwitchBreakLabels = new();

    int _switchLabelCounter;
    /// <summary>Generate a unique end label for a switch statement (per EmitContext = per class).</summary>
    public string NextSwitchEndLabel() => $"__switchEnd_{++_switchLabelCounter}";

    // Delegate fields: tracks which user fields are delegate-typed and were expanded to bundles
    public readonly HashSet<string> DelegateFields = new();

    // Pending delegate bridges for dynamically hoisted lambdas/local functions
    public readonly List<(IMethodSymbol method, string bridgeExportName, Dictionary<ITypeParameterSymbol, ITypeSymbol> resolvedTypeParamMap)> PendingDelegateBridges = new();

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
        CaptureAnalyzer = new LambdaCaptureAnalyzer(compilation);
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

    /// <summary>Get or create a lookup array for int→enum runtime conversions. Cached per enum type.</summary>
    public EnumArrayInfo GetOrCreateEnumArray(INamedTypeSymbol enumType)
    {
        if (EnumArrayVars.TryGetValue(enumType, out var existing))
            return existing;

        var members = enumType.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => f.HasConstantValue && f.IsConst)
            .ToList();

        long minVal = 0, maxVal = 0;
        bool first = true;
        foreach (var m in members)
        {
            if (m.ConstantValue == null) continue;
            var val = Convert.ToInt64(m.ConstantValue);
            if (first) { minVal = val; maxVal = val; first = false; }
            else { if (val < minVal) minVal = val; if (val > maxVal) maxVal = val; }
        }

        long range = maxVal - minVal + 1;
        if (range > 65536)
            throw new NotSupportedException(
                $"Cannot cast integer to enum {enumType.Name}: value range {minVal}..{maxVal} ({range}) exceeds 65536 limit");

        int msb = 0;
        long tmp = range - 1;
        while (tmp > 0) { tmp >>= 1; msb++; }
        int arraySize = Math.Max(1 << msb, 1);

        var underlyingType = enumType.EnumUnderlyingType;
        var clrType = underlyingType?.SpecialType switch
        {
            SpecialType.System_Byte => typeof(byte),
            SpecialType.System_SByte => typeof(sbyte),
            SpecialType.System_Int16 => typeof(short),
            SpecialType.System_UInt16 => typeof(ushort),
            SpecialType.System_Int32 => typeof(int),
            SpecialType.System_UInt32 => typeof(uint),
            SpecialType.System_Int64 => typeof(long),
            SpecialType.System_UInt64 => typeof(ulong),
            _ => typeof(int),
        };

        var enumArr = new object[arraySize];
        for (int i = 0; i < arraySize; i++)
            enumArr[i] = Convert.ChangeType(i + minVal, clrType);

        var enumFullName = enumType.ToDisplayString().Replace('.', '_');
        var arrayId = $"__enumArr_{enumFullName}";
        DeclareEnumArray(arrayId, enumArr);
        var info = new EnumArrayInfo(arrayId, minVal);
        EnumArrayVars[enumType] = info;
        return info;
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

    /// <summary>Allocate a Scratch slot for a temporary value (slot-based, coalesced by register allocator).</summary>
    public int AllocTemp(string type) => Builder.AllocScratch(type);

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

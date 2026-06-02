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
        var fields = new List<FieldInfo>();
        var nameToIndex = new Dictionary<string, int>();

        if (type.IsTupleType)
        {
            var elements = type.TupleElements;
            for (int i = 0; i < elements.Length; i++)
            {
                var name = elements[i].Name;
                fields.Add(new FieldInfo(name, i, elements[i].Type));
                nameToIndex[name] = i;
                var itemName = $"Item{i + 1}";
                if (name != itemName) nameToIndex[itemName] = i;
                if (elements[i].CorrespondingTupleField != null)
                {
                    var corrName = elements[i].CorrespondingTupleField.Name;
                    if (!nameToIndex.ContainsKey(corrName)) nameToIndex[corrName] = i;
                }
            }
        }
        else if (type.TypeKind == TypeKind.Struct)
        {
            // User struct → instance fields mapped to indices in declaration order. Auto-property backing
            // fields are implicitly declared but carry the property as AssociatedSymbol; map them by the
            // property name so `get`/`set`/`init` resolve to the same object[] element.
            int i = 0;
            foreach (var member in type.GetMembers())
            {
                if (member is not IFieldSymbol { IsStatic: false, IsConst: false } f) continue;
                if (!f.IsImplicitlyDeclared)
                {
                    fields.Add(new FieldInfo(f.Name, i, f.Type));
                    nameToIndex[f.Name] = i++;
                }
                else if (f.AssociatedSymbol is IPropertySymbol prop)
                {
                    fields.Add(new FieldInfo(prop.Name, i, f.Type));
                    nameToIndex[prop.Name] = i++;
                }
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"AggregateLayout.Build called on non-aggregate type '{type.Name}'");
        }

        return new AggregateLayout(fields.AsReadOnly(), nameToIndex);
    }
}

public class EmitContext
{
    // Core dependencies
    public readonly Compilation Compilation;
    public readonly INamedTypeSymbol ClassSymbol;
    public readonly CModule Module;
    public readonly CoreBuilder Builder;
    public readonly LayoutPlanner Planner;

    // Method bookkeeping
    public readonly Dictionary<IMethodSymbol, CFunction> MethodFunctions = new(SymbolEqualityComparer.Default);
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

    /// <summary>When emitting a user-struct method/ctor, the receiver object[] param var id; otherwise null.
    /// Makes <c>this</c> / <c>this.field</c> resolve to the receiver array instead of the Behaviour.</summary>
    public string CurrentStructReceiverParamId;

    /// <summary>For each internal method, the set of callees that lie in the same strongly-connected
    /// component (i.e. calls that can re-enter the caller). Calls along these edges must spill the
    /// caller's live values to the software stack, because Udon's flat heap shares param/local slots
    /// across call frames. Populated by <c>UasmEmitter.BuildRecursionInfo</c> before emit.</summary>
    public Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> RecursiveCallees;

    /// <summary>True when a call from <paramref name="caller"/> to <paramref name="callee"/> is a
    /// recursion-cycle edge (callee in caller's non-trivial SCC, including direct self-recursion).</summary>
    public bool IsRecursiveEdge(IMethodSymbol caller, IMethodSymbol callee)
        => caller != null && callee != null && RecursiveCallees != null
           // Reduce BOTH ends to OriginalDefinition: RecursiveCallees is keyed by definition, but a
           // monomorphized generic specialization (e.g. Fact<int>) emits with the constructed symbol as
           // _currentMethod/target — without this its self-edge would be missed and the frame not spilled.
           && RecursiveCallees.TryGetValue(caller.OriginalDefinition, out var callees)
           && callees.Contains(callee.OriginalDefinition);

    /// <summary>A hoisted internal function (local function or lambda) — these re-enter shared flat-heap slots.</summary>
    public static bool IsHoistedFunction(IMethodSymbol m)
        => m != null && m.MethodKind is MethodKind.LocalFunction or MethodKind.LambdaMethod or MethodKind.AnonymousFunction;

    /// <summary>Record a recursion-cycle edge at emit time (used for mutual lambda recursion, which the
    /// pre-emit SCC pass cannot see because lambdas hoist during emission).</summary>
    public void MarkRecursiveEdge(IMethodSymbol caller, IMethodSymbol callee)
    {
        var key = caller.OriginalDefinition;
        RecursiveCallees ??= new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);
        if (!RecursiveCallees.TryGetValue(key, out var set))
            RecursiveCallees[key] = set = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        set.Add(callee.OriginalDefinition);
    }

    /// <summary>True if <paramref name="t"/> is <c>Nullable&lt;T&gt;</c>; yields the underlying T.
    /// Nullable is emulated as a boxed object (null | boxed T) — see ExternResolver type mapping.</summary>
    public static bool IsNullableT(ITypeSymbol t, out ITypeSymbol underlying)
    {
        if (t is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            underlying = n.TypeArguments[0];
            return true;
        }
        underlying = null;
        return false;
    }

    // ── Tail-call analysis (shared by named-method and recursive-lambda recursion detection) ──
    // A self-recursive call only needs spilling when it is NOT in tail position: a tail call reads nothing
    // of its frame afterwards, so the flat-heap clobber is harmless and deep tail recursion must not spill.

    /// <summary>Returns the call's argument list if <paramref name="op"/> is a self-recursive call to
    /// track, else default (and false via the out usage). Lets one tail walker serve named calls and
    /// delegate-variable invocations.</summary>
    public delegate bool SelfCallMatcher(IOperation op, out System.Collections.Immutable.ImmutableArray<IArgumentOperation> args);

    /// <summary>True if <paramref name="body"/> contains a NON-tail self-recursive call (per the matcher).
    /// Conditional (`cond ? a : self(..)`) branches count as tail positions; the condition does not.</summary>
    public static bool HasNonTailSelfCall(IOperation body, SelfCallMatcher isSelf)
    {
        if (body == null) return false;
        if (body is IReturnOperation ret) return NonTailInTailExpr(ret.ReturnedValue, isSelf);
        if (isSelf(body, out _)) return true; // self-call as a statement / non-tail position
        foreach (var child in body.Children)
        {
            if (child is ILocalFunctionOperation || child is IAnonymousFunctionOperation) continue;
            if (HasNonTailSelfCall(child, isSelf)) return true;
        }
        return false;
    }

    static bool NonTailInTailExpr(IOperation expr, SelfCallMatcher isSelf)
    {
        if (expr == null) return false;
        if (isSelf(expr, out var args)) // a tail self-call; only its arguments are non-tail
        {
            foreach (var a in args)
                if (AnySelfCall(a, isSelf)) return true;
            return false;
        }
        if (expr is IConditionalOperation cond) // branches stay in tail position; the condition does not
        {
            if (AnySelfCall(cond.Condition, isSelf)) return true;
            return NonTailInTailExpr(cond.WhenTrue, isSelf) || NonTailInTailExpr(cond.WhenFalse, isSelf);
        }
        return AnySelfCall(expr, isSelf); // any self-call buried in a non-tail expression
    }

    static bool AnySelfCall(IOperation op, SelfCallMatcher isSelf)
    {
        if (op == null) return false;
        if (isSelf(op, out _)) return true;
        foreach (var child in op.Children)
        {
            if (child is ILocalFunctionOperation || child is IAnonymousFunctionOperation) continue;
            if (AnySelfCall(child, isSelf)) return true;
        }
        return false;
    }

    /// <summary>Non-tail check for a recursive lambda: matches delegate invocations on <paramref name="selfVar"/>.</summary>
    public static bool HasNonTailDelegateSelfCall(IOperation body, ILocalSymbol selfVar)
        => HasNonTailSelfCall(body, (IOperation op, out System.Collections.Immutable.ImmutableArray<IArgumentOperation> args) =>
        {
            if (op is IInvocationOperation inv && inv.Instance is ILocalReferenceOperation lr
                && SymbolEqualityComparer.Default.Equals(lr.Local, selfVar))
            {
                args = inv.Arguments;
                return true;
            }
            args = default;
            return false;
        });
    public int NextMethodIndex;
    public readonly List<(IMethodSymbol symbol, CFunction func)> PendingLocalFunctions = new();
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

    // Aggregate type support — tuples and user-defined structs share the object[] emulation.
    public static bool IsAggregateType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named) return false;
        return named.IsTupleType || IsUserStruct(named);
    }

    /// <summary>Source-defined value struct (object[]-emulated). Excludes SDK/native structs
    /// (Vector3, Color, …) — which have native Udon extern types — by namespace, since in the test
    /// environment SDK types are source stubs (so syntax-refs alone can't tell them apart).</summary>
    public static bool IsUserStruct(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Struct || type.SpecialType != SpecialType.None) return false;
        if (type.DeclaringSyntaxReferences.Length == 0) return false; // from a referenced assembly = native
        return !IsSdkNamespace(type.ContainingNamespace);
    }

    static bool IsSdkNamespace(INamespaceSymbol ns)
    {
        for (var n = ns; n != null && !n.IsGlobalNamespace; n = n.ContainingNamespace)
        {
            if (n.Name is "System" or "UnityEngine" or "VRC" or "Cinemachine"
                or "TMPro" or "Unity" or "Microsoft")
                return true;
        }
        return false;
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
    public readonly Stack<(CLeaf Target, string DelegateFieldName)> ConditionalAccessStack = new();

    // using declaration Dispose tracking
    public readonly Stack<List<(CLeaf val, ITypeSymbol type)>> UsingDisposableStack = new();

    /// <summary>Stack of using-stack depths at loop/switch entry points.
    /// Used to limit Dispose emission for break/continue to scopes inside the loop.</summary>
    public readonly Stack<int> LoopUsingDepthStack = new();

    // Switch break label stack — top is non-null inside switch body, null sentinel inside loop body.
    // StatementHandler.VisitBranch reads top to distinguish switch breaks (goto end label) from loop breaks (CBreak).
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
    Func<IOperation, CLeaf> _visitExpression;
    Func<CLeaf, ITypeSymbol, IPatternOperation, CLeaf> _emitPatternCheck;

    public Action<IOperation> VisitOperation => _visitOperation
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");
    public Func<IOperation, CLeaf> VisitExpression => _visitExpression
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");
    public Func<CLeaf, ITypeSymbol, IPatternOperation, CLeaf> EmitPatternCheck => _emitPatternCheck
        ?? throw new InvalidOperationException("EmitContext dispatchers not initialized. Call InitializeDispatchers first.");

    public void InitializeDispatchers(
        Action<IOperation> visitOp,
        Func<IOperation, CLeaf> visitExpr,
        Func<CLeaf, ITypeSymbol, IPatternOperation, CLeaf> emitPattern)
    {
        _visitOperation = visitOp ?? throw new ArgumentNullException(nameof(visitOp));
        _visitExpression = visitExpr ?? throw new ArgumentNullException(nameof(visitExpr));
        _emitPatternCheck = emitPattern ?? throw new ArgumentNullException(nameof(emitPattern));
    }

    public EmitContext(Compilation compilation, INamedTypeSymbol classSymbol, LayoutPlanner planner)
    {
        Compilation = compilation;
        ClassSymbol = classSymbol;
        Module = new CModule { ClassName = classSymbol.ToDisplayString() };
        Builder = new CoreBuilder(Module);
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

    /// <summary>Declare a field in Module. Idempotent — returns existing name if already declared.</summary>
    public string DeclareField(string name, string type, FieldFlags flags = FieldFlags.None,
        object defaultValue = null, string syncMode = null)
    {
        if (_declaredFieldNames.Contains(name)) return name;
        var field = new FieldDecl(name, type) { Flags = flags, DefaultValue = defaultValue, SyncMode = syncMode };
        Module.Fields.Add(field);
        _declaredFieldNames.Add(name);
        return name;
    }

    /// <summary>Declare a named variable field. Idempotent.</summary>
    public string DeclareVar(string id, string type)
    {
        if (_declaredFieldNames.Contains(id)) return id;
        Module.Fields.Add(new FieldDecl(id, type));
        _declaredFieldNames.Add(id);
        return id;
    }

    /// <summary>Try to declare a variable. Returns true if newly declared.</summary>
    public bool TryDeclareVar(string id, string type)
    {
        if (_declaredFieldNames.Contains(id)) return false;
        Module.Fields.Add(new FieldDecl(id, type));
        _declaredFieldNames.Add(id);
        return true;
    }

    /// <summary>Declare a local variable with unique field name.</summary>
    public string DeclareLocal(string name, string type)
    {
        var idx = NextIndex($"lcl_{name}_{type}");
        var id = $"__lcl_{name}_{type}_{idx}";
        Module.Fields.Add(new FieldDecl(id, type));
        _declaredFieldNames.Add(id);
        return id;
    }

    /// <summary>Declare a "this" reference field with type remapping for Udon heap.</summary>
    public string DeclareThis(string udonType)
    {
        var heapType = SupportedThisTypes.Contains(udonType) ? udonType : "VRCUdonUdonBehaviour";
        var idx = NextIndex($"this_{heapType}");
        var id = $"__this_{heapType}_{idx}";
        Module.Fields.Add(new FieldDecl(id, heapType) { DefaultValue = "this" });
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
        Module.Fields.Add(new FieldDecl(id, "SystemObjectArray") { DefaultValue = values });
        _declaredFieldNames.Add(id);
        return id;
    }

    // ── Software recursion stack ──
    // Udon's flat heap shares param/local slots across call frames, so recursion-cycle calls must spill
    // the caller's live values to a heap-backed LIFO stack (boxed object[]) and reload after the call.

    public const string RecurStackId = "__recurStack";
    public const string RecurSpId = "__recurSp";
    /// <summary>Max boxed values held across all live recursion frames (depth × live-vars-per-frame).</summary>
    public const int RecurStackSize = 512;
    bool _recurStackDeclared;

    /// <summary>Idempotently declare the per-program recursion stack (object[] backing + int stack pointer).
    /// Heap default allocates the backing array and zeroes the pointer; LIFO spill/reload keeps it balanced.</summary>
    public void EnsureRecursionStack()
    {
        if (_recurStackDeclared) return;
        _recurStackDeclared = true;
        Module.Fields.Add(new FieldDecl(RecurStackId, "SystemObjectArray") { DefaultValue = new object[RecurStackSize] });
        _declaredFieldNames.Add(RecurStackId);
        Module.Fields.Add(new FieldDecl(RecurSpId, "SystemInt32") { DefaultValue = 0 });
        _declaredFieldNames.Add(RecurSpId);
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
        var field = Module.Fields.FirstOrDefault(f => f.Name == name);
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
        Module.Fields.Add(new FieldDecl(id, type) { DefaultValue = value });
        _declaredFieldNames.Add(id);
        _structConstIds[key] = id;
        return id;
    }

    /// <summary>Get the Udon type of a declared field by its ID.</summary>
    public string GetFieldType(string id)
    {
        return Module.Fields.FirstOrDefault(f => f.Name == id)?.Type;
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

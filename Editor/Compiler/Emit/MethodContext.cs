using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

public readonly struct MethodSlot
{
    public readonly int Index;
    public readonly string VarPrefix;

    public MethodSlot(int index, string varPrefix)
    {
        Index = index;
        VarPrefix = varPrefix;
    }
}

/// <summary>
/// Owns per-method emission bookkeeping for one class emission.
/// </summary>
public sealed class MethodContext
{
    public enum ReceiverAbi { None, ObjectArray }
    public enum CallableKind { Method, Closure, Bridge, Synthetic }

    public class RegisteredCallable
    {
        public readonly IMethodSymbol Definition;
        public readonly MethodSlot Slot;
        public readonly string ExportName;
        public readonly string ReceiverFieldId;
        public readonly ImmutableArray<string> ParamVarIds;
        public readonly ImmutableArray<StorageType> ParamStorageTypes;
        public readonly ImmutableArray<ReturnSlot> ReturnSlots;
        public readonly MethodLayout Layout;
        public readonly ReceiverAbi Receiver;
        public readonly CallableKind Kind;
        public readonly string Name;
        public readonly IMethodSymbol TargetDefinition;
        public readonly bool IsDeferredBody;

        public RegisteredCallable(
            IMethodSymbol definition,
            MethodSlot slot,
            string name,
            string exportName,
            string receiverFieldId,
            IEnumerable<string> paramVarIds,
            IEnumerable<StorageType> paramStorageTypes,
            IEnumerable<ReturnSlot> returnSlots,
            ReceiverAbi receiver,
            CallableKind kind,
            MethodLayout layout = null,
            IMethodSymbol targetDefinition = null,
            bool isDeferredBody = false)
        {
            Definition = definition;
            Slot = slot;
            Name = name ?? throw new ArgumentNullException(nameof(name));
            ExportName = exportName;
            ReceiverFieldId = receiverFieldId;
            ParamVarIds = (paramVarIds
                ?? throw new ArgumentNullException(nameof(paramVarIds)))
                .ToImmutableArray();
            ParamStorageTypes = (paramStorageTypes
                ?? throw new ArgumentNullException(nameof(paramStorageTypes)))
                .ToImmutableArray();
            ReturnSlots = (returnSlots
                ?? throw new ArgumentNullException(nameof(returnSlots)))
                .ToImmutableArray();
            if (ParamVarIds.Length != ParamStorageTypes.Length)
                throw new ArgumentException(
                    "Callable parameter ids and storage types must have equal length.");
            Receiver = receiver;
            Kind = kind;
            Layout = layout;
            TargetDefinition = targetDefinition?.OriginalDefinition;
            IsDeferredBody = isDeferredBody;
        }
    }

    readonly Dictionary<IMethodSymbol, RegisteredCallable> _callables =
        new(SymbolEqualityComparer.Default);
    readonly Dictionary<IMethodSymbol, FlatFunction> _functions =
        new(SymbolEqualityComparer.Default);
    readonly Dictionary<RegisteredCallable, FlatFunction>
        _functionsByCallable = new();
    readonly Dictionary<string, RegisteredCallable> _syntheticCallables = new(StringComparer.Ordinal);
    readonly List<RegisteredCallableBody> _registeredBodies = new();
    BoundMethodBodyTable.Materializer _bodyAuthority;
    bool _callableRegistryFrozen;
    public IReadOnlyDictionary<IMethodSymbol, RegisteredCallable> Callables => _callables;
    public IReadOnlyDictionary<string, RegisteredCallable> SyntheticCallables => _syntheticCallables;
    public IReadOnlyDictionary<IMethodSymbol, FlatFunction> Functions
        => _functions;
    public IReadOnlyDictionary<IMethodSymbol, MethodSlot> Slots { get; }
    public IReadOnlyDictionary<IMethodSymbol, ReturnSlot[]> Returns { get; }
    public IReadOnlyDictionary<IMethodSymbol, string[]> ParamVarIds { get; }

    public MethodContext()
    {
        Slots = new CallableProjection<MethodSlot>(_callables, c => c.Slot);
        Returns = new CallableProjection<ReturnSlot[]>(
            _callables, c => c.ReturnSlots.ToArray(),
            c => c.ReturnSlots.Length > 0);
        ParamVarIds = new CallableProjection<string[]>(
            _callables, c => c.ParamVarIds.ToArray());
    }

    internal void ConfigureBodyAuthority(
        BoundMethodBodyTable.Materializer bodyAuthority)
    {
        RequireMutableRegistry();
        if (_bodyAuthority != null)
            throw new InvalidOperationException(
                "Callable body authority was configured twice.");
        _bodyAuthority = bodyAuthority
            ?? throw new ArgumentNullException(
                nameof(bodyAuthority));
    }

    internal BoundMethodBody DescribeBody(
        IMethodSymbol method)
        => (_bodyAuthority
            ?? throw new InvalidOperationException(
                "Callable body authority was not configured."))
            .Get(method)
            ?? throw new InvalidOperationException(
                $"Callable body '{method}' could not be described.");

    internal bool IsExecutableCallable(
        IMethodSymbol method)
    {
        var body = DescribeBody(method);
        if (body.Disposition
            == CallableBodyDisposition.Unsupported)
            throw body.UnsupportedCallableException(
                "Callable registration");
        return body.HasExecutableCallableBody;
    }

    BoundMethodBody RequireExecutableBody(
        IMethodSymbol method)
    {
        var body = DescribeBody(method);
        if (!body.HasExecutableCallableBody)
            throw body.UnsupportedCallableException(
                "Callable registration");
        return body;
    }

    public IMethodSymbol CurrentMethod;

    // When emitting a user-struct method/ctor, the receiver object[] param var id; otherwise null.
    // Makes this / this.field resolve to the receiver array instead of the Behaviour.
    public string CurrentStructReceiverParamId;

    public int NextMethodIndex;

    public MethodSlot Reserve(Func<int, string> prefixFactory)
    {
        RequireMutableRegistry();
        var idx = NextMethodIndex++;
        return new MethodSlot(idx, prefixFactory(idx));
    }

    public RegisteredCallable AddCallable(
        IMethodSymbol method,
        MethodSlot slot,
        string name,
        string exportName,
        string receiverFieldId,
        IEnumerable<string> paramVarIds,
        IEnumerable<StorageType> paramStorageTypes,
        IEnumerable<ReturnSlot> returnSlots,
        ReceiverAbi receiver = ReceiverAbi.None,
        MethodLayout layout = null,
        bool isDeferredBody = false)
    {
        RequireMutableRegistry();
        if (method == null)
            throw new ArgumentNullException(nameof(method));
        var body = RequireExecutableBody(method);
        var callable = new RegisteredCallable(
            method, slot, name, exportName, receiverFieldId,
            paramVarIds, paramStorageTypes, returnSlots,
            receiver, CallableKind.Method, layout,
            isDeferredBody: isDeferredBody);
        _callables.Add(method, callable);
        _registeredBodies.Add(
            new RegisteredCallableBody(
                callable, null, body));
        return callable;
    }

    sealed class CallableProjection<T> : IReadOnlyDictionary<IMethodSymbol, T>
    {
        readonly IReadOnlyDictionary<IMethodSymbol, RegisteredCallable> _source;
        readonly Func<RegisteredCallable, T> _select;
        readonly Func<RegisteredCallable, bool> _include;

        public CallableProjection(IReadOnlyDictionary<IMethodSymbol, RegisteredCallable> source,
            Func<RegisteredCallable, T> select, Func<RegisteredCallable, bool> include = null)
        {
            _source = source;
            _select = select;
            _include = include ?? (_ => true);
        }

        public int Count => _source.Values.Count(_include);
        public IEnumerable<IMethodSymbol> Keys
            => _source.Where(pair => _include(pair.Value)).Select(pair => pair.Key);
        public IEnumerable<T> Values
            => _source.Values.Where(_include).Select(_select);
        public T this[IMethodSymbol key]
            => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();

        public bool ContainsKey(IMethodSymbol key)
            => _source.TryGetValue(key, out var callable) && _include(callable);

        public bool TryGetValue(IMethodSymbol key, out T value)
        {
            if (_source.TryGetValue(key, out var callable) && _include(callable))
            {
                value = _select(callable);
                return true;
            }
            value = default;
            return false;
        }

        public IEnumerator<KeyValuePair<IMethodSymbol, T>> GetEnumerator()
        {
            foreach (var pair in _source)
                if (_include(pair.Value))
                    yield return new KeyValuePair<IMethodSymbol, T>(pair.Key, _select(pair.Value));
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ── Per-spec hoisted closures (design 2026-07-10 v3 §2B, B64/B70 root fix) ──
    //
    // A lambda / non-generic local function's IMethodSymbol is DEFINITION-EQUAL across the enclosing
    // generic's instantiations ([Y8]), so a bare symbol key shares ONE hoisted FlatFunction across specs —
    // the closure body is then baked with the first spec's type arguments and its flat bookkeeping
    // aliases (VM-proven B64/B70/B89). Every hoisted closure therefore registers under a composite
    // (definition, enclosing-spec type-args) key. The args component uses CLR SYMBOL identity
    // (element-wise SymbolEqualityComparer) — NEVER Udon type-name strings, which launder distinct
    // CLR types onto one tag (B66/B76) and would unsoundly share closures across e.g. Run<IFoo>/Run<IBar>.
    // A closure in a non-generic context has an EMPTY args vector — the key degenerates 1:1 to the
    // definition and behavior is byte-identical to the old symbol keying.

    /// <summary>Everything a per-spec hoisted closure owns: its exact function ABI, flat param/return field
    /// ids, its hidden __envp field id (null for capture-free), the enclosing constructed specs it
    /// was registered under (drives the type-param compose at its own emission — replacing the
    /// fallback lookup), and the composite key args.</summary>
    public sealed class ClosureSpec : RegisteredCallable
    {
        public readonly ImmutableArray<ITypeSymbol> KeyArgs;
        public readonly ImmutableArray<IMethodSymbol> OwnerSpecs;
        public readonly INamedTypeSymbol ContainingTypeSpec;
        public readonly string EnvpFieldId;

        public ClosureSpec(
            IMethodSymbol definition,
            MethodSlot slot,
            string name,
            IEnumerable<string> paramVarIds,
            IEnumerable<StorageType> paramStorageTypes,
            IEnumerable<ReturnSlot> returnSlots,
            ImmutableArray<ITypeSymbol> keyArgs,
            ImmutableArray<IMethodSymbol> ownerSpecs,
            INamedTypeSymbol containingTypeSpec,
            string envpFieldId,
            bool isDeferredBody)
            : base(
                definition, slot, name, null, null,
                paramVarIds, paramStorageTypes, returnSlots,
                ReceiverAbi.None, CallableKind.Closure,
                isDeferredBody: isDeferredBody)
        {
            KeyArgs = keyArgs;
            OwnerSpecs = ownerSpecs;
            ContainingTypeSpec = containingTypeSpec;
            EnvpFieldId = envpFieldId;
        }
    }

    /// <summary>One registered source body with the exact specialization scope used by emission.</summary>
    public sealed class RegisteredCallableBody
    {
        public RegisteredCallable Callable { get; }
        public IMethodSymbol Method => Callable.Definition;
        public ClosureSpec Closure { get; }
        internal BoundMethodBody BoundBody { get; }
        public ImmutableArray<IMethodSymbol> OwnerSpecs { get; }
        public IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeParameterMap { get; }
        internal CallSiteBindingScope BindingScope { get; }

        internal RegisteredCallableBody(
            RegisteredCallable callable,
            ClosureSpec closure,
            BoundMethodBody boundBody)
        {
            Callable = callable ?? throw new ArgumentNullException(nameof(callable));
            Closure = closure;
            BoundBody = boundBody
                ?? throw new ArgumentNullException(
                    nameof(boundBody));
            if (!BoundBody.HasExecutableCallableBody)
                throw BoundBody.UnsupportedCallableException(
                    "Callable body registration");
            if (!SymbolEqualityComparer.Default.Equals(
                    BoundBody.MethodDefinition,
                    callable.Definition.OriginalDefinition))
                throw new ArgumentException(
                    "The registered callable and executable body "
                    + "describe different method definitions.",
                    nameof(boundBody));
            OwnerSpecs = closure?.OwnerSpecs
                ?? (!callable.Definition.IsDefinition
                    ? ImmutableArray.Create(callable.Definition)
                    : ImmutableArray<IMethodSymbol>.Empty);

            IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map =
                closure?.ContainingTypeSpec != null
                    ? TypeEnvironment.ForContainingType(
                        closure.ContainingTypeSpec, null)
                    : null;
            for (var i = OwnerSpecs.Length - 1; i >= 0; i--)
                map = TypeEnvironment.ForMethod(OwnerSpecs[i], map);
            if (closure != null)
                map = TypeEnvironment.ForMethod(closure.Definition, map);
            TypeParameterMap = map;

            if (closure != null)
            {
                var coveredOwners = new HashSet<IMethodSymbol>(
                    OwnerSpecs.Select(owner => owner.OriginalDefinition),
                    SymbolEqualityComparer.Default);
                for (var symbol = callable.Definition.ContainingSymbol;
                     symbol is IMethodSymbol enclosing;
                     symbol = enclosing.ContainingSymbol)
                    if (!coveredOwners.Contains(enclosing.OriginalDefinition)
                        && (enclosing.OriginalDefinition.TypeParameters.Length > 0
                            || enclosing.ContainingType is { IsGenericType: true }))
                        throw new InvalidOperationException(
                            $"Closure '{callable.Definition.ToDisplayString()}' was registered "
                            + "without its lexical owner specialization "
                            + $"'{enclosing.OriginalDefinition.ToDisplayString()}'.");
            }

            var key = closure != null
                ? new SpecializationKey(callable.Definition, closure.KeyArgs)
                : SpecializationKey.ForMethod(callable.Definition);
            BindingScope = CallSiteBindingScope.ForMethod(key);
        }
    }

    /// <summary>Per-spec identity as a TYPE (design 2026-07-10 symbol-intern v2, T2 pilot): one
    /// definition + the composed enclosing-spec type-argument vector. A def-keyed map and a
    /// spec-keyed map now differ in KEY TYPE, so filing a spec-dependent value under a bare
    /// definition key — the B89 first-wins class — no longer type-checks. Args compare element-wise
    /// by CLR symbol identity (absorbs the former ArgsEqual; Udon type-name strings stay banned,
    /// B66/B76).</summary>
    readonly Dictionary<SpecializationKey, ClosureSpec> _closureSpecs = new();

    /// <summary>The closure spec currently being emitted (set by EmitMethod for pending-closure
    /// drains), or null when emitting a named method. Consumers that used to read the bare
    /// definition-keyed maps for the CURRENT method (spill collection, self-__envp) must prefer
    /// this — a bare read under multi-spec is the silent under-spill / wrong-env class (F3/F4).</summary>
    public ClosureSpec CurrentClosureSpec;

    /// <summary>The enclosing constructed specs of the current emission context (a named spec =
    /// itself; an emitting closure = its record's chain; else empty).</summary>
    public ImmutableArray<IMethodSymbol> CurrentOwnerSpecs = ImmutableArray<IMethodSymbol>.Empty;

    public IDisposable EnterCallableScope(IMethodSymbol method, ClosureSpec closureSpec,
        string structReceiverParamId, ImmutableArray<IMethodSymbol> ownerSpecs)
    {
        var scope = new CallableScope(this);
        CurrentMethod = method;
        CurrentClosureSpec = closureSpec;
        CurrentStructReceiverParamId = structReceiverParamId;
        CurrentOwnerSpecs = ownerSpecs;
        return scope;
    }

    sealed class CallableScope : IDisposable
    {
        readonly MethodContext _context;
        readonly IMethodSymbol _method;
        readonly ClosureSpec _closureSpec;
        readonly string _receiverParamId;
        readonly ImmutableArray<IMethodSymbol> _ownerSpecs;
        bool _disposed;

        public CallableScope(MethodContext context)
        {
            _context = context;
            _method = context.CurrentMethod;
            _closureSpec = context.CurrentClosureSpec;
            _receiverParamId = context.CurrentStructReceiverParamId;
            _ownerSpecs = context.CurrentOwnerSpecs;
        }

        public void Dispose()
        {
            if (_disposed)
                throw new InvalidOperationException("Callable scope disposed twice.");
            _disposed = true;
            _context.CurrentMethod = _method;
            _context.CurrentClosureSpec = _closureSpec;
            _context.CurrentStructReceiverParamId = _receiverParamId;
            _context.CurrentOwnerSpecs = _ownerSpecs;
        }
    }

    public bool TryGetClosureSpec(IMethodSymbol def, ImmutableArray<ITypeSymbol> keyArgs, out ClosureSpec spec)
    {
        spec = null;
        if (def == null) return false;
        if (_closureSpecs.TryGetValue(new SpecializationKey(def, keyArgs), out spec)) return true;

        // Roslyn can materialize a different local-function/lambda symbol when the same source body is
        // obtained through a constructed containing type. Pre-emission planning stores the body-graph's
        // canonical symbol, while emission sees the operation-tree symbol. They are one definition even
        // when SymbolEqualityComparer does not relate them, so use the frozen source identity as the
        // secondary definition comparison. The specialization arguments remain exact.
        foreach (var pair in _closureSpecs)
        {
            if (!ClosureIdentityPlan.SameSpec(pair.Key, def, keyArgs)) continue;
            if (spec != null)
                throw new InvalidOperationException(
                    $"Hoisted closure '{def?.Name}' matches more than one registered "
                    + "specialization; the fallback would silently pick one.");
            spec = pair.Value;
        }
        return spec != null;
    }

    /// <summary>Throw-on-miss twin (design v3 §2B: a multi-spec context must never silently fall
    /// back to another spec's closure — the pre-fix failure mode was exactly that, silent).</summary>
    public ClosureSpec GetClosureSpec(IMethodSymbol def, ImmutableArray<ITypeSymbol> keyArgs)
        => TryGetClosureSpec(def, keyArgs, out var spec)
            ? spec
            : throw new InvalidOperationException(
                $"Hoisted closure '{def?.Name}' has no registration for the current enclosing spec "
                + $"args [{string.Join(", ", keyArgs.Select(a => a?.ToDisplayString() ?? "?"))}] — a "
                + "per-spec closure lookup fell outside its registration context (per-spec keying bug).");

    public ClosureSpec AddClosureCallable(
        IMethodSymbol definition,
        ImmutableArray<ITypeSymbol> keyArgs,
        ImmutableArray<IMethodSymbol> ownerSpecs,
        INamedTypeSymbol containingTypeSpec,
        MethodSlot slot,
        string name,
        IEnumerable<string> paramVarIds,
        IEnumerable<StorageType> paramStorageTypes,
        IEnumerable<ReturnSlot> returnSlots,
        string envpFieldId,
        bool isDeferredBody)
    {
        RequireMutableRegistry();
        if (definition == null)
            throw new ArgumentNullException(nameof(definition));
        var body = RequireExecutableBody(definition);
        var spec = new ClosureSpec(
            definition, slot, name,
            paramVarIds, paramStorageTypes, returnSlots,
            keyArgs, ownerSpecs, containingTypeSpec,
            envpFieldId, isDeferredBody);
        _closureSpecs.Add(new SpecializationKey(definition, keyArgs), spec);
        _registeredBodies.Add(
            new RegisteredCallableBody(
                spec, spec, body));
        return spec;
    }

    public RegisteredCallable AddSyntheticCallable(string name, FlatFunction function,
        IMethodSymbol signatureMethod, IMethodSymbol targetMethod, CallableKind kind,
        string[] paramVarIds = null, ReturnSlot[] returnSlots = null)
    {
        if (string.IsNullOrEmpty(name) || function == null)
            throw new ArgumentException("A synthetic callable requires a name and function.");
        var callable = new RegisteredCallable(
            signatureMethod, default, name, function.ExportName, null,
            paramVarIds ?? Array.Empty<string>(),
            (paramVarIds ?? Array.Empty<string>())
                .Select(_ => StorageTypes.Object),
            returnSlots ?? Array.Empty<ReturnSlot>(),
            ReceiverAbi.None, kind,
            targetDefinition: targetMethod);
        _syntheticCallables.Add(name, callable);
        _functionsByCallable.Add(callable, function);
        return callable;
    }

    /// <summary>Census surface (read-only): every registered per-spec closure key. Harness
    /// instrumentation only — emission never enumerates the registry.</summary>
    public IEnumerable<SpecializationKey> ClosureSpecKeys => _closureSpecs.Keys;
    /// <summary>Read-only callable records used to validate the frozen pre-emission definition set.</summary>
    public IEnumerable<ClosureSpec> ClosureSpecs => _closureSpecs.Values;

    /// <summary>Frozen-body census input in exact registration order.
    /// Synthetic callables have no source body and are excluded.</summary>
    public IReadOnlyList<RegisteredCallableBody> RegisteredBodies
        => _registeredBodies;

    public void FreezeCallableRegistry()
    {
        if (_callableRegistryFrozen)
            throw new InvalidOperationException(
                "Callable registry was frozen twice.");
        _callableRegistryFrozen = true;
    }

    public void AddMaterializedFunction(
        RegisteredCallable callable,
        FlatFunction function)
    {
        if (!_callableRegistryFrozen)
            throw new InvalidOperationException(
                "Callable functions cannot be materialized before "
                + "the callable registry is frozen.");
        if (callable == null || function == null)
            throw new ArgumentNullException(
                callable == null
                    ? nameof(callable)
                    : nameof(function));
        _functionsByCallable.Add(callable, function);
        if (callable.Kind == CallableKind.Method)
            _functions.Add(callable.Definition, function);
    }

    public FlatFunction RequireFunction(
        RegisteredCallable callable)
        => callable != null
           && _functionsByCallable.TryGetValue(
               callable, out var function)
            ? function
            : throw new InvalidOperationException(
                $"Callable '{callable?.Name}' has no materialized function.");

    public RegisteredCallable RequireCallable(FlatFunction function)
    {
        if (function == null) throw new ArgumentNullException(nameof(function));
        foreach (var pair in _functionsByCallable)
            if (ReferenceEquals(pair.Value, function))
                return pair.Key;
        throw new InvalidOperationException(
            $"Function '{function.Name}' has no registered callable.");
    }

    void RequireMutableRegistry()
    {
        if (_callableRegistryFrozen)
            throw new InvalidOperationException(
                "Callable registry cannot change after publication.");
    }
}

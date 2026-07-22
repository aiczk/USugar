using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

public class EmitContext
{
    // Core dependencies
    public readonly Compilation Compilation;
    public readonly INamedTypeSymbol ClassSymbol;
    public readonly CModule Module;
    public readonly CoreBuilder Builder;
    public readonly LayoutPlanner Planner;
    public readonly StorageContext Storage;
    public readonly BoundaryChecker Boundary;
    public readonly MethodAnalysisCache MethodAnalyses;
    public readonly GenericContext Generics = new GenericContext();
    public readonly RecursionContext RecursionContext = new RecursionContext();
    public readonly ClosureContext Closures = new ClosureContext();
    public readonly AggregateContext Aggregates = new AggregateContext();
    public readonly ClassTypeObjectContext ClassTypes = new ClassTypeObjectContext();

    /// <summary>CA-v2b-2: virtual-call lowering authority (dispatch set + devirt). Set by UasmEmitter after
    /// ClassTypes is seeded and before EmitMethods/BuildRecursionInfo, which both consume it.</summary>
    public VirtualDispatch VirtualDispatch;
    public readonly SyntheticContext Synthetics = new SyntheticContext();
    public readonly ControlFlowContext ControlFlow = new ControlFlowContext();
    public readonly InitializationContext Initializers = new InitializationContext();
    public readonly DiagnosticContext DiagnosticState = new DiagnosticContext();
    public readonly MethodContext Methods = new MethodContext();
    public readonly struct MethodSlot
    {
        public readonly int Index;
        public readonly string VarPrefix;
        public MethodSlot(int index, string varPrefix) { Index = index; VarPrefix = varPrefix; }
    }

    // Depth-1 type-param scope. EmitMethod is a non-recursive serial drain, so exactly one map is
    // active at a time; a nested Enter means a prior scope leaked (a compiler bug) and throws loudly
    // rather than silently inheriting someone else's map. Dispose is the SOLE clear site, so the map
    // is cleared even if body emission throws.
    public IDisposable EnterTypeParamScope(IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map)
        => Generics.EnterScope(map, Methods.CurrentMethod);

    /// <summary>Composite key args for a hoisted-closure registration or lookup (2026-07-11 pre-fuzz
    /// audit HIGH fix): the closure's own type args ⊕ the args of its LEXICAL enclosing generic
    /// owners (declaration chain), each resolved against the current emission's owner chain
    /// (CurrentOwnerSpecs, captured into the registration identity). The
    /// former composition used the lookup site's AMBIENT spec vector, which keyed the TARGET by the
    /// REGISTRAR's own spec dimension: a self-recursive / mutually-recursive generic local function
    /// then re-composed its own args on every hop (key length grew, every lookup missed, the pending
    /// drain re-registered forever — VM-proven compile hang). The lexical chain is the same from
    /// every reference site, so registration and lookup can never skew.</summary>
    public readonly struct ClosureIdentity
    {
        public readonly System.Collections.Immutable.ImmutableArray<ITypeSymbol> KeyArgs;
        public readonly System.Collections.Immutable.ImmutableArray<IMethodSymbol> OwnerSpecs;
        public ClosureIdentity(
            System.Collections.Immutable.ImmutableArray<ITypeSymbol> keyArgs,
            System.Collections.Immutable.ImmutableArray<IMethodSymbol> ownerSpecs)
        { KeyArgs = keyArgs; OwnerSpecs = ownerSpecs; }
    }

    public ClosureIdentity ResolveClosureIdentity(IMethodSymbol closure)
    {
        var b = System.Collections.Immutable.ImmutableArray.CreateBuilder<ITypeSymbol>();
        var owners = System.Collections.Immutable.ImmutableArray.CreateBuilder<IMethodSymbol>();
        if (closure.TypeArguments.Length > 0) b.AddRange(closure.TypeArguments);
        for (var s = closure.OriginalDefinition.ContainingSymbol; s is IMethodSymbol owner; s = owner.ContainingSymbol)
        {
            var ownerDef = owner.OriginalDefinition;
            IMethodSymbol spec = null;
            foreach (var os in Methods.CurrentOwnerSpecs)
                if (SymbolEqualityComparer.Default.Equals(os.OriginalDefinition, ownerDef)) { spec = os; break; }
            if (spec == null)
            {
                if (ownerDef.TypeParameters.Length > 0
                    || ownerDef.ContainingType is { IsGenericType: true })
                    throw new InvalidOperationException(
                        $"Closure '{closure.ToDisplayString()}' has no active lexical owner "
                        + $"specialization for '{ownerDef.ToDisplayString()}'.");
                continue;
            }
            if (!owners.Any(existing => SymbolEqualityComparer.Default.Equals(existing, spec)))
                owners.Add(spec);
            if (ownerDef.TypeParameters.Length > 0) b.AddRange(spec.TypeArguments);
            // Containing-type dimension once, at the outermost named method (feature G: a generic
            // struct/class member's closure binds the container's T too).
            if (owner.ContainingSymbol is not IMethodSymbol && spec.ContainingType is { IsGenericType: true } ct)
                b.AddRange(ct.TypeArguments);
        }
        return new ClosureIdentity(b.ToImmutable(), owners.ToImmutable());
    }

    public System.Collections.Immutable.ImmutableArray<ITypeSymbol> ComposeClosureKeyArgs(IMethodSymbol closure)
        => ResolveClosureIdentity(closure).KeyArgs;

    // Persistent local symbol → field name mapping (survives scope pop). Holds NON-captured locals
    // only: a captured local has no flat field — its cell lives in the owning scope's env record
    // (Stage 2, TryGetEnvBinding / EnvEmit), so per-activation captures no longer alias.
    public readonly struct LocalBinding
    {
        public readonly string Id;
        public LocalBinding(string id) { Id = id; }
    }

    // Round-7 follow-up [Q4]: foreach ITERATION variables. C# makes them READONLY, so invoking a
    // non-readonly struct member on one runs on a DEFENSIVE COPY (the classic foreach-struct-
    // mutation no-op); the loop variable's object[] is live storage in the flat emulation, so the
    // struct-instance-call receiver is CLONED when its chain roots at one of these locals
    // (VM-proven: loop-var reads after a mutating call 1112 vs CLR 102). MEMBERSHIP-ONLY set (§1.5).
    public readonly HashSet<ILocalSymbol> ForeachIterationLocals = new(SymbolEqualityComparer.Default);

    // Dispatch delegates (Core IR-based)
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
        Module = new CModule(planner.TypeFacts) { ClassName = classSymbol.ToDisplayString() };
        Builder = new CoreBuilder(Module);
        Planner = planner;
        Storage = new StorageContext(Module);
        MethodAnalyses = new MethodAnalysisCache(compilation);
        Boundary = new BoundaryChecker(this);
    }

    // ══════════════════════════════════════════════════════════════════
    // Variable naming utilities (replaces VariableTable)
    // ══════════════════════════════════════════════════════════════════

    // ── Software recursion stack ──
    // Udon's flat heap shares param/local slots across call frames, so recursion-cycle calls must spill
    // the caller's live values to a heap-backed LIFO stack (boxed object[]) and reload after the call.

    public const string RecurStackId = RecurStack.StackId;
    public const string RecurSpId = RecurStack.SpId;
    /// <summary>Max boxed values held across all live recursion frames (depth × live-vars-per-frame).
    /// Wave-12 [V1]: 512 → 8192. Legal non-tail recursion at depth ~600 with per-frame closure state
    /// (~9 spilled slots per logical frame, VM-proven ER05/ER11 budget probes) needs ~5400 entries —
    /// the old 512 budget VmFaulted compile-clean code at depths plain C# handles trivially. The
    /// array is allocated once per program and ONLY when a recursion cycle exists
    /// (EnsureRecursionStack is on-demand), so non-recursive programs pay nothing; the size lives in
    /// the heap-default side channel, not the UASM text.</summary>
    public const int RecurStackSize = RecurStack.Size;


    public const string ReflTypeIdField = "__refl_typeid";
    public const string ReflTypeIdsField = "__refl_typeids";
    public const string ReflTypeNameField = "__refl_typename";

    /// <summary>Declare reflection type IDs array.</summary>
    public void DeclareReflTypeIds(long[] typeIds)
    {
        Storage.DeclareField(ReflTypeIdsField, "SystemInt64Array", defaultValue: typeIds);
    }

}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Stage 2 M1 — structural closure-scope analysis (design §2). Computes, for a class and its
/// hoisted lambdas/local functions, which lexical scope OWNS each captured variable (the env
/// allocation point), the declaration-order slot within that scope, the raw lexical scope tree,
/// and — per closure — the nearest enclosing capture-bearing scope it binds its env chain to
/// (BindingScope) plus static hop-distance along that chain.
///
/// BEHAVIOR-NEUTRAL (M1 contract): this analysis is read-only and consumed by nothing yet. Codegen
/// wiring (env alloc/access rewrite) is M2. Structure only — no independent type resolution (design
/// §2 rule 1: types are resolved at emit time from the live _typeParamMap, never re-derived here).
///
/// Keying discipline (design §2 rule 2): <see cref="Build"/> walks DEFINITION symbols only (generic
/// method original definitions, never constructed specs), so every symbol reached during the walk —
/// locals, params, closure method symbols — is itself definition-side. All maps use
/// SymbolEqualityComparer.Default. A caller resolving a CONSTRUCTED spec's parameter against
/// CapturedSlots/ClosureScopes must first re-key through <c>p.ContainingSymbol.OriginalDefinition</c>
/// (Ordinal lookup) — RegisterGenericSpecialization mints fresh IParameterSymbol instances per spec
/// that never compare equal to the definition's own.
///
/// Ownership table implemented (design §1.2): method/closure params and body-direct locals share the
/// MethodEntry scope; a for-loop's init-clause variables own their own ForInit scope (shared across
/// all iterations); for/foreach/while/do BODY locals — including pattern/out-var declarations inside
/// a loop's own condition test (INV-3) — own a per-iteration Iteration scope; switch section locals
/// and case-pattern variables own one Switch scope per switch statement; any other nested block
/// (if/else bodies, bare braces, a using-statement's body block) owns its own Block scope; using
/// declarations, out-vars, and is-pattern variables outside a loop condition are owned by the
/// nearest enclosing scope (wherever the walk currently is — no scope of their own).
/// </summary>
public enum CaptureScopeKind { MethodEntry, ForInit, Iteration, Switch, Block }

/// <summary>One lexical scope node in the raw (unfiltered) scope tree. A scope OWNS a captured
/// variable when that variable is declared directly in it (design: "所有スコープ") — see
/// <see cref="OwnedCaptures"/>. Non-owning ("non-capture-bearing") scopes carry no env at emit time;
/// the runtime chain skips them (<see cref="CaptureScopeAnalysis.EffectiveParent"/>).</summary>
public sealed class CaptureScope
{
    public readonly int Id;
    public readonly CaptureScopeKind Kind;
    public readonly CaptureScope Parent; // raw lexical parent — unfiltered, always the syntactic enclosing scope
    public readonly IOperation Node; // representative operation for this scope's introduction point (source-position lookups)

    /// <summary>Non-null only for a MethodEntry scope that represents a lambda or local function's
    /// OWN body scope (null for the class-level root scope of an ordinary method/field initializer).</summary>
    public IMethodSymbol ClosureSymbol;

    /// <summary>Set only when <see cref="ClosureSymbol"/> is non-null: the nearest enclosing (inclusive
    /// of scopes at the closure's own declaration site) capture-bearing scope — where this closure's
    /// hidden __envp parameter chains to (design §2 BindingScope). Null when the closure's entire
    /// enclosing chain owns no captures (no env needed for this closure).</summary>
    public CaptureScope BindingScope;

    /// <summary>Every local/parameter declared directly in this scope, in declaration (source) order.</summary>
    public readonly List<ISymbol> DeclaredSymbols = new();

    /// <summary>The subset of <see cref="DeclaredSymbols"/> that is captured by some closure, in the
    /// SAME relative order — index + 1 is the env record slot (design §1.1: slot 0 is always parent).</summary>
    public readonly List<ISymbol> OwnedCaptures = new();

    public bool IsCaptureBearing => OwnedCaptures.Count > 0;

    internal CaptureScope(int id, CaptureScopeKind kind, CaptureScope parent, IOperation node)
    {
        Id = id;
        Kind = kind;
        Parent = parent;
        Node = node;
    }

    public override string ToString() => $"#{Id}:{Kind}" + (ClosureSymbol != null ? $"({ClosureSymbol.Name})" : "");
}

public sealed class CaptureScopeAnalysis
{
    public IReadOnlyList<CaptureScope> Scopes { get; }

    /// <summary>Captured symbol (definition-keyed) → (owning scope, slot). Slot is 1-based;
    /// slot 0 in the eventual env record is always the parent link (design §1.1).</summary>
    public IReadOnlyDictionary<ISymbol, (CaptureScope Scope, int Slot)> CapturedSlots { get; }

    /// <summary>Closure method symbol (definition-keyed) → that closure's OWN MethodEntry scope
    /// (read its .BindingScope for the design §2 BindingScope map).</summary>
    public IReadOnlyDictionary<IMethodSymbol, CaptureScope> ClosureScopes { get; }

    internal CaptureScopeAnalysis(
        List<CaptureScope> scopes,
        Dictionary<ISymbol, (CaptureScope, int)> capturedSlots,
        Dictionary<IMethodSymbol, CaptureScope> closureScopes)
    {
        Scopes = scopes;
        CapturedSlots = capturedSlots;
        ClosureScopes = closureScopes;
    }

    /// <summary>Nearest capture-bearing ANCESTOR of <paramref name="scope"/> (strictly above it —
    /// never returns <paramref name="scope"/> itself), skipping non-capture-bearing scopes in the raw
    /// parent chain. Null at the top of the chain. This is the runtime env-record parent link a
    /// capture-bearing scope's EnvAlloc writes into slot 0 (design §3, ParentLeaf).</summary>
    public CaptureScope EffectiveParent(CaptureScope scope)
    {
        var p = scope?.Parent;
        while (p != null && !p.IsCaptureBearing) p = p.Parent;
        return p;
    }

    /// <summary>Static parent-chain hop count from <paramref name="from"/> up to
    /// <paramref name="owner"/> (design §4.1 HopDistance — 0 when they are the same scope, otherwise
    /// one hop per <see cref="EffectiveParent"/> step). Throws if <paramref name="owner"/> is not
    /// actually an ancestor of (or equal to) <paramref name="from"/> in the capture-bearing chain —
    /// callers must resolve a symbol's owner via <see cref="CapturedSlots"/> first.</summary>
    public int HopDistance(CaptureScope from, CaptureScope owner)
    {
        if (from == null) throw new ArgumentNullException(nameof(from));
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        int hops = 0;
        var cur = from;
        while (cur != null && !ReferenceEquals(cur, owner))
        {
            cur = EffectiveParent(cur);
            hops++;
        }
        if (cur == null)
            throw new InvalidOperationException(
                $"CaptureScopeAnalysis.HopDistance: '{owner}' is not an ancestor of '{from}' in the capture-bearing chain.");
        return hops;
    }

    /// <summary>Builds the analysis for one class: every ordinary/generic-definition method body plus
    /// every field initializer, and (transitively, inline) every lambda and local function nested
    /// anywhere inside them. Self-contained — owns its own <see cref="LambdaCaptureAnalyzer"/> and
    /// local-function transitive-capture fixpoint (mirrors UasmEmitter.BuildRecursionInfo's [K2]
    /// fixpoint in miniature) so this module has zero shared mutable state with the emitter
    /// (thread-safe per §12: nothing here is written after Build returns).</summary>
    public static CaptureScopeAnalysis Build(Compilation compilation, INamedTypeSymbol classSymbol)
    {
        var captureAnalyzer = new LambdaCaptureAnalyzer(compilation);

        var roots = classSymbol.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.DeclaringSyntaxReferences.Length > 0
                && m.MethodKind != MethodKind.PropertyGet && m.MethodKind != MethodKind.PropertySet)
            .Select(m => m.IsGenericMethod ? (IMethodSymbol)m.OriginalDefinition : m)
            .Distinct(SymbolEqualityComparer.Default).Cast<IMethodSymbol>()
            .ToList();

        var rootBodies = new List<(IMethodSymbol Root, IOperation Body)>();
        foreach (var root in roots)
        {
            var body = GetOperationBody(compilation, root);
            if (body != null) rootBodies.Add((root, body));
        }

        var fieldInits = CollectFieldInitializerOperations(compilation, classSymbol);

        SeedLocalFunctionCaptureFixpoint(rootBodies.Select(rb => rb.Body).Concat(fieldInits), captureAnalyzer);

        var builder = new Builder(captureAnalyzer);
        foreach (var (root, body) in rootBodies)
        {
            var scope = builder.NewScope(CaptureScopeKind.MethodEntry, null, body);
            foreach (var p in root.Parameters) builder.Declare(p, scope);
            builder.FlattenInto(body, scope);
        }
        foreach (var initOp in fieldInits)
        {
            var scope = builder.NewScope(CaptureScopeKind.MethodEntry, null, initOp);
            builder.FlattenInto(initOp, scope);
        }

        return builder.Finish();
    }

    static IOperation GetOperationBody(Compilation compilation, IMethodSymbol method)
    {
        var syntaxRef = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) return null;
        return compilation.GetSemanticModel(syntaxRef.SyntaxTree).GetOperation(syntaxRef.GetSyntax());
    }

    static List<IOperation> CollectFieldInitializerOperations(Compilation compilation, INamedTypeSymbol classSymbol)
    {
        var result = new List<IOperation>();
        foreach (var field in classSymbol.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsConst || field.IsStatic) continue;
            foreach (var syntaxRef in field.DeclaringSyntaxReferences)
            {
                if (syntaxRef.GetSyntax() is not Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax
                    { Initializer: { Value: { } initSyntax } })
                    continue;
                var model = compilation.GetSemanticModel(syntaxRef.SyntaxTree);
                if (model.GetOperation(initSyntax) is { } initOp) result.Add(initOp);
            }
        }
        return result;
    }

    /// <summary>Miniature standalone twin of UasmEmitter.BuildRecursionInfo's [K2] fixpoint: a local
    /// function converted to a method group is a closure exactly like a lambda, and capture-ness is
    /// TRANSITIVE over the local-function call graph (a wrapper local function over a capturing local
    /// function is the same closure one hop removed). Runs before any GetCaptures/
    /// GetLocalFunctionCaptures call so the lambda-side cache is never pinned pre-fixpoint.</summary>
    static void SeedLocalFunctionCaptureFixpoint(IEnumerable<IOperation> roots, LambdaCaptureAnalyzer captureAnalyzer)
    {
        var lfCaptures = new Dictionary<IMethodSymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);
        var lfInside = new Dictionary<IMethodSymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);
        var lfRefs = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);

        void CollectLocalFunctions(IOperation op, List<IOperation> into)
        {
            if (op == null) return;
            if (op is ILocalFunctionOperation lf && lf.Body != null) into.Add(op);
            foreach (var child in op.Children) CollectLocalFunctions(child, into);
        }

        var lfOps = new List<IOperation>();
        foreach (var root in roots) CollectLocalFunctions(root, lfOps);

        foreach (var lfOp in lfOps)
        {
            var lf = (ILocalFunctionOperation)lfOp;
            var direct = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var inside = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var refs = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            LambdaCaptureAnalyzer.AnalyzeLocalFunction(lf.Symbol, lf.Body, direct, inside, refs);
            lfCaptures[lf.Symbol] = direct;
            lfInside[lf.Symbol] = inside;
            lfRefs[lf.Symbol] = refs;
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var kv in lfRefs)
            {
                var mySet = lfCaptures[kv.Key];
                var myInside = lfInside[kv.Key];
                foreach (var callee in kv.Value)
                {
                    if (!lfCaptures.TryGetValue(callee, out var calleeSet)) continue;
                    if (ReferenceEquals(calleeSet, mySet)) continue;
                    foreach (var s in calleeSet)
                        if (!myInside.Contains(s) && mySet.Add(s)) changed = true;
                }
            }
        }

        var final = new Dictionary<IMethodSymbol, System.Collections.Immutable.ImmutableArray<ISymbol>>(SymbolEqualityComparer.Default);
        foreach (var kv in lfCaptures)
            if (kv.Value.Count > 0) final[kv.Key] = kv.Value.ToImmutableArray();
        captureAnalyzer.SetLocalFunctionCaptures(final);
    }

    /// <summary>Owns the recursive scope-tree walk. One instance per <see cref="Build"/> call — not
    /// shared, not reused.</summary>
    sealed class Builder
    {
        readonly LambdaCaptureAnalyzer _captureAnalyzer;
        readonly List<CaptureScope> _scopes = new();
        readonly Dictionary<ISymbol, CaptureScope> _declaringScope = new(SymbolEqualityComparer.Default);
        readonly Dictionary<IMethodSymbol, CaptureScope> _rawEnclosing = new(SymbolEqualityComparer.Default);
        readonly List<(IMethodSymbol Sym, IAnonymousFunctionOperation Op)> _lambdas = new();
        readonly List<IMethodSymbol> _localFunctions = new();
        int _nextId;

        public Builder(LambdaCaptureAnalyzer captureAnalyzer) { _captureAnalyzer = captureAnalyzer; }

        public CaptureScope NewScope(CaptureScopeKind kind, CaptureScope parent, IOperation node)
        {
            var scope = new CaptureScope(_nextId++, kind, parent, node);
            _scopes.Add(scope);
            return scope;
        }

        public void Declare(ISymbol symbol, CaptureScope scope)
        {
            if (symbol == null || scope == null) return;
            if (_declaringScope.ContainsKey(symbol)) return; // already declared — defensive, shouldn't happen for well-formed C#
            _declaringScope[symbol] = scope;
            scope.DeclaredSymbols.Add(symbol);
        }

        /// <summary>Walk a scope-owning construct's body WITHOUT pushing an extra scope for its own
        /// outermost block — its top-level statements are owned directly by <paramref name="scope"/>
        /// (design: method/closure params + body-direct locals share ONE scope; a loop/switch's own
        /// body-block locals are owned by the loop/switch's iteration/switch scope, not a further-
        /// nested child).</summary>
        public void FlattenInto(IOperation body, CaptureScope scope)
        {
            switch (body)
            {
                case null: return;
                case IBlockOperation block:
                    foreach (var stmt in block.Operations) Walk(stmt, scope);
                    return;
                case IMethodBodyBaseOperation mb:
                    if (mb.BlockBody != null) FlattenInto(mb.BlockBody, scope);
                    if (mb.ExpressionBody != null) Walk(mb.ExpressionBody, scope);
                    return;
                default:
                    Walk(body, scope);
                    return;
            }
        }

        public void Walk(IOperation op, CaptureScope scope)
        {
            switch (op)
            {
                case null:
                    return;

                case IAnonymousFunctionOperation af when af.Symbol != null && af.Body != null:
                {
                    _lambdas.Add((af.Symbol, af));
                    _rawEnclosing[af.Symbol] = scope;
                    var lambdaScope = NewScope(CaptureScopeKind.MethodEntry, scope, af.Body);
                    lambdaScope.ClosureSymbol = af.Symbol;
                    foreach (var p in af.Symbol.Parameters) Declare(p, lambdaScope);
                    FlattenInto(af.Body, lambdaScope);
                    return;
                }

                case ILocalFunctionOperation lf when lf.Symbol != null:
                {
                    _localFunctions.Add(lf.Symbol);
                    _rawEnclosing[lf.Symbol] = scope;
                    if (lf.Body != null)
                    {
                        var lfScope = NewScope(CaptureScopeKind.MethodEntry, scope, lf.Body);
                        lfScope.ClosureSymbol = lf.Symbol;
                        foreach (var p in lf.Symbol.Parameters) Declare(p, lfScope);
                        FlattenInto(lf.Body, lfScope);
                    }
                    return;
                }

                case IForLoopOperation forOp:
                {
                    var forScope = NewScope(CaptureScopeKind.ForInit, scope, forOp);
                    foreach (var b in forOp.Before) Walk(b, forScope);
                    var iterScope = NewScope(CaptureScopeKind.Iteration, forScope, forOp);
                    if (forOp.Condition != null) Walk(forOp.Condition, iterScope);
                    FlattenInto(forOp.Body, iterScope);
                    foreach (var ab in forOp.AtLoopBottom) Walk(ab, iterScope);
                    return;
                }

                case IWhileLoopOperation whileOp:
                {
                    var iterScope = NewScope(CaptureScopeKind.Iteration, scope, whileOp);
                    if (whileOp.Condition != null) Walk(whileOp.Condition, iterScope);
                    FlattenInto(whileOp.Body, iterScope);
                    return;
                }

                case IForEachLoopOperation feOp:
                {
                    // Collection is evaluated once, before the loop starts — owned by the ENCLOSING scope.
                    if (feOp.Collection != null) Walk(feOp.Collection, scope);
                    var iterScope = NewScope(CaptureScopeKind.Iteration, scope, feOp);
                    Walk(feOp.LoopControlVariable, iterScope);
                    FlattenInto(feOp.Body, iterScope);
                    return;
                }

                case ISwitchOperation switchOp:
                {
                    if (switchOp.Value != null) Walk(switchOp.Value, scope);
                    var switchScope = NewScope(CaptureScopeKind.Switch, scope, switchOp);
                    foreach (var c in switchOp.Cases)
                    {
                        foreach (var clause in c.Clauses) Walk(clause, switchScope);
                        foreach (var stmt in c.Body) Walk(stmt, switchScope);
                    }
                    return;
                }

                case IBlockOperation block:
                {
                    var blockScope = NewScope(CaptureScopeKind.Block, scope, block);
                    foreach (var stmt in block.Operations) Walk(stmt, blockScope);
                    return;
                }

                case IVariableDeclarationGroupOperation declGroup:
                    foreach (var decl in declGroup.Declarations) Walk(decl, scope);
                    return;

                case IVariableDeclarationOperation decl:
                    foreach (var d in decl.Declarators) Walk(d, scope);
                    return;

                case IVariableDeclaratorOperation declarator:
                    Declare(declarator.Symbol, scope);
                    if (declarator.Initializer?.Value != null) Walk(declarator.Initializer.Value, scope);
                    return;

                case IUsingDeclarationOperation usingDecl:
                    Walk(usingDecl.DeclarationGroup, scope);
                    return;

                case IUsingOperation usingOp:
                    if (usingOp.Resources != null) Walk(usingOp.Resources, scope);
                    if (usingOp.Body != null) Walk(usingOp.Body, scope);
                    return;

                case IDeclarationExpressionOperation declExpr:
                    DeclareFromExpressionTarget(declExpr.Expression, scope);
                    return;

                case ITupleOperation tup:
                    foreach (var el in tup.Elements) Walk(el, scope);
                    return;

                case IDeclarationPatternOperation declPat:
                    if (declPat.DeclaredSymbol != null) Declare(declPat.DeclaredSymbol, scope);
                    return;

                default:
                    foreach (var child in op.Children) Walk(child, scope);
                    return;
            }
        }

        void DeclareFromExpressionTarget(IOperation expr, CaptureScope scope)
        {
            switch (expr)
            {
                case ILocalReferenceOperation lr: Declare(lr.Local, scope); break;
                case IDeclarationExpressionOperation nested: DeclareFromExpressionTarget(nested.Expression, scope); break;
                // A single `var` covers the WHOLE nested tuple pattern (`var (a, (b, c)) = …`) — each
                // element is either a bare ILocalReferenceOperation or a further-nested ITupleOperation,
                // not individually re-wrapped in its own IDeclarationExpressionOperation. Recurse through
                // the SAME declaring walk (not the general Walk) so nested elements keep being treated
                // as declaration targets at any depth.
                case ITupleOperation tup: foreach (var el in tup.Elements) DeclareFromExpressionTarget(el, scope); break;
                default: break; // IDiscardOperation, or an EXISTING-local deconstruction target — nothing to declare
            }
        }

        public CaptureScopeAnalysis Finish()
        {
            var captured = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            foreach (var (_, op) in _lambdas)
                foreach (var s in _captureAnalyzer.GetCaptures(op)) captured.Add(s);
            foreach (var sym in _localFunctions)
                foreach (var s in _captureAnalyzer.GetLocalFunctionCaptures(sym)) captured.Add(s);

            var capturedSlots = new Dictionary<ISymbol, (CaptureScope, int)>(SymbolEqualityComparer.Default);
            foreach (var scope in _scopes)
            {
                foreach (var sym in scope.DeclaredSymbols)
                {
                    if (!captured.Contains(sym)) continue;
                    var slot = scope.OwnedCaptures.Count + 1;
                    scope.OwnedCaptures.Add(sym);
                    capturedSlots[sym] = (scope, slot);
                }
            }

            var closureScopes = new Dictionary<IMethodSymbol, CaptureScope>(SymbolEqualityComparer.Default);
            foreach (var scope in _scopes)
            {
                if (scope.ClosureSymbol == null) continue;
                closureScopes[scope.ClosureSymbol] = scope;
                var cur = _rawEnclosing.TryGetValue(scope.ClosureSymbol, out var raw) ? raw : null;
                while (cur != null && !cur.IsCaptureBearing) cur = cur.Parent;
                scope.BindingScope = cur;
            }

            return new CaptureScopeAnalysis(_scopes, capturedSlots, closureScopes);
        }
    }
}

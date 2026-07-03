using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// Analyzes lambda capture sets via direct IOperation-tree walking.
///
/// NOTE on Roslyn API: SemanticModel.AnalyzeDataFlow().Captured is not reliable for
/// per-lambda capture extraction in Roslyn 4.3 — analyzing a lambda's body (BlockSyntax)
/// or expression (LambdaExpressionSyntax) returns the captures of ALL lambdas in the
/// enclosing method, not just this lambda. The manual walker scopes captures correctly
/// to the specific IAnonymousFunctionOperation.
///
/// Replaces HandlerBase.HasCaptures (pre-v2.2) with a Symbol-returning analyzer so we
/// can aggregate per-symbol capture lists for emit-time aliasing detection.
/// </summary>
public sealed class LambdaCaptureAnalyzer
{
    readonly Compilation _compilation;
    readonly Dictionary<IAnonymousFunctionOperation, ImmutableArray<ISymbol>> _capturesCache = new();

    // §2.8 round-4 [K2]: TRANSITIVE local-function capture sets (computed by the UasmEmitter
    // pre-scan fixpoint in BuildRecursionInfo, set BEFORE any GetCaptures caller runs). A lambda
    // that invokes/references a capturing local function captures that function's capture set —
    // otherwise wrapper lambdas (`fs[i] = () => Inner();`) launder the closure past every guard
    // (VM-verified silent wrong values). Filtered against the lambda's own `inside` set so a
    // local function capturing only the LAMBDA's locals does not mark the lambda capturing.
    Dictionary<IMethodSymbol, ImmutableArray<ISymbol>> _localFunctionCaptures;

    public LambdaCaptureAnalyzer(Compilation compilation)
    {
        _compilation = compilation;
    }

    /// <summary>Install the transitive local-function capture-set map (UasmEmitter.BuildRecursionInfo
    /// fixpoint, §2.8 round-4 [K2]). Must run before the first GetCaptures call — the per-lambda
    /// cache would otherwise pin a pre-map (non-transitive) result.</summary>
    public void SetLocalFunctionCaptures(Dictionary<IMethodSymbol, ImmutableArray<ISymbol>> map)
    {
        _localFunctionCaptures = map;
    }

    bool TryGetLocalFunctionCaptures(IMethodSymbol m, out ImmutableArray<ISymbol> captures)
    {
        captures = default;
        if (_localFunctionCaptures == null || m == null || m.MethodKind != MethodKind.LocalFunction)
            return false;
        // Symbol identity across semantic models is value-based for local functions
        // (syntax + container), hence the OriginalDefinition fallback.
        return _localFunctionCaptures.TryGetValue(m, out captures)
            || (m.OriginalDefinition != null && _localFunctionCaptures.TryGetValue(m.OriginalDefinition, out captures));
    }

    /// <summary>
    /// Get the symbols (locals / parameters) captured by the lambda from outer scope.
    /// Excludes the lambda's own parameters and locals declared inside the lambda body.
    /// Returns an empty array for body-less lambdas.
    /// </summary>
    public ImmutableArray<ISymbol> GetCaptures(IAnonymousFunctionOperation lambda)
    {
        if (_capturesCache.TryGetValue(lambda, out var cached)) return cached;

        var body = lambda.Body;
        if (body == null)
        {
            _capturesCache[lambda] = ImmutableArray<ISymbol>.Empty;
            return ImmutableArray<ISymbol>.Empty;
        }

        var inside = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var p in lambda.Symbol.Parameters) inside.Add(p);
        foreach (var op in body.DescendantsAndSelf())
        {
            switch (op)
            {
                case IVariableDeclaratorOperation decl:
                    inside.Add(decl.Symbol);
                    break;
                // A symbol declared INSIDE a nested closure (its params, and — via the declarator
                // arm above — its locals) is NOT captured by THIS lambda: the nested closure owns it
                // and chains its own env. Without this, `a => b => a + b` reports `b` as an outer
                // capture, so IsCapturingClosure(outer) is spuriously true (BindingScope=null →
                // ClosureEnvLeaf throws) and `b` is wrongly slotted in the inner scope. Mirrors
                // AnalyzeLocalFunction's nested-closure-param exclusion.
                case IAnonymousFunctionOperation af when af.Symbol != null:
                    foreach (var p in af.Symbol.Parameters) inside.Add(p);
                    break;
                case ILocalFunctionOperation lf when lf.Symbol != null:
                    foreach (var p in lf.Symbol.Parameters) inside.Add(p);
                    break;
            }
        }

        var captures = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var op in body.DescendantsAndSelf())
        {
            switch (op)
            {
                case ILocalReferenceOperation lr when !inside.Contains(lr.Local):
                    captures.Add(lr.Local);
                    break;
                case IParameterReferenceOperation pr when !inside.Contains(pr.Parameter):
                    captures.Add(pr.Parameter);
                    break;
                // §2.8 round-4 [K2]: invoking or referencing a CAPTURING local function makes this
                // lambda capture that function's (transitive) capture set — a wrapper lambda is the
                // same closure one hop removed. Symbols declared inside the lambda are filtered out
                // (a local function over the lambda's own locals runs entirely in this activation).
                case IInvocationOperation inv when TryGetLocalFunctionCaptures(inv.TargetMethod, out var invCaps):
                    foreach (var s in invCaps)
                        if (!inside.Contains(s)) captures.Add(s);
                    break;
                case IMethodReferenceOperation mre when TryGetLocalFunctionCaptures(mre.Method, out var refCaps):
                    foreach (var s in refCaps)
                        if (!inside.Contains(s)) captures.Add(s);
                    break;
            }
        }

        var result = captures.Count == 0 ? ImmutableArray<ISymbol>.Empty : captures.ToImmutableArray();
        _capturesCache[lambda] = result;
        return result;
    }

    public bool HasCaptures(IAnonymousFunctionOperation lambda) => GetCaptures(lambda).Length > 0;

    /// <summary>Wave-9 [W2]: the TRANSITIVE capture set of a local function (empty when capture-free
    /// or unanalyzed). Public twin of <see cref="TryGetLocalFunctionCaptures"/> so a method-group
    /// store can register its captured symbols in the aliasing detector and the per-iteration
    /// escape guard exactly like a lambda's GetCaptures result.</summary>
    public ImmutableArray<ISymbol> GetLocalFunctionCaptures(IMethodSymbol m)
        => TryGetLocalFunctionCaptures(m, out var captures) ? captures : ImmutableArray<ISymbol>.Empty;

    /// <summary>
    /// Analyze a LOCAL FUNCTION body for the §2.8 round-3/round-4 capture machinery. A local
    /// function converted to a method group (IMethodReferenceOperation) is a closure exactly like
    /// a lambda, but it never passes through the lambda analyzer — capturing local functions are
    /// capturing-lambda-equivalent everywhere the escape guards look. Same walker discipline as
    /// GetCaptures: symbols declared inside the body (locals, own params, params of any nested
    /// lambda / local function) are not captures; `this` is excluded by construction (instance
    /// references are not local/param reads). §2.8 round-4 [K2] additionally collects every
    /// local function invoked/referenced in the body so the caller (UasmEmitter.BuildRecursionInfo)
    /// can run a fixpoint: capture-ness is TRANSITIVE over the local-function call graph (a wrapper
    /// `Outer(){return Inner();}` is the same closure one hop removed — VM-verified laundering),
    /// with each hop filtered against the caller's own `inside` set so a callee capturing only the
    /// CALLER's locals does not mark the caller capturing. Static and stateless by design — called
    /// from the UasmEmitter pre-scan with the body from the recursion-info tree family.
    /// </summary>
    public static void AnalyzeLocalFunction(IMethodSymbol symbol, IOperation body,
        HashSet<ISymbol> directCaptures, HashSet<ISymbol> inside, HashSet<IMethodSymbol> localFunctionRefs)
    {
        if (symbol == null || body == null) return;

        foreach (var p in symbol.Parameters) inside.Add(p);
        foreach (var op in body.DescendantsAndSelf())
        {
            switch (op)
            {
                case IVariableDeclaratorOperation decl:
                    inside.Add(decl.Symbol);
                    break;
                case IAnonymousFunctionOperation af when af.Symbol != null:
                    foreach (var p in af.Symbol.Parameters) inside.Add(p);
                    break;
                case ILocalFunctionOperation lf when lf.Symbol != null:
                    foreach (var p in lf.Symbol.Parameters) inside.Add(p);
                    break;
            }
        }

        foreach (var op in body.DescendantsAndSelf())
        {
            switch (op)
            {
                case ILocalReferenceOperation lr when !inside.Contains(lr.Local):
                    directCaptures.Add(lr.Local);
                    break;
                case IParameterReferenceOperation pr when !inside.Contains(pr.Parameter):
                    directCaptures.Add(pr.Parameter);
                    break;
                case IInvocationOperation inv
                    when inv.TargetMethod is { MethodKind: MethodKind.LocalFunction }:
                    localFunctionRefs.Add(inv.TargetMethod.OriginalDefinition ?? inv.TargetMethod);
                    break;
                case IMethodReferenceOperation mre
                    when mre.Method is { MethodKind: MethodKind.LocalFunction }:
                    localFunctionRefs.Add(mre.Method.OriginalDefinition ?? mre.Method);
                    break;
            }
        }
    }
}

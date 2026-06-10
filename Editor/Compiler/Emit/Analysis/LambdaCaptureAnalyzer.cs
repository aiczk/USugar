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

    public LambdaCaptureAnalyzer(Compilation compilation)
    {
        _compilation = compilation;
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
            if (op is IVariableDeclaratorOperation decl) inside.Add(decl.Symbol);
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
            }
        }

        var result = captures.Count == 0 ? ImmutableArray<ISymbol>.Empty : captures.ToImmutableArray();
        _capturesCache[lambda] = result;
        return result;
    }

    public bool HasCaptures(IAnonymousFunctionOperation lambda) => GetCaptures(lambda).Length > 0;

    /// <summary>
    /// Does a LOCAL FUNCTION capture enclosing locals/params? A local function converted to a
    /// method group (IMethodReferenceOperation) is a closure exactly like a lambda, but it never
    /// passes through the lambda analyzer — §2.8 round 3 treats capturing local functions as
    /// capturing-lambda-equivalent everywhere the escape guards look. Same walker discipline as
    /// GetCaptures: symbols declared inside the body (locals, own params, params of any nested
    /// lambda / local function) are not captures; `this` is excluded by construction (instance
    /// references are not local/param reads). Static and stateless by design — called from the
    /// UasmEmitter pre-scan with the body from the recursion-info tree family.
    /// </summary>
    public static bool LocalFunctionHasCaptures(IMethodSymbol symbol, IOperation body)
    {
        if (symbol == null || body == null) return false;

        var inside = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
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
                    return true;
                case IParameterReferenceOperation pr when !inside.Contains(pr.Parameter):
                    return true;
            }
        }
        return false;
    }
}

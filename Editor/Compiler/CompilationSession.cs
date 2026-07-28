using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Explicit shared authority for one Roslyn compilation. Immutable compiler
/// services live here; mutable per-class emission state remains in LoweringState
/// so parallel behaviour emission cannot leak state between classes.
/// </summary>
public sealed class CompilationSession
{
    public Compilation Compilation { get; }
    public UdonAbiCatalog AbiCatalog { get; }
    public UdonTypeFactRegistry TypeFacts { get; }
    internal UdonTypeSystem Types { get; }
    internal ObjectArrayBehaviourAliasCensus ObjectArrayBehaviourAliases { get; }
    internal IReadOnlyList<Diagnostic> BlockingDiagnostics { get; }

    public CompilationSession(Compilation compilation, UdonAbiCatalog abiCatalog)
        : this(compilation, abiCatalog, new UdonTypeFactRegistry())
    {
    }

    internal CompilationSession(Compilation compilation, UdonAbiCatalog abiCatalog,
        UdonTypeFactRegistry typeFacts)
    {
        Compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        BlockingDiagnostics =
            CompilerDiagnosticPolicy.GetBlockingDiagnostics(Compilation);
        AbiCatalog = abiCatalog
            ?? throw new ArgumentNullException(nameof(abiCatalog));
        TypeFacts = typeFacts ?? throw new ArgumentNullException(nameof(typeFacts));
        AbiCatalog.SeedTypeFacts(TypeFacts);
        ObjectArrayBehaviourAliases = ObjectArrayBehaviourAliasCensus.For(Compilation);
        Types = new UdonTypeSystem(
            TypeFacts, ObjectArrayBehaviourAliases, AbiCatalog);
    }
}

/// <summary>
/// Roslyn diagnostics that are warnings for CLR targets but hard errors for
/// USugar because their fallback semantics require an exception that Udon
/// cannot represent.
/// </summary>
internal static class CompilerDiagnosticPolicy
{
    static readonly HashSet<string> UnsupportedExceptionDiagnostics =
        new(StringComparer.Ordinal)
        {
            "CS8509", // non-exhaustive switch expression
            "CS8524", // enum switch misses unnamed values
            "CS8655", // switch expression misses null
            "CS8846", // guarded patterns leave a possible unmatched value
        };

    internal static bool IsBlocking(Diagnostic diagnostic)
        => diagnostic.Severity == DiagnosticSeverity.Error
           || UnsupportedExceptionDiagnostics.Contains(diagnostic.Id);

    internal static IReadOnlyList<Diagnostic> GetBlockingDiagnostics(
        Compilation compilation)
    {
        var diagnostics = compilation.GetDiagnostics();
        var blocking = diagnostics
            .Where(IsBlocking)
            .ToList();
        if (blocking.Any(diagnostic =>
                UnsupportedExceptionDiagnostics.Contains(diagnostic.Id))
            || !ContainsSwitchWithoutDiscard(compilation))
            return blocking;

        // A #pragma or project NoWarn can suppress the CLR warning, but it
        // cannot make SwitchExpressionException implementable on Udon.
        // Re-run diagnostics once on a non-mutating probe compilation with
        // those suppressions removed. This preserves Roslyn's exhaustiveness
        // analysis (so proven-total bool switches remain legal) instead of
        // replacing it with a blunt "must contain _" syntax rule.
        var probe = UnsuppressedExceptionProbe(compilation);
        blocking.AddRange(probe.GetDiagnostics().Where(diagnostic =>
            UnsupportedExceptionDiagnostics.Contains(diagnostic.Id)));
        return blocking;
    }

    static bool ContainsSwitchWithoutDiscard(Compilation compilation)
        => compilation.SyntaxTrees.Any(tree =>
            tree.GetRoot().DescendantNodes()
                .OfType<SwitchExpressionSyntax>()
                .Any(expression => expression.Arms.All(arm =>
                    arm.Pattern is not DiscardPatternSyntax)));

    static Compilation UnsuppressedExceptionProbe(
        Compilation compilation)
    {
        var diagnosticOptions =
            compilation.Options.SpecificDiagnosticOptions;
        foreach (var id in UnsupportedExceptionDiagnostics)
            diagnosticOptions =
                diagnosticOptions.SetItem(id, ReportDiagnostic.Warn);
        var probe = compilation.WithOptions(
            compilation.Options.WithSpecificDiagnosticOptions(
                diagnosticOptions));

        foreach (var tree in compilation.SyntaxTrees)
        {
            var root = tree.GetRoot();
            var pragmas = root.DescendantTrivia(descendIntoTrivia: true)
                .Where(trivia =>
                    trivia.GetStructure()
                    is PragmaWarningDirectiveTriviaSyntax)
                .ToArray();
            if (pragmas.Length == 0)
                continue;
            var withoutPragmas = root.ReplaceTrivia(
                pragmas, (_, _) => default);
            probe = probe.ReplaceSyntaxTree(
                tree,
                tree.WithRootAndOptions(
                    withoutPragmas, tree.Options));
        }
        return probe;
    }

    internal static void RequireSupported(CompilationSession session)
    {
        var unsupported = session.BlockingDiagnostics
            .FirstOrDefault(diagnostic =>
                UnsupportedExceptionDiagnostics.Contains(diagnostic.Id));
        if (unsupported == null)
            return;

        throw new NotSupportedException(
            $"Roslyn {unsupported.Id} is an error in USugar: "
            + "a non-exhaustive switch expression requires "
            + "SwitchExpressionException when no arm matches, but Udon has "
            + "no exception semantics. Make the switch exhaustive, normally "
            + "with a discard (`_`) arm. "
            + unsupported.GetMessage());
    }
}

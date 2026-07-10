using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

public static class Program
{
    public static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "dumps";
        Directory.CreateDirectory(outDir);

        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p) && p.EndsWith(".dll"))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        foreach (var probe in CfgProbes.All)
        {
            var sb = new StringBuilder();
            sb.AppendLine("### " + probe.Name);
            try
            {
                DumpProbe(probe.Source, refs, sb);
            }
            catch (Exception ex)
            {
                sb.AppendLine("DRIVER EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            }
            File.WriteAllText(Path.Combine(outDir, probe.Name + ".txt"), sb.ToString());
        }
        Console.WriteLine("wrote " + CfgProbes.All.Count + " dumps to " + Path.GetFullPath(outDir));
    }

    static void DumpProbe(string source, List<MetadataReference> refs, StringBuilder sb)
    {
        // C#9 = Unity 2022.3 language level; both Roslyn versions must parse identically.
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp9));
        var comp = CSharpCompilation.Create("probe", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            sb.AppendLine("COMPILE ERRORS:");
            foreach (var d in errors) sb.AppendLine("  " + d);
            return;
        }

        var model = comp.GetSemanticModel(tree);
        foreach (var m in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (m.Identifier.Text != "M") continue;
            var op = model.GetOperation(m);
            if (op is not IMethodBodyBaseOperation body)
            {
                sb.AppendLine("-- method " + m.Identifier.Text + ": no IMethodBodyOperation (got " + (op?.Kind.ToString() ?? "null") + ")");
                continue;
            }
            sb.AppendLine("--- method " + m.Identifier.Text + " ---");
            var cfg = ControlFlowGraph.Create((IMethodBodyOperation)body);
            DumpCfg(cfg, sb, "");
        }
    }

    static void DumpCfg(ControlFlowGraph cfg, StringBuilder sb, string indent)
    {
        foreach (var b in cfg.Blocks)
        {
            var region = b.EnclosingRegion != null ? " region=" + b.EnclosingRegion.Kind : "";
            sb.AppendLine(indent + "B" + b.Ordinal + " [" + b.Kind + "]" + region);
            foreach (var op in b.Operations)
                DumpOp(op, sb, indent + "  ");
            if (b.BranchValue != null)
            {
                sb.AppendLine(indent + "  BranchValue:");
                DumpOp(b.BranchValue, sb, indent + "    ");
            }
            if (b.ConditionalSuccessor?.Destination != null)
                sb.AppendLine(indent + "  -> B" + b.ConditionalSuccessor.Destination.Ordinal + " when " + b.ConditionKind);
            if (b.FallThroughSuccessor?.Destination != null)
                sb.AppendLine(indent + "  -> B" + b.FallThroughSuccessor.Destination.Ordinal + " (" + b.FallThroughSuccessor.Semantics + ")");
        }

        foreach (var lf in cfg.LocalFunctions)
        {
            sb.AppendLine(indent + "== local function " + lf.Name + " ==");
            DumpCfg(cfg.GetLocalFunctionControlFlowGraph(lf), sb, indent + "  ");
        }

        var anons = cfg.Blocks
            .SelectMany(b => b.Operations.Concat(b.BranchValue != null ? new[] { b.BranchValue } : Enumerable.Empty<IOperation>()))
            .SelectMany(Flatten)
            .OfType<IFlowAnonymousFunctionOperation>()
            .ToList();
        int i = 0;
        foreach (var a in anons)
        {
            sb.AppendLine(indent + "== anonymous function #" + i++ + " ==");
            DumpCfg(cfg.GetAnonymousFunctionControlFlowGraph(a), sb, indent + "  ");
        }
    }

    static IEnumerable<IOperation> Flatten(IOperation op)
    {
        yield return op;
        foreach (var c in Children(op))
            foreach (var d in Flatten(c))
                yield return d;
    }

    static IEnumerable<IOperation> Children(IOperation op)
    {
#pragma warning disable CS0618 // Children is the only child API on Roslyn 3.10
        return op.Children;
#pragma warning restore CS0618
    }

    static void DumpOp(IOperation op, StringBuilder sb, string indent)
    {
        var syntax = op.Syntax.ToString().Replace("\r", "").Replace("\n", " ");
        if (syntax.Length > 48) syntax = syntax.Substring(0, 45) + "...";
        string extra = op switch
        {
            IFlowCaptureOperation fc => " cap#" + fc.Id.GetHashCode(),
            IFlowCaptureReferenceOperation fr => " capref#" + fr.Id.GetHashCode(),
            _ => ""
        };
        sb.AppendLine(indent + op.Kind + extra + " [" + (op.Type?.ToDisplayString() ?? "-") + "] `" + syntax + "`");
        foreach (var c in Children(op))
            DumpOp(c, sb, indent + "  ");
    }
}

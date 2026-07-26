using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace USugar.Tests;

public class EditorSyntaxGuardTests
{
    [Fact]
    public void DemandedDelegateBridgesCannotBeSilentlySkipped()
    {
        var packageRoot = FindPackageRoot();
        var path = Path.Combine(
            packageRoot, "Editor", "Compiler", "Emit",
            "DelegateBridgeEmitter.cs");
        var source = File.ReadAllText(path);

        Assert.DoesNotContain(
            "TryResolveTarget(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "RequireTarget(",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnityIntegrationSourcesParseAsCSharp9()
    {
        var packageRoot = FindPackageRoot();
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp9);
        var failures = Directory.GetFiles(
                Path.Combine(packageRoot, "Editor"), "*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(path => CSharpSyntaxTree.ParseText(
                    File.ReadAllText(path), parseOptions, path)
                .GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.ToString()))
            .ToArray();

        Assert.True(failures.Length == 0,
            "Editor integration sources must remain valid Unity C# 9:\n"
            + string.Join("\n", failures));
    }

    [Fact]
    public void HandwrittenCompilerTypesDoNotUsePartialDeclarations()
    {
        var packageRoot = FindPackageRoot();
        var compilerRoot = Path.Combine(packageRoot, "Editor", "Compiler");
        var failures = Directory.GetFiles(
                compilerRoot, "*.cs", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(path =>
            {
                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
                return tree.GetRoot()
                    .DescendantNodes()
                    .OfType<TypeDeclarationSyntax>()
                    .Where(type => type.Modifiers.Any(SyntaxKind.PartialKeyword))
                    .Select(type =>
                    {
                        var line = type.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        return $"{Path.GetRelativePath(packageRoot, path)}:{line}: {type.Identifier.ValueText}";
                    });
            })
            .ToArray();

        Assert.True(failures.Length == 0,
            "Compiler implementation types must be cohesive composition units, not partial declarations. "
            + "This does not restrict partial types in compiled user source.\n"
            + string.Join("\n", failures));
    }

    [Fact]
    public void BodyLoweringCannotReachTheAbiCatalogBinder()
    {
        var packageRoot = FindPackageRoot();
        var emitRoot = Path.Combine(packageRoot, "Editor", "Compiler", "Emit");
        var failures = Directory.GetFiles(emitRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !string.Equals(
                Path.GetFileName(path), "BoundAbiPlan.cs",
                StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (line, index))
                .Where(item =>
                    item.line.Contains("UdonAbiBinder", StringComparison.Ordinal)
                    || item.line.Contains(".Abi.Bind", StringComparison.Ordinal)
                    || item.line.Contains(".Abi.TryBind", StringComparison.Ordinal)
                    || item.line.Contains(".AbiCatalog.", StringComparison.Ordinal))
                .Select(item =>
                    $"{Path.GetRelativePath(packageRoot, path)}:{item.index + 1}: "
                    + item.line.Trim()))
            .ToArray();

        Assert.True(failures.Length == 0,
            "Body lowering must consume BoundProgram.Abi; only BoundAbiPlan may reach the "
            + "catalog binder or probe catalog membership.\n"
            + string.Join("\n", failures));
    }

    [Fact]
    public void BodyHandlersCannotRecomputeSourceDispatch()
    {
        var packageRoot = FindPackageRoot();
        var emitRoot = Path.Combine(packageRoot, "Editor", "Compiler", "Emit");
        var roots = new[]
        {
            Path.Combine(emitRoot, "Handlers"),
            Path.Combine(emitRoot, "LValueLowerer.cs"),
            Path.Combine(emitRoot, "LoweringServices.cs"),
            Path.Combine(emitRoot, "ReceiverBridgeEmitter.cs"),
        };
        var files = roots.SelectMany(path => Directory.Exists(path)
                ? Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories)
                : new[] { path })
            .OrderBy(path => path, StringComparer.Ordinal);
        var failures = files.SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (line, index))
                .Where(item =>
                    item.line.Contains("VirtualDispatch.Resolve", StringComparison.Ordinal)
                    || item.line.Contains("EdgeResolver.ResolveCallableSite", StringComparison.Ordinal))
                .Select(item =>
                    $"{Path.GetRelativePath(packageRoot, path)}:{item.index + 1}: "
                    + item.line.Trim()))
            .ToArray();

        Assert.True(failures.Length == 0,
            "Body handlers must consume BoundProgram.CallSites; source dispatch is resolved only "
            + "while constructing the bound program.\n"
            + string.Join("\n", failures));
    }

    [Fact]
    public void BodyLoweringCannotQueryRoslynSemanticModels()
    {
        var packageRoot = FindPackageRoot();
        var emitRoot = Path.Combine(packageRoot, "Editor", "Compiler", "Emit");
        var roots = new[]
        {
            Path.Combine(emitRoot, "Handlers"),
            Path.Combine(emitRoot, "LValueLowerer.cs"),
            Path.Combine(emitRoot, "LoweringServices.cs"),
        };
        var forbidden = new[]
        {
            "GetSemanticModel",
            "GetOperation(",
            "GetDeconstructionInfo",
            "GetEnclosingSymbol",
        };
        var failures = roots.SelectMany(path => Directory.Exists(path)
                ? Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories)
                : new[] { path })
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (line, index))
                .Where(item => forbidden.Any(token =>
                    item.line.Contains(token, StringComparison.Ordinal)))
                .Select(item =>
                    $"{Path.GetRelativePath(packageRoot, path)}:{item.index + 1}: "
                    + item.line.Trim()))
            .ToArray();

        Assert.True(failures.Length == 0,
            "Body lowering must consume materialized BoundProgram semantics and cannot query "
            + "Roslyn SemanticModel services.\n"
            + string.Join("\n", failures));
    }

    [Fact]
    public void BodyLoweringCannotReachLiveSemanticAuthorities()
    {
        var packageRoot = FindPackageRoot();
        var emitRoot = Path.Combine(
            packageRoot, "Editor", "Compiler", "Emit");
        var roots = new[]
        {
            Path.Combine(emitRoot, "Handlers"),
            Path.Combine(emitRoot, "LValueLowerer.cs"),
            Path.Combine(emitRoot, "LoweringServices.cs"),
            Path.Combine(emitRoot, "BoundaryChecker.cs"),
            Path.Combine(emitRoot, "DelegateBridgeEmitter.cs"),
            Path.Combine(emitRoot, "ReceiverBridgeEmitter.cs"),
            Path.Combine(emitRoot, "WrapperBridgeEmitter.cs"),
            Path.Combine(emitRoot, "MulticastDelegateEmitter.cs"),
        };
        var forbidden = new[]
        {
            ".Session.Types",
            "MaterializingUdonTypeSystem",
            "UdonAbiBinder",
            "UdonAbiCatalog",
            "ClassifyConversion",
            "TryGetConstFieldInitializer",
            "RecordSourceLowering",
            "VirtualDispatch.FindAccessor(",
            "VirtualDispatch.IsDispatchSite(",
        };
        var failures = roots.SelectMany(path => Directory.Exists(path)
                ? Directory.GetFiles(
                    path, "*.cs", SearchOption.AllDirectories)
                : new[] { path })
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (line, index))
                .Where(item => forbidden.Any(token =>
                    item.line.Contains(
                        token, StringComparison.Ordinal)))
                .Select(item =>
                    $"{Path.GetRelativePath(packageRoot, path)}:"
                    + $"{item.index + 1}: {item.line.Trim()}"))
            .ToArray();

        Assert.True(failures.Length == 0,
            "Body lowering must consume only frozen BoundProgram semantics; "
            + "live catalog, type-system, and Roslyn classification "
            + "authorities are planning-only.\n"
            + string.Join("\n", failures));
    }

    [Fact]
    public void LoweringStateDoesNotFallbackBetweenSemanticPhases()
    {
        var packageRoot = FindPackageRoot();
        var path = Path.Combine(
            packageRoot, "Editor", "Compiler", "Emit",
            "LoweringState.cs");
        var source = File.ReadAllText(path);
        var forbidden = new[]
        {
            "Program?.",
            "Program != null ?",
            "Program == null ?",
            "_plannedClosure",
            "_plannedCapture",
            "_plannedRecursion",
            "TypeEnvironment.CloseType",
        };
        var failures = forbidden.Where(token =>
            source.Contains(token, StringComparison.Ordinal))
            .ToArray();

        Assert.True(failures.Length == 0,
            "LoweringState must transition its authorities when BoundProgram is published; "
            + "a read may not choose between planning and emission data as a fallback.\n"
            + string.Join("\n", failures));
    }

    [Fact]
    public void CallableRegistrationIsOwnedBySpecializationRegistrar()
    {
        var packageRoot = FindPackageRoot();
        var emitRoot = Path.Combine(
            packageRoot, "Editor", "Compiler", "Emit");
        var allowed = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(emitRoot, "LoweringServices.cs"),
            Path.Combine(emitRoot, "SpecializationRegistrar.cs"),
        };
        var forbidden = new[]
        {
            "RegisterSpecializationForPlanning(",
            "RegisterClosureForPlanning(",
        };
        var failures = Directory.GetFiles(
                emitRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !allowed.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (line, index))
                .Where(item => forbidden.Any(token =>
                    item.line.Contains(token, StringComparison.Ordinal)))
                .Select(item =>
                    $"{Path.GetRelativePath(packageRoot, path)}:{item.index + 1}: "
                    + item.line.Trim()))
            .ToArray();

        Assert.True(failures.Length == 0,
            "Only SpecializationRegistrar may invoke callable registration; "
            + "emission must consume the bound callable set.\n"
            + string.Join("\n", failures));
    }

    [Fact]
    public void BodyHandlersCannotPlanSyntheticHelpers()
    {
        var packageRoot = FindPackageRoot();
        var handlers = Path.Combine(
            packageRoot, "Editor", "Compiler", "Emit", "Handlers");
        var forbidden = new[]
        {
            "PlanMulticastSig(",
            "PlanWrapperSig(",
            "PlanEnumToStringDemand(",
            ".SyntheticDemandPlanner.Register",
        };
        var failures = Directory.GetFiles(
                handlers, "*.cs", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (line, index))
                .Where(item => forbidden.Any(token =>
                    item.line.Contains(token, StringComparison.Ordinal)))
                .Select(item =>
                    $"{Path.GetRelativePath(packageRoot, path)}:{item.index + 1}: "
                    + item.line.Trim()))
            .ToArray();

        Assert.True(failures.Length == 0,
            "Body handlers may only require synthetic helpers already present in "
            + "BoundProgram.SyntheticDemands.\n"
            + string.Join("\n", failures));
    }

    static string FindPackageRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current != null;
             current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Editor"))
                && Directory.Exists(Path.Combine(current.FullName, "Editor~")))
                return current.FullName;
        }
        throw new DirectoryNotFoundException(
            $"Could not find the USugar package root above '{AppContext.BaseDirectory}'.");
    }
}

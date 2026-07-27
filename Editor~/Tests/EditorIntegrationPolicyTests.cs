using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace USugar.Tests;

public class EditorIntegrationPolicyTests
{
    class ProxyBase
    {
    }

    sealed class CompleteProxy : ProxyBase
    {
        public int Direct = 0;
        public int Auto { get; set; }
        public event Action Changed;
        [NonSerialized] public int EditorOnly = 0;
    }

    sealed class UnknownProxy : ProxyBase
    {
        public int Missing = 0;
    }

    [Fact]
    public void AcceptsAssetsAndPackageCSharpSources()
    {
        Assert.True(USugarEditorIntegrationPolicy.IsCSharpSource("Assets/Scripts/Test.cs"));
        Assert.True(USugarEditorIntegrationPolicy.IsCSharpSource("Packages/com.example/Test.CS"));
        Assert.True(USugarEditorIntegrationPolicy.IsCSharpSource(
            "Library/PackageCache/com.example@1.0.0/Runtime/Test.cs"));
        Assert.False(USugarEditorIntegrationPolicy.IsCSharpSource("Assets/Scripts/Test.asmdef"));
    }

    [Fact]
    public void NormalizesProjectRelativeAndAbsolutePathsToOneIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "usugar-project");
        var relative = Path.Combine("Packages", "com.example", "Runtime", "Test.cs");
        var absolute = Path.Combine(root, relative);

        Assert.Equal(
            USugarEditorIntegrationPolicy.NormalizeSourcePath(root, absolute),
            USugarEditorIntegrationPolicy.NormalizeSourcePath(root, relative),
            ignoreCase: true);
    }

    [Fact]
    public void SourceAssemblyClosureCrossesOnlyExplicitDomainEdges()
    {
        var references =
            new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["Assembly-CSharp"] = new[]
                {
                    "Udon.Library",
                    "Unity.InputSystem",
                },
                ["Udon.Library"] = new[] { "Udon.Data" },
                ["Unity.InputSystem"] = new[] { "Udon.HiddenBehindMetadata" },
                ["Udon.Data"] = Array.Empty<string>(),
                ["Udon.HiddenBehindMetadata"] = Array.Empty<string>(),
            };
        var domain = new HashSet<string>(
            new[]
            {
                "Assembly-CSharp",
                "Udon.Library",
                "Udon.Data",
                "Udon.HiddenBehindMetadata",
            },
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            new[] { "Assembly-CSharp", "Udon.Data", "Udon.Library" },
            USugarEditorIntegrationPolicy.SelectSourceAssemblyClosure(
                "Assembly-CSharp", references, domain));
    }

    [Fact]
    public void SourceAssemblyClosureRejectsAnUnownedRoot()
    {
        var references =
            new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["Foreign"] = Array.Empty<string>(),
            };
        var domain = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            USugarEditorIntegrationPolicy.SelectSourceAssemblyClosure(
                "Foreign", references, domain);
        });
        Assert.Contains("outside the USugar source domain", error.Message);
    }

    [Fact]
    public void PreprocessorDefinesPreserveTheRequestedBuildMode()
    {
        var assemblyDefines = new[]
        {
            "UNITY_STANDALONE",
            "CUSTOM_ASSEMBLY",
            "UNITY_EDITOR",
        };
        var editorDefines = new[]
        {
            "UNITY_EDITOR",
            "UNITY_EDITOR_WIN",
            "CUSTOM_PROJECT",
        };

        var editor =
            USugarEditorIntegrationPolicy.SelectPreprocessorDefines(
                assemblyDefines, editorDefines, editorBuild: true);
        Assert.Contains("UNITY_EDITOR", editor);
        Assert.Contains("UNITY_EDITOR_WIN", editor);
        Assert.Contains("CUSTOM_ASSEMBLY", editor);
        Assert.Contains("CUSTOM_PROJECT", editor);

        var client =
            USugarEditorIntegrationPolicy.SelectPreprocessorDefines(
                assemblyDefines, editorDefines, editorBuild: false);
        Assert.DoesNotContain(
            client,
            define => define.StartsWith(
                "UNITY_EDITOR",
                StringComparison.Ordinal));
        Assert.Contains("CUSTOM_ASSEMBLY", client);
        Assert.Contains("CUSTOM_PROJECT", client);
        Assert.Contains("COMPILER_UDONSHARP", client);
        Assert.Contains("UDONSHARP", client);
    }

    [Fact]
    public void ExactIndexRejectsMissingAndAmbiguousBindings()
    {
        var index = new USugarExactSourcePathIndex<string>();
        index.Add(@"C:\Project\Assets\A.cs", "first");
        index.Add(@"C:\Project\Assets\A.cs", "second");

        Assert.False(index.TryResolveUnique(
            @"C:\Project\Assets\Missing.cs", out _, out var missing));
        Assert.Contains("No program asset", missing);

        Assert.False(index.TryResolveUnique(
            @"C:\Project\Assets\A.cs", out _, out var ambiguous));
        Assert.Contains("ambiguous", ambiguous);
    }

    [Fact]
    public void ExactIndexDoesNotFallBackByClassNameOrSuffix()
    {
        var index = new USugarExactSourcePathIndex<string>();
        index.Add(@"C:\Project\Assets\One\Shared.cs", "one");

        Assert.False(index.TryResolveUnique(
            @"C:\Project\Assets\Two\Shared.cs", out _, out _));
    }

    [Fact]
    public void ExactIndexResolvesOneAssetAcrossPartialDeclarations()
    {
        var index = new USugarExactSourcePathIndex<string>();
        index.Add(@"C:\Project\Assets\Main.cs", "program");

        Assert.True(index.TryResolveUnique(
            new[]
            {
                @"C:\Project\Assets\Part.cs",
                @"C:\Project\Assets\Main.cs",
            },
            out var program,
            out var error));
        Assert.Equal("program", program);
        Assert.Null(error);
    }

    [Fact]
    public void ExactIndexExposesAllCandidatesWithoutFallback()
    {
        var index = new USugarExactSourcePathIndex<string>();
        index.Add(@"C:\Project\Assets\Main.cs", "first");
        index.Add(@"C:\Project\Assets\Main.cs", "second");

        Assert.Equal(
            new[] { "first", "second" },
            index.GetCandidates(new[]
            {
                @"C:\Project\Assets\Helper.cs",
                @"C:\Project\Assets\Main.cs",
            }).OrderBy(value => value));
    }

    [Fact]
    public void ProgramRootPolicyIgnoresHelpersAndRejectsGenericProgramRoots()
    {
        var compilation = TestHelper.BuildCompilation(@"
using UdonSharp;
public class ConcreteProgram : UdonSharpBehaviour { }
public class GenericHelper<T> : UdonSharpBehaviour { }
", "ConcreteProgram", out var concrete);
        var generic = compilation.GetTypeByMetadataName("GenericHelper`1");

        Assert.False(USugarProgramRootPolicy.ShouldEmit(
            concrete, hasProgramAsset: false, out var helperError));
        Assert.Null(helperError);
        Assert.True(USugarProgramRootPolicy.ShouldEmit(
            concrete, hasProgramAsset: true, out var concreteError));
        Assert.Null(concreteError);
        Assert.False(USugarProgramRootPolicy.ShouldEmit(
            generic, hasProgramAsset: true, out var genericError));
        Assert.Contains("Generic UdonSharpBehaviour", genericError);
    }

    [Fact]
    public void CompileRequestStateStartsUnknownAndInvalidatesCleanResults()
    {
        var state = new USugarCompileRequestState();
        Assert.Equal(USugarCompileHealth.Unknown, state.Health);

        var first = state.Request();
        state.MarkClean();
        state.Complete(first);
        Assert.Equal(USugarCompileHealth.Clean, state.Health);
        Assert.False(state.HasPendingRequest);

        state.Request();
        Assert.Equal(USugarCompileHealth.Unknown, state.Health);
        Assert.True(state.HasPendingRequest);
    }

    [Fact]
    public void CompletingOlderCompileDoesNotConsumeRequestMadeWhileRunning()
    {
        var state = new USugarCompileRequestState();
        var running = state.Request();
        state.Request();

        state.MarkClean();
        state.Complete(running);

        Assert.True(state.HasPendingRequest);
        Assert.Equal(1, state.CompiledVersion);
        Assert.Equal(2, state.RequestedVersion);
        Assert.Equal(USugarCompileHealth.Unknown, state.Health);
    }

    [Fact]
    public void OpaqueStoragePolicySeparatesSupportedJaggedArraysFromAbiBundles()
    {
        static bool IsExtern(Type type) => type == typeof(int);
        static bool IsBehaviour(Type type) => false;

        Assert.False(USugarEditorIntegrationPolicy.RequiresOpaqueObjectArrayStorage(
            typeof(object[]), typeof(object[]), IsExtern, IsBehaviour));
        Assert.False(USugarEditorIntegrationPolicy.RequiresOpaqueObjectArrayStorage(
            typeof(int[][]), typeof(object[]), IsExtern, IsBehaviour));
        Assert.True(USugarEditorIntegrationPolicy.RequiresOpaqueObjectArrayStorage(
            typeof(Action), typeof(object[]), IsExtern, IsBehaviour));
        Assert.True(USugarEditorIntegrationPolicy.RequiresOpaqueObjectArrayStorage(
            typeof(Action[][]), typeof(object[]), IsExtern, IsBehaviour));
    }

    [Fact]
    public void ProxyProjectionClassifiesExactBackingAndEditorOnlyFields()
    {
        var sourceFields = new Dictionary<string, Type>
        {
            [nameof(CompleteProxy.Direct)] = typeof(int),
            [nameof(CompleteProxy.Auto)] = typeof(int),
            [nameof(CompleteProxy.Changed)] = typeof(Action),
        };

        var projection =
            USugarEditorIntegrationPolicy.ProjectProxyFields(
                typeof(CompleteProxy),
                typeof(ProxyBase),
                sourceFields);

        Assert.Contains(projection, item =>
            item.Field.Name == nameof(CompleteProxy.Direct)
            && item.SourceName == nameof(CompleteProxy.Direct)
            && !item.IsProxyOnly);
        Assert.Contains(projection, item =>
            item.Field.Name == "<Auto>k__BackingField"
            && item.SourceName == nameof(CompleteProxy.Auto)
            && !item.IsProxyOnly);
        Assert.Contains(projection, item =>
            item.Field.Name == nameof(CompleteProxy.Changed)
            && item.SourceName == nameof(CompleteProxy.Changed)
            && !item.IsProxyOnly);
        Assert.Contains(projection, item =>
            item.Field.Name == nameof(CompleteProxy.EditorOnly)
            && item.SourceName == null
            && item.IsProxyOnly);
    }

    [Fact]
    public void ProxyProjectionRejectsAnUnclassifiedClrField()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            USugarEditorIntegrationPolicy.ProjectProxyFields(
                typeof(UnknownProxy),
                typeof(ProxyBase),
                new Dictionary<string, Type>()));

        Assert.Contains("has no compiler-owned Udon field", error.Message);
    }

    [Fact]
    public void ProxyProjectionRejectsATypeMismatch()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            USugarEditorIntegrationPolicy.ProjectProxyFields(
                typeof(UnknownProxy),
                typeof(ProxyBase),
                new Dictionary<string, Type>
                {
                    [nameof(UnknownProxy.Missing)] = typeof(string),
                }));

        Assert.Contains("has type 'System.Int32'", error.Message);
        Assert.Contains("has type 'System.String'", error.Message);
    }

    [Fact]
    public void NetworkMetadataPolicyIncludesInheritedCallableMethods()
    {
        var compilation = TestHelper.BuildCompilation(@"
using UdonSharp;
using VRC.SDK3.UdonNetworkCalling;
public class MetadataBase : UdonSharpBehaviour
{
    [NetworkCallable]
    public void Notify(int value) { }
}
public class MetadataDerived : MetadataBase { }
", "MetadataDerived", out var derived);
        var planner = new LayoutPlanBuilder(compilation).Build();

        var callable = Assert.Single(
            USugarNetworkMetadataPolicy.GetCallableMethods(planner.GetLayout(derived)));

        Assert.Equal("Notify", callable.Key.Name);
        Assert.Equal("MetadataBase", callable.Key.ContainingType.Name);
        Assert.Equal("Notify", callable.Value.ExportName);
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace USugar.Tests;

public class CompilationSessionTests
{
    [Fact]
    public void TypeFactsAreOwnedByOneExplicitSession()
    {
        var compilation = CreateCompilation(
            "namespace VRC.Sample { public enum ForeignMode { A, B } }");
        var mode = compilation.GetTypeByMetadataName("VRC.Sample.ForeignMode");
        var first = new CompilationSession(compilation, TestHelper.RegistryFacts);
        var second = new CompilationSession(compilation, TestHelper.RegistryFacts);

        Assert.Equal("VRCSampleForeignMode", first.Types.GetUdonTypeName(mode));
        Assert.True(first.TypeFacts.IsEnumFact("VRCSampleForeignMode"));
        Assert.Null(second.TypeFacts.IsEnumFact("VRCSampleForeignMode"));
    }

    [Fact]
    public void PureTypeNameResolutionDoesNotMutateSessionFacts()
    {
        var compilation = CreateCompilation(
            "namespace VRC.Sample { public enum ForeignMode { A, B } }");
        var type = compilation.GetTypeByMetadataName("VRC.Sample.ForeignMode");
        var session = new CompilationSession(compilation, TestHelper.RegistryFacts);

        Assert.Equal("VRCSampleForeignMode", ExternResolver.GetUdonTypeName(type));
        Assert.Null(session.TypeFacts.IsEnumFact("VRCSampleForeignMode"));

        Assert.Equal("VRCSampleForeignMode", session.Types.GetUdonTypeName(type));
        Assert.True(session.TypeFacts.IsEnumFact("VRCSampleForeignMode"));
    }

    [Fact]
    public void RegisteredNameCannotLaunderSourceClassIntoSdkType()
    {
        var compilation = CreateCompilation(
            "namespace UserCode { public class Resource { public int Value; } }");
        var type = compilation.GetTypeByMetadataName("UserCode.Resource");
        const string externName =
            "UserCodeResource.__get_Value__SystemInt32";
        var catalog = UdonAbiCatalog.FromNamesForTests(new[] { externName });
        var session = new CompilationSession(compilation, catalog);

        Assert.True(TypeClassifier.IsUserClass(type));
        Assert.Equal(StorageTypes.ObjectArray,
            session.Types.GetStorageType(type));
    }

    static CSharpCompilation CreateCompilation(string source)
        => CSharpCompilation.Create(
            "SessionTests",
            new[] { CSharpSyntaxTree.ParseText(source) },
            TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
}

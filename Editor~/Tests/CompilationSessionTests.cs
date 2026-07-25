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
            "namespace VRC.Sample { public class ForeignResource { } }");
        var resource = compilation.GetTypeByMetadataName("VRC.Sample.ForeignResource");
        var first = new CompilationSession(compilation, TestHelper.RegistryFacts);
        var second = new CompilationSession(compilation, TestHelper.RegistryFacts);

        Assert.Equal("VRCSampleForeignResource",
            first.Types.GetUdonTypeName(resource));
        Assert.True(first.TypeFacts.IsReferenceFact("VRCSampleForeignResource"));
        Assert.Null(second.TypeFacts.IsReferenceFact("VRCSampleForeignResource"));
    }

    [Fact]
    public void CanonicalTypeIdentityDoesNotMutateSessionFacts()
    {
        var compilation = CreateCompilation(
            "namespace VRC.Sample { public class ForeignResource { } }");
        var type = compilation.GetTypeByMetadataName("VRC.Sample.ForeignResource");
        var session = new CompilationSession(compilation, TestHelper.RegistryFacts);

        Assert.Equal("VRCSampleForeignResource",
            UdonTypeIdentity.From(type).Name);
        Assert.Null(session.TypeFacts.IsReferenceFact("VRCSampleForeignResource"));

        Assert.Equal("VRCSampleForeignResource",
            session.Types.GetUdonTypeName(type));
        Assert.True(session.TypeFacts.IsReferenceFact("VRCSampleForeignResource"));
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

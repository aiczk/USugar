using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace USugar.Tests;

public class ClrTypeResolverTests
{
    public sealed class Outer<TOuter>
    {
        public sealed class Inner<TInner> { }
        public sealed class NonGenericInner { }
    }

    static CSharpCompilation Compilation()
    {
        var references = TestHelper.StandardRefs
            .Concat(new[] {
                MetadataReference.CreateFromFile(typeof(ClrTypeResolverTests).Assembly.Location)
            });
        return CSharpCompilation.Create(
            "ClrTypeResolverSymbolSource",
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    [Fact]
    public void ResolvesNestedMetadataName()
    {
        var compilation = Compilation();
        var symbol = compilation.GetTypeByMetadataName(
            "USugar.Tests.ClrTypeResolverTests+Outer`1+NonGenericInner");

        Assert.Equal(
            "USugar.Tests.ClrTypeResolverTests+Outer`1+NonGenericInner",
            ClrTypeResolver.GetMetadataName(symbol));
    }

    [Fact]
    public void ResolvesConstructedNestedGenericType()
    {
        var compilation = Compilation();
        var outerDefinition = compilation.GetTypeByMetadataName(
            "USugar.Tests.ClrTypeResolverTests+Outer`1");
        var outer = outerDefinition.Construct(compilation.GetSpecialType(SpecialType.System_Int32));
        var innerDefinition = outer.GetTypeMembers("Inner").Single();
        var inner = innerDefinition.Construct(compilation.GetSpecialType(SpecialType.System_String));

        Assert.Equal(typeof(Outer<int>.Inner<string>), ClrTypeResolver.Resolve(inner));
    }

    [Fact]
    public void ResolvesNonGenericTypeInsideConstructedGenericType()
    {
        var compilation = Compilation();
        var outerDefinition = compilation.GetTypeByMetadataName(
            "USugar.Tests.ClrTypeResolverTests+Outer`1");
        var outer = outerDefinition.Construct(compilation.GetSpecialType(SpecialType.System_Int32));
        var inner = outer.GetTypeMembers("NonGenericInner").Single();

        Assert.Equal(typeof(Outer<int>.NonGenericInner), ClrTypeResolver.Resolve(inner));
    }

    [Fact]
    public void ResolvesConstructedGenericArrayRank()
    {
        var compilation = Compilation();
        var listDefinition = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
        var list = listDefinition.Construct(compilation.GetSpecialType(SpecialType.System_String));
        var array = compilation.CreateArrayTypeSymbol(list, rank: 2);

        Assert.Equal(typeof(System.Collections.Generic.List<string>[,]),
            ClrTypeResolver.Resolve(array));
    }
}

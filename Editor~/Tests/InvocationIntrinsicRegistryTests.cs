using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace USugar.Tests;

public class InvocationIntrinsicRegistryTests
{
    [Fact]
    public void IntrinsicKeyMatchesContainingTypeMethodArityAndParameterShape()
    {
        var type = CompileType(@"
public class Api {
    public void Query(int value) { }
    public void Query(string value) { }
    public void Query<T>(int value) { }
}");
        var methods = type.GetMembers("Query").OfType<IMethodSymbol>().ToArray();
        var key = new IntrinsicKey(
            new[] { "Api" },
            new[] { "Query" },
            genericArity: 0,
            new IntrinsicParameterShape(
                1, 1,
                new IntrinsicParameterConstraint(0, "System.Int32")));

        Assert.Single(methods.Where(key.Matches));
        Assert.Equal(SpecialType.System_Int32,
            methods.Single(key.Matches).Parameters[0].Type.SpecialType);
    }

    [Fact]
    public void OptionalParameterConstraintAppliesOnlyWhenPresent()
    {
        var type = CompileType(@"
public class Api {
    public void Query() { }
    public void Query(bool includeInactive) { }
    public void Query(int invalid) { }
}");
        var key = new IntrinsicKey(
            new[] { "Api" },
            new[] { "Query" },
            genericArity: 0,
            new IntrinsicParameterShape(
                0, 1,
                new IntrinsicParameterConstraint(0, "System.Boolean")));

        Assert.Equal(2, type.GetMembers("Query").OfType<IMethodSymbol>()
            .Count(key.Matches));
    }

    [Fact]
    public void RegistryRejectsDuplicateRuleNames()
    {
        var key = new IntrinsicKey(
            new[] { "Api" },
            new[] { "Query" },
            genericArity: 0,
            new IntrinsicParameterShape(0, 0));
        InvocationIntrinsicLowerer lower = (_, _, _) => null;

        var error = Assert.Throws<InvalidOperationException>(() =>
            new InvocationIntrinsicRegistry(new[]
            {
                new InvocationIntrinsicRule("duplicate", key, lower),
                new InvocationIntrinsicRule("duplicate", key, lower),
            }));

        Assert.Contains("Duplicate", error.Message);
    }

    static INamedTypeSymbol CompileType(string source)
    {
        var compilation = CSharpCompilation.Create(
            "IntrinsicKeyTests",
            new[] { CSharpSyntaxTree.ParseText(source) },
            TestHelper.StandardRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.GetTypeByMetadataName("Api");
    }
}

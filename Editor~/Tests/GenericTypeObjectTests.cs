using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System;
using Xunit;

namespace USugar.Tests;

public class SpecKeyParityPlain { }
public class SpecKeyParityWrapper<T> { }
public class SpecKeyParityOuter<T> { public class Inner { } }

public class GenericTypeObjectTests
{
    [Fact]
    public void SpecKey_SymbolAndReflectionSpellingsAgree()
    {
        TestHelper.BuildCompilation(@"
namespace USugar.Tests {
  public class SpecKeyParityPlain { }
  public class SpecKeyParityWrapper<T> { }
  public class SpecKeyParityOuter<T> { public class Inner { } }
}
class SpecKeyHost {
  public USugar.Tests.SpecKeyParityPlain a;
  public USugar.Tests.SpecKeyParityWrapper<int> b;
  public USugar.Tests.SpecKeyParityOuter<int>.Inner c;
}", "SpecKeyHost", out var host);
        var fields = host.GetMembers().OfType<Microsoft.CodeAnalysis.IFieldSymbol>().ToArray();
        var clr = new[]
        {
            typeof(SpecKeyParityPlain),
            typeof(SpecKeyParityWrapper<int>),
            typeof(SpecKeyParityOuter<int>.Inner),
        };
        var keys = new string[clr.Length];
        for (var i = 0; i < clr.Length; i++)
        {
            keys[i] = ClassTypeObjectContext.SpecKey(clr[i]);
            Assert.False(string.IsNullOrEmpty(keys[i]));
            Assert.Equal(ClassTypeObjectContext.SpecKey(fields[i].Type), keys[i]);
        }
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void NestedTypeSpecKey_IncludesConstructedOwner()
    {
        var compilation = TestHelper.BuildCompilation(@"
class Owner<T> { public class Inner<U> { } }
class NestedKeys {
  public Owner<int>.Inner<string> a;
  public Owner<float>.Inner<string> b;
}", "NestedKeys", out var classSymbol);
        var fields = classSymbol.GetMembers().OfType<Microsoft.CodeAnalysis.IFieldSymbol>().ToArray();

        Assert.NotEqual(ClassTypeObjectContext.SpecKey(fields[0].Type),
            ClassTypeObjectContext.SpecKey(fields[1].Type));
    }

    [Fact]
    public void CloseType_ClosesGenericOwnerOfNestedType()
    {
        var compilation = TestHelper.BuildCompilation(@"
class Owner<T> { public class Inner<U> { } public class Leaf { } }
class NestedClose<T> {
  public Owner<T>.Inner<string> inner;
  public Owner<T>.Leaf leaf;
}", "NestedClose", out var classSymbol);
        var parameter = classSymbol.TypeParameters[0];
        var intType = compilation.GetSpecialType(Microsoft.CodeAnalysis.SpecialType.System_Int32);
#pragma warning disable RS1024 // TypeParamIdComparer intentionally preserves declaration identity.
        var map = new System.Collections.Generic.Dictionary<Microsoft.CodeAnalysis.ITypeParameterSymbol,
            Microsoft.CodeAnalysis.ITypeSymbol>(TypeParamIdComparer.Instance) { [parameter] = intType };
#pragma warning restore RS1024

        foreach (var field in classSymbol.GetMembers().OfType<Microsoft.CodeAnalysis.IFieldSymbol>())
        {
            var closed = (Microsoft.CodeAnalysis.INamedTypeSymbol)TypeEnvironment.CloseType(
                compilation, field.Type, map);
            Assert.Equal(intType, closed.ContainingType.TypeArguments[0],
                Microsoft.CodeAnalysis.SymbolEqualityComparer.Default);
            Assert.False(ClassTypeObjectContext.ContainsTypeParameter(closed));
        }
    }

    [Fact]
    public void NestedGenericOwners_MintDistinctTypeObjects()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class GtoOwner<T> { public class Inner<U> { public T owner; public U value; } }
public class GtoOwnerIdentity : UdonSharpBehaviour {
  void Start() {
    var a = new GtoOwner<int>.Inner<string>();
    var b = new GtoOwner<float>.Inner<string>();
  }
}", "GtoOwnerIdentity");

        Assert.True(Regex.Matches(uasm, @"__typeobj_[ON][^: ]+").Cast<Match>()
            .Select(m => m.Value).Distinct().Count() >= 2);
    }

    [Fact]
    public void GenericMethodMint_RegistersEachClosedSpecialization()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class GtoBox<T> { public T value; }
public class GtoMulti : UdonSharpBehaviour {
  GtoBox<T> Make<T>() { return new GtoBox<T>(); }
  void Start() { GtoBox<int> a = Make<int>(); GtoBox<string> b = Make<string>(); }
}", "GtoMulti");
        Assert.Contains("__typeobj_GtoBox_Int32", uasm);
        Assert.Contains("__typeobj_GtoBox_String", uasm);
    }

    [Fact]
    public void NestedGenericLegacyNameCollision_GetsDistinctStructuralKeys()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class GtoNest<T> { public T value; }
public class GtoNested : UdonSharpBehaviour {
  void Start() { var a = new GtoNest<GtoNest<int>>(); var b = new GtoNest<GtoNest<string>>(); }
}", "GtoNested");
        Assert.True(Regex.Matches(uasm, @"__typeobj_N[^: ]+").Cast<Match>()
            .Select(m => m.Value).Distinct().Count() >= 2);
    }

    [Fact]
    public void ArrayArgumentLegacyNameCollision_GetsDistinctStructuralKeys()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class GtoArray<T> { public T value; }
public class GtoArrays : UdonSharpBehaviour {
  void Start() { var a = new GtoArray<int[]>(); var b = new GtoArray<string[]>(); }
}", "GtoArrays");
        Assert.True(Regex.Matches(uasm, @"__typeobj_N[^: ]+").Cast<Match>()
            .Select(m => m.Value).Distinct().Count() >= 2);
    }

    [Fact]
    public void NamespaceArgumentLegacyNameCollision_GetsDistinctStructuralKeys()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
namespace A { public class Item { } }
namespace B { public class Item { } }
public class GtoNs<T> { public T value; }
public class GtoNamespaces : UdonSharpBehaviour {
  void Start() { var a = new GtoNs<A.Item>(); var b = new GtoNs<B.Item>(); }
}", "GtoNamespaces");
        Assert.True(Regex.Matches(uasm, @"__typeobj_N[^: ]+").Cast<Match>()
            .Select(m => m.Value).Distinct().Count() >= 2);
    }

    [Fact]
    public void GenericClassFieldInitializer_ClosesTransitiveMint()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class GtoInner<T> { public T value; }
public class GtoOuter<T> { public GtoInner<T> child = new GtoInner<T>(); }
public class GtoFieldInit : UdonSharpBehaviour {
  GtoOuter<T> Make<T>() { return new GtoOuter<T>(); }
  void Start() { GtoOuter<int> a = Make<int>(); GtoOuter<string> b = Make<string>(); }
}", "GtoFieldInit");
        Assert.Contains("__typeobj_GtoOuter_Int32", uasm);
        Assert.Contains("__typeobj_GtoOuter_String", uasm);
        Assert.Contains("__typeobj_GtoInner_Int32", uasm);
        Assert.Contains("__typeobj_GtoInner_String", uasm);
    }

    [Fact]
    public void MixedSpecs_TypeTestCastAndDispatch_CompileTogether()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
public class GtoBase { public virtual int Read() { return 1; } }
public class GtoLeaf<T> : GtoBase { public T value; public override int Read() { return 2; } }
public class GtoMixed : UdonSharpBehaviour {
  public int result;
  GtoBase Make<T>() { return new GtoLeaf<T>(); }
  void Start() {
    GtoBase a = Make<int>(); GtoBase b = Make<string>();
    if (a is GtoLeaf<int>) result += ((GtoLeaf<int>)a).Read();
    if (b is GtoLeaf<string>) result += b.Read();
  }
}", "GtoMixed");
        Assert.Contains("__typeobj_GtoLeaf_Int32", uasm);
        Assert.Contains("__typeobj_GtoLeaf_String", uasm);
        Assert.Matches(@"__\d+_Read", uasm);
    }

    [Fact]
    public void MultiSpecOutput_IsDeterministic()
    {
        const string source = @"
using UdonSharp;
public class GtoDet<T> { public T value; }
public class GtoDeterminism : UdonSharpBehaviour {
  GtoDet<T> Make<T>() { return new GtoDet<T>(); }
  void Start() { GtoDet<string> b = Make<string>(); GtoDet<int> a = Make<int>(); }
}";
        var first = TestHelper.CompileToUasm(source, "GtoDeterminism");
        var second = TestHelper.CompileToUasm(source, "GtoDeterminism");
        Assert.Equal(first, second);
    }

    [Fact]
    public void MoreThan256FiniteSpecializations_AreAllowed()
    {
        var source = new StringBuilder("using UdonSharp;\n");
        for (var i = 0; i < 257; i++) source.Append("public class ManyArg").Append(i).Append(" {}\n");
        source.Append("public class ManyBox<T> { public T value; }\n")
            .Append("public class ManySpecs : UdonSharpBehaviour {\n")
            .Append("  ManyBox<T> Make<T>() { return new ManyBox<T>(); }\n")
            .Append("  void Start() {\n");
        for (var i = 0; i < 257; i++)
            source.Append("    var v").Append(i).Append(" = Make<ManyArg").Append(i).Append(">();\n");
        source.Append("  }\n}");

        var uasm = TestHelper.CompileToUasm(source.ToString(), "ManySpecs");
        Assert.Contains("__typeobj_ManyBox_ManyArg256", uasm);
    }

    [Fact]
    public void RecursivelyGrowingSpecialization_IsRejectedWithoutNumericLimit()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class GrowNode<T> { }
public class GrowingSpecs : UdonSharpBehaviour {
  void Grow<T>() { Grow<GrowNode<T>>(); }
  void Start() { Grow<int>(); }
}", "GrowingSpecs"));
        Assert.Contains("Generic specialization expands recursively", ex.Message);
        Assert.Contains("Grow<int>", ex.Message);
        Assert.Contains("Grow<GrowNode<int>>", ex.Message);
    }

    [Fact]
    public void FieldInitializerGrowingTypeSpecialization_IsRejectedWithoutRecursing()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class FieldGrow<T> { public FieldGrow<FieldGrow<T>> next = new FieldGrow<FieldGrow<T>>(); }
public class FieldGrowingSpecs : UdonSharpBehaviour {
  void Start() { var root = new FieldGrow<int>(); }
}", "FieldGrowingSpecs"));
        Assert.Contains("expands recursively through field initializers", ex.Message);
        Assert.Contains("FieldGrow<int>", ex.Message);
        Assert.Contains("FieldGrow<FieldGrow<int>>", ex.Message);
    }
}

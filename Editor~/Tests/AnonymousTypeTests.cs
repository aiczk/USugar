using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Xunit;

namespace USugar.Tests;

// Anonymous types use a compiler-owned object[] carrier, but remain C# reference types. Assignment
// and == preserve identity; only the compiler-generated Equals/GetHashCode/ToString members inspect
// the read-only property payload.
public class AnonymousTypeTests
{
    [Fact]
    public void Anon_CreateRead_Compiles()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class AtP1 : UdonSharpBehaviour { public int result;
  void Start(){ var p = new { X = 1, Y = 2 }; result = p.X + p.Y; } }", "AtP1");

    [Fact]
    public void Anon_MixedFields_Compiles()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class AtP2 : UdonSharpBehaviour { public int result;
  void Start(){ var p = new { A = 1, B = true, C = 3 }; result = p.A + (p.B ? 10 : 0) + p.C; } }", "AtP2");

    [Fact]
    public void Anon_RuntimeShape_IsReferenceAggregate()
    {
        var compilation = TestHelper.BuildCompilation(
            @"using UdonSharp;
public class AtShape : UdonSharpBehaviour {
  void Start() { var value = new { X = 1 }; }
}", "AtShape", out _);
        var tree = compilation.SyntaxTrees.Last();
        var syntax = tree.GetRoot().DescendantNodes()
            .OfType<AnonymousObjectCreationExpressionSyntax>()
            .Single();
        var operation = (IAnonymousObjectCreationOperation)compilation
            .GetSemanticModel(tree).GetOperation(syntax);

        var shape = TypeClassifier.ShapeOf(
            operation.Type, new TypeClassifierContext(null));
        Assert.Equal(
            RuntimeBundleKind.ReferenceAggregate, shape.Bundle);
        Assert.True(TypeClassifier.IsObjectArrayEmulated(
            operation.Type));
        Assert.False(TypeClassifier.IsAggregateValue(
            operation.Type));
    }

    [Fact]
    public void Anon_Assignment_DoesNotDeepCloneCarrier()
    {
        var (uasm, consts) = TestHelper.CompileWithConsts(@"using UdonSharp;
public class AtAlias : UdonSharpBehaviour { public int result;
  void Start() {
    var first = new { X = 1 };
    var second = first;
    result = second.X;
  }
}", "AtAlias");

        Assert.Equal(
            1,
            uasm.Split(
                "SystemObjectArray.__ctor__SystemInt32__SystemObjectArray")
                .Length - 1);
        Assert.Contains(
            consts,
            constant => constant.Value is string value
                && value.StartsWith(
                    "usugar-reference-aggregate:",
                    System.StringComparison.Ordinal));
    }

    [Fact]
    public void Anon_EqualityAndGeneratedMembers_Compile()
    {
        var (uasm, consts) = TestHelper.CompileWithConsts(@"using UdonSharp;
public class AtMembers : UdonSharpBehaviour {
  public bool sameReference;
  public bool sameValue;
  public bool boxedSameValue;
  public bool boxedReference;
  public int hash;
  public int boxedHash;
  public string text;
  void Start() {
    var first = new { X = 1, Name = ""one"" };
    var alias = first;
    var equalValue = new { X = 1, Name = ""one"" };
    sameReference = first == alias;
    sameValue = first.Equals(equalValue);
    boxedSameValue = object.Equals(first, equalValue);
    boxedReference = object.ReferenceEquals(first, alias);
    hash = first.GetHashCode();
    text = first.ToString();
    object boxed = first;
    boxedSameValue = boxedSameValue && boxed.Equals(equalValue);
    boxedHash = boxed.GetHashCode();
  }
}", "AtMembers");

        Assert.Contains(
            "SystemObject.__op_Equality__SystemObject_SystemObject__SystemBoolean",
            uasm);
        Assert.Contains(
            "SystemObject.__Equals__SystemObject_SystemObject__SystemBoolean",
            uasm);
        Assert.Contains(
            "SystemObject.__GetHashCode__SystemInt32",
            uasm);
        Assert.Contains(
            "SystemObject.__ReferenceEquals__SystemObject_SystemObject__SystemBoolean",
            uasm);
        Assert.Contains(
            consts,
            constant => Equals(constant.Value, "X = "));
        Assert.Contains(
            consts,
            constant => Equals(constant.Value, "Name = "));
    }

    [Fact]
    public void Anon_NestedNullReference_CompilesGeneratedMembers()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class AtNested : UdonSharpBehaviour {
  public int seed;
  public bool equal;
  public int hash;
  public string text;
  void Start() {
    var inner = seed > 0 ? new { X = seed } : null;
    var first = new { Inner = inner };
    var second = new { Inner = inner };
    equal = first.Equals(second);
    hash = first.GetHashCode();
    text = first.ToString();
  }
}", "AtNested");

    [Fact]
    public void Anon_GeneratedObservationOfDelegate_IsRejected()
    {
        var error = Assert.Throws<System.NotSupportedException>(
            () => TestHelper.CompileToUasm(@"using UdonSharp;
using System;
public class AtDelegate : UdonSharpBehaviour {
  void Start() {
    Func<int, int> callback = value => value + 1;
    var holder = new { Callback = callback };
    holder.ToString();
  }
}", "AtDelegate"));

        Assert.Contains("callable-only", error.Message);
    }
}

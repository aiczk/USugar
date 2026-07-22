using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace USugar.Tests;

public class AggregateLayoutIdentityTests
{
    [Fact]
    public void HiddenFields_HaveDistinctSymbolQualifiedSlots()
    {
        TestHelper.BuildCompilation(@"
class Base { public int value; }
class Derived : Base { public new string value; }
class LayoutHost { Derived field; }
", "Derived", out var derived);
        var layout = AggregateLayout.Build(derived);
        var derivedField = derived.GetMembers("value").OfType<IFieldSymbol>().Single();
        var baseField = derived.BaseType.GetMembers("value").OfType<IFieldSymbol>().Single();

        Assert.True(layout.TryGetIndex(baseField, out var baseIndex));
        Assert.True(layout.TryGetIndex(derivedField, out var derivedIndex));
        Assert.NotEqual(baseIndex, derivedIndex);
        Assert.False(layout.TryGetIndex("value", out _));
    }

    [Fact]
    public void ExplicitInterfaceAutoProperty_UsesPropertySymbolAsSlotIdentity()
    {
        TestHelper.BuildCompilation(@"
interface IValue { int Value { get; set; } }
class Impl : IValue { int IValue.Value { get; set; } }
", "Impl", out var type);
        var property = type.GetMembers().OfType<IPropertySymbol>().Single();
        var layout = AggregateLayout.Build(type);

        Assert.True(layout.TryGetIndex(property, out var index));
        Assert.Equal(1, index);
    }

    [Fact]
    public void HiddenUserClassFields_CompileThroughBothStaticTypes()
        => TestHelper.CompileToUasm(@"
using UdonSharp;
public class HiddenBase { public int value; }
public class HiddenDerived : HiddenBase { public new string value; }
public class HiddenFieldUse : UdonSharpBehaviour {
  public int result;
  void Start() {
    var d = new HiddenDerived();
    d.value = ""ok"";
    ((HiddenBase)d).value = 7;
    result = ((HiddenBase)d).value + d.value.Length;
  }
}
", "HiddenFieldUse");

}

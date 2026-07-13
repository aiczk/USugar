using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace USugar.Tests;

public class VirtualDispatchTests
{
    // Build a VirtualDispatch over a source, seeding the typeobj registry with every concrete (non-abstract,
    // non-UdonSharpBehaviour) user class — the stand-in for the frozen minted set — then resolve the dispatch
    // targets for `methodName` at receiver static type `staticTypeName`.
    static System.Collections.Generic.List<VDispatchTarget> Resolve(
        string src, string behaviour, string staticTypeName, string methodName)
    {
        var comp = TestHelper.BuildCompilation(src, behaviour, out _);
        var allTypes = comp.GlobalNamespace.GetMembers().OfType<INamedTypeSymbol>()
            .Where(t => t.TypeKind == TypeKind.Class).ToList();
        INamedTypeSymbol Find(string n) => allTypes.First(t => t.Name == n);

        var typeObjs = new ClassTypeObjectContext();
        var minted = allTypes.Where(t => !t.IsAbstract
            && !t.AllInterfaces.Any() // keep it simple: user data classes
            && t.BaseType != null && t.BaseType.Name != "UdonSharpBehaviour"
            && !IsBehaviour(t)).ToList();
        // Include the concrete leaves whose base chain stays in user classes (Sq/Rc/Dog/...).
        typeObjs.Seed(allTypes.Where(t => !t.IsAbstract && !IsBehaviour(t)));

        var staticTy = Find(staticTypeName);
        var slot = staticTy.GetMembers(methodName).OfType<IMethodSymbol>().First();
        return new VirtualDispatch(typeObjs).ResolveTargets(staticTy, slot);
    }

    static bool IsBehaviour(INamedTypeSymbol t)
    {
        for (var b = t; b != null; b = b.BaseType)
            if (b.Name == "UdonSharpBehaviour") return true;
        return false;
    }

    [Fact]
    public void TwoOverrides_YieldsTwoTargets()
    {
        var src = @"using UdonSharp;
public abstract class Shape { public abstract int Area(); }
public class Sq : Shape { public int s; public override int Area() => s * s; }
public class Rc : Shape { public int w, h; public override int Area() => w * h; }
public class UseTwo : UdonSharpBehaviour { public int seed; public int result;
  void Start(){ Shape a = new Sq(); Shape b = new Rc(); result = a.Area() + b.Area(); } }";
        var targets = Resolve(src, "UseTwo", "Shape", "Area");
        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, t => t.Concrete.Name == "Sq" && t.Impl.ContainingType.Name == "Sq");
        Assert.Contains(targets, t => t.Concrete.Name == "Rc" && t.Impl.ContainingType.Name == "Rc");
    }

    [Fact]
    public void SingleOverride_YieldsSingletonForDevirt()
    {
        var src = @"using UdonSharp;
public abstract class Animal { public abstract int Speak(); }
public class Dog : Animal { public int v; public override int Speak() => v; }
public class UseOne : UdonSharpBehaviour { public int seed; public int result;
  void Start(){ Animal a = new Dog(); result = a.Speak(); } }";
        var targets = Resolve(src, "UseOne", "Animal", "Speak");
        Assert.Single(targets);
        Assert.Equal("Dog", targets[0].Concrete.Name);
    }

    [Fact]
    public void NewNonVirtual_DoesNotJoinBaseSlot()
    {
        // `new int M()` on Der does NOT override Base.M — a Base-typed call must resolve Der's impl to Base.M.
        var src = @"using UdonSharp;
public class Base { public virtual int M() => 1; }
public class Der : Base { public new int M() => 2; }
public class UseHide : UdonSharpBehaviour { public int seed; public int result;
  void Start(){ Base b = new Der(); result = b.M(); } }";
        var targets = Resolve(src, "UseHide", "Base", "M");
        Assert.Contains(targets, t => t.Concrete.Name == "Der" && t.Impl.ContainingType.Name == "Base");
        Assert.Contains(targets, t => t.Concrete.Name == "Base" && t.Impl.ContainingType.Name == "Base");
    }

    [Fact]
    public void NonGenericVirtualClass_Compiles()
    {
        var src = @"using UdonSharp;
public class Base2 { public virtual int M() => 1; }
public class Der2 : Base2 { public override int M() => 2; }
public class UseV : UdonSharpBehaviour { public int seed; public int result;
  void Start(){ Base2 b = new Der2(); result = b.M() + seed; } }";
        TestHelper.CompileToUasm(src, "UseV"); // must not throw (reject lifted)
    }

    [Fact]
    public void GenericVirtualMethod_StillRejected()
    {
        var src = @"using UdonSharp;
public class GBase { public virtual T Id<T>(T x) => x; }
public class UseGV : UdonSharpBehaviour { public int seed; public int result;
  void Start(){ var b = new GBase(); result = b.Id<int>(seed); } }";
        var ex = Assert.ThrowsAny<System.Exception>(() => TestHelper.CompileToUasm(src, "UseGV"));
        Assert.Contains("generic", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public void NewVirtual_FormsSeparateSlot()
    {
        // `new virtual int M()` on Der starts a fresh slot; a Base-typed call must NOT dispatch to Der.M.
        var src = @"using UdonSharp;
public class Base3 { public virtual int M() => 1; }
public class Der3 : Base3 { public new virtual int M() => 2; }
public class UseReV : UdonSharpBehaviour { public int seed; public int result;
  void Start(){ Base3 b = new Der3(); result = b.M(); } }";
        var targets = Resolve(src, "UseReV", "Base3", "M");
        // Der3 seen via Base3.M slot must resolve to Base3.M (Der3.M is a different slot).
        Assert.Contains(targets, t => t.Concrete.Name == "Der3" && t.Impl.ContainingType.Name == "Base3");
    }
}

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Xunit;

namespace USugar.Tests;

// S3 property tests (design 2026-07-10 symbol-intern v2 §gates-3): TypeParamId is the walk-stable
// identity that retired the EmitMethod rekey block. Direction-split on purpose — a naive
// "TypeParamId equal iff SymbolEqualityComparer equal" self-check would be FALSE by design
// ([Y8]: fresh per-walk twins are SymbolEqualityComparer-unequal yet must intern equal).
public class TypeParamIdTests
{
    const string Source = @"
using System; using UdonSharp;
public struct GBox<T> {
    public static int Run<U>(int seed) {
        int Lf<W>() { T[] a = new T[1]; U[] b = new U[1]; W[] c = new W[1]; return a.Length + b.Length + c.Length + seed; }
        return Lf<int>();
    }
}
public class TpCls : UdonSharpBehaviour {
    public int seed; public int result;
    int M<T>(int v) { Func<int> f = () => v + (default(T) == null ? 1 : 2); return f(); }
    void Start(){ result = GBox<int>.Run<string>(seed) + M<long>(seed); }
}";

    static (Compilation comp, SemanticModel model, SyntaxNode root) Compile()
    {
        var comp = TestHelper.BuildCompilation(Source, "TpCls", out _);
        var tree = comp.SyntaxTrees.First(t => t.ToString().Contains("GBox"));
        return (comp, comp.GetSemanticModel(tree), tree.GetRoot());
    }

    static List<ITypeParameterSymbol> AllDeclaredTypeParams(Compilation comp, SyntaxNode root, SemanticModel model)
    {
        var result = new List<ITypeParameterSymbol>();
        foreach (var node in root.DescendantNodes())
        {
            var sym = model.GetDeclaredSymbol(node);
            if (sym is IMethodSymbol ms) result.AddRange(ms.TypeParameters);
            if (sym is INamedTypeSymbol ts) result.AddRange(ts.TypeParameters);
        }
        return result;
    }

    // S3 completeness: SymbolEqualityComparer-equal type params always intern equal.
    [Fact]
    public void SymbolEqual_ImpliesIdEqual()
    {
        var (comp, model, root) = Compile();
        var all = AllDeclaredTypeParams(comp, root, model);
        Assert.True(all.Count >= 4, "corpus should declare >=4 type params");
        foreach (var a in all)
            foreach (var b in all)
                if (SymbolEqualityComparer.Default.Equals(a, b))
                    Assert.Equal(TypeParamId.For(a), TypeParamId.For(b));
    }

    // S3 injectivity: DISTINCT declared type params never collapse to one id (a collapse would
    // merge two map keys — the inverse failure mode of the fresh-twin split).
    [Fact]
    public void DistinctDeclarations_GetDistinctIds()
    {
        var (comp, model, root) = Compile();
        var all = AllDeclaredTypeParams(comp, root, model);
        for (int i = 0; i < all.Count; i++)
            for (int j = i + 1; j < all.Count; j++)
                if (!SymbolEqualityComparer.Default.Equals(all[i], all[j]))
                    Assert.NotEqual(TypeParamId.For(all[i]), TypeParamId.For(all[j]));
    }

    // S2 spec-dimension preservation (design v2 §gates-3): symbol-equal constructed methods give
    // equal SpecKeys; distinct instantiations of ONE definition give UNEQUAL SpecKeys. This is the
    // key-level gate for the B89 class (a spec-dependent value filed under a def that collapses
    // across instantiations).
    [Fact]
    public void SpecKey_PreservesSpecDimension()
    {
        var (comp, model, root) = Compile();
        var runDef = comp.GetTypeByMetadataName("GBox`1").GetMembers("Run")
            .OfType<IMethodSymbol>().Single();
        var intType = comp.GetSpecialType(SpecialType.System_Int32);
        var strType = comp.GetSpecialType(SpecialType.System_String);

        var runInt = runDef.Construct(intType);
        var runIntTwin = runDef.Construct(intType);
        var runStr = runDef.Construct(strType);

        var keyInt = new SpecializationKey(runInt, runInt.TypeArguments.CastArray<ITypeSymbol>());
        var keyIntTwin = new SpecializationKey(
            runIntTwin, runIntTwin.TypeArguments.CastArray<ITypeSymbol>());
        var keyStr = new SpecializationKey(runStr, runStr.TypeArguments.CastArray<ITypeSymbol>());

        Assert.Equal(keyInt, keyIntTwin);                       // symbol-equal ⟹ SpecKey-equal
        Assert.Equal(keyInt.GetHashCode(), keyIntTwin.GetHashCode());
        Assert.NotEqual(keyInt, keyStr);                        // same def, different args ⟹ unequal
        Assert.True(SymbolEqualityComparer.Default.Equals(
            keyInt.Definition, keyStr.Definition)); // …despite one definition
    }

    // Registry loud-polarity: registering the same (def, args) twice must throw, not last-writer-win.
    [Fact]
    public void AddClosureSpec_DuplicateKey_Throws()
    {
        var (comp, model, root) = Compile();
        var lfSym = ((ILocalFunctionOperation)model.GetOperation(
            root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single())).Symbol;
        var mc = new MethodContext();
        var args = System.Collections.Immutable.ImmutableArray<ITypeSymbol>.Empty
            .Add(comp.GetSpecialType(SpecialType.System_Int32));
        var owner = comp.GetTypeByMetadataName("GBox`1")
            .Construct(comp.GetSpecialType(SpecialType.System_Int32))
            .GetMembers("Run")
            .OfType<IMethodSymbol>()
            .Single()
            .Construct(comp.GetSpecialType(SpecialType.System_String));
        var ownerSpecs =
            System.Collections.Immutable.ImmutableArray.Create(owner);
        mc.AddClosureCallable(lfSym, args,
            ownerSpecs,
            new MethodSlot(0, "__0_lf"), "lf1",
            System.Array.Empty<string>(),
            System.Array.Empty<StorageType>(),
            System.Array.Empty<ReturnSlot>(), null, true);
        Assert.ThrowsAny<System.ArgumentException>(() =>
            mc.AddClosureCallable(lfSym, args,
                ownerSpecs,
                new MethodSlot(1, "__1_lf"), "lf2",
                System.Array.Empty<string>(),
                System.Array.Empty<StorageType>(),
                System.Array.Empty<ReturnSlot>(), null, true));
    }

    // TypeParamScope.Compose unit pins (2026-07-11 audit coverage hole): the identity-binding skip is
    // the B70 stack-overflow armor and was previously exercised only through full compiles.
    [Fact]
    public void Compose_SkipsIdentityBinding_IncludingFreshTwin()
    {
        var (comp, model, root) = Compile();
        var lfSyntax = root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single();
        var enclosing = lfSyntax.Ancestors().OfType<MethodDeclarationSyntax>().First();
        var walk1 = ((ILocalFunctionOperation)model.GetOperation(lfSyntax)).Symbol.TypeParameters;
        ILocalFunctionOperation viaBody = null;
        void Find(IOperation op)
        {
            if (op is ILocalFunctionOperation lf) { viaBody = lf; return; }
            foreach (var c in op.ChildOps()) { Find(c); if (viaBody != null) return; }
        }
        Find(model.GetOperation(enclosing));
        var walk2 = viaBody.Symbol.TypeParameters;

        // Literal identity binding (W -> same W): never installed.
        var m1 = TypeParamScope.Compose(null, newWins: true,
            new[] { ((IReadOnlyList<ITypeParameterSymbol>)walk1, (IReadOnlyList<ITypeSymbol>)walk1.CastArray<ITypeSymbol>()) });
        Assert.False(m1.ContainsKey(walk1[0]));

        // Fresh-twin identity binding (walk1 W -> walk2 W, TypeParamId-equal): equally skipped —
        // installing it would self-cycle GetUdonTypeName's resolve-then-recurse through the twin.
        var m2 = TypeParamScope.Compose(null, newWins: true,
            new[] { ((IReadOnlyList<ITypeParameterSymbol>)walk1, (IReadOnlyList<ITypeSymbol>)walk2.CastArray<ITypeSymbol>()) });
        Assert.False(m2.ContainsKey(walk1[0]));
    }

    [Fact]
    public void Compose_NewWinsPrecedence()
    {
        var (comp, model, root) = Compile();
        var lfSym = ((ILocalFunctionOperation)model.GetOperation(
            root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single())).Symbol;
        var tp = lfSym.TypeParameters;
        var intType = (ITypeSymbol)comp.GetSpecialType(SpecialType.System_Int32);
        var strType = (ITypeSymbol)comp.GetSpecialType(SpecialType.System_String);

        var baseMap = TypeParamScope.Compose(null, newWins: true,
            new[] { ((IReadOnlyList<ITypeParameterSymbol>)tp, (IReadOnlyList<ITypeSymbol>)new[] { intType }) });
        var overlaid = TypeParamScope.Compose(baseMap, newWins: true,
            new[] { ((IReadOnlyList<ITypeParameterSymbol>)tp, (IReadOnlyList<ITypeSymbol>)new[] { strType }) });
        var kept = TypeParamScope.Compose(baseMap, newWins: false,
            new[] { ((IReadOnlyList<ITypeParameterSymbol>)tp, (IReadOnlyList<ITypeSymbol>)new[] { strType }) });

        Assert.Equal(strType, overlaid[tp[0]], SymbolEqualityComparer.Default);
        Assert.Equal(intType, kept[tp[0]], SymbolEqualityComparer.Default);
    }

    // S3 freshness erasure (the load-bearing direction, [Y8]): a local function's type-parameter
    // symbols obtained from TWO independent operation walks must intern to ONE id — this is exactly
    // the equivalence the retired rekey block compensated for. The assert deliberately does NOT
    // require SymbolEqualityComparer to disagree (Roslyn-version-dependent); it requires TypeParamId
    // to agree regardless.
    [Fact]
    public void FreshWalkTwins_InternEqual()
    {
        var (comp, model, root) = Compile();
        var lfSyntax = root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single();

        // Walk 1: the LF discovered inside the ENCLOSING method's body operation tree.
        var enclosing = lfSyntax.Ancestors().OfType<MethodDeclarationSyntax>().First();
        var enclosingOp = model.GetOperation(enclosing);
        ILocalFunctionOperation viaBody = null;
        void Find(IOperation op)
        {
            if (op is ILocalFunctionOperation lf) { viaBody = lf; return; }
            foreach (var c in op.ChildOps()) { Find(c); if (viaBody != null) return; }
        }
        Find(enclosingOp);
        Assert.NotNull(viaBody);

        // Walk 2: the LF's own declaration operation, fetched independently.
        var viaDecl = (ILocalFunctionOperation)model.GetOperation(lfSyntax);

        var tp1 = viaBody.Symbol.TypeParameters;
        var tp2 = viaDecl.Symbol.TypeParameters;
        Assert.Equal(tp1.Length, tp2.Length);
        for (int i = 0; i < tp1.Length; i++)
        {
            Assert.Equal(TypeParamId.For(tp1[i]), TypeParamId.For(tp2[i]));
            Assert.True(TypeParamIdComparer.Instance.Equals(tp1[i], tp2[i]));
        }
    }
}

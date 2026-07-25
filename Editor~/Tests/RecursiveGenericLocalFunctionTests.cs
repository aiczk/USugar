using Xunit;

namespace USugar.Tests;

// Pre-fuzz audit finding (2026-07-11 zone-3 HIGH): the ambient key composition keyed a closure by
// the REGISTRAR own spec dimension, so a self-/mutually-recursive generic local function re-composed
// its own args on every hop -- every lookup missed, the pending drain re-registered forever, and the
// compile HUNG (empirically reproduced). Fixed by deriving key args from the closure LEXICAL
// enclosing chain (LoweringState.ComposeClosureKeyArgs); these pins hold the door shut.
public class RecursiveGenericLocalFunctionTests
{
    [Fact]
    public void SelfRecursiveGenericLocalFunction_Compiles()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class RecLf : UdonSharpBehaviour {
    public int result;
    void Start(){ int Fac<T>(int n){ if (n <= 1) return 1; return n * Fac<T>(n - 1); } result = Fac<int>(5); }
}", "RecLf");

    [Fact]
    public void MutuallyRecursiveGenericLocalFunctions_Compile()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class MutLf : UdonSharpBehaviour {
    public int result;
    void Start(){
        int A<T>(int n){ if (n <= 0) return 0; return B<T>(n - 1) + 1; }
        int B<T>(int n){ if (n <= 0) return 0; return A<T>(n - 1) + 2; }
        result = A<int>(5);
    }
}", "MutLf");

    [Fact]
    public void LambdaInsideGenericLocalFunction_ReferencingThatLf_Compiles()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class LamRefLf : UdonSharpBehaviour {
    public int result;
    void Start(){
        int A<T>(int n){ System.Func<int> f = () => n <= 0 ? 0 : A<T>(n - 1); return f() + 1; }
        result = A<int>(3);
    }
}", "LamRefLf");
}

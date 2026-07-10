using Xunit;

namespace USugar.Tests;

// Pre-fuzz audit finding (2026-07-10 zone-1 HIGH): EmitRefOutCopyBack read the definition-keyed
// param map with a closure target — a local function with a ref/out parameter crashed with
// KeyNotFoundException on legal C# (the per-spec campaign stopped writing closures into that map,
// and this consumer was missed). Red-proofed, then fixed via the per-spec registry arm.
public class LocalFunctionRefParamTests
{
    [Fact]
    public void LocalFunction_RefParam_Compiles()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class RefLf : UdonSharpBehaviour {
    public int result;
    void Start(){ int x = 0; void Inc(ref int n){ n++; } Inc(ref x); Inc(ref x); result = x; }
}", "RefLf");

    [Fact]
    public void LocalFunction_OutParam_Compiles()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class OutLf : UdonSharpBehaviour {
    public int result;
    void Start(){ void Give(out int n){ n = 41; } Give(out int y); result = y + 1; }
}", "OutLf");

    [Fact]
    public void GenericLocalFunction_RefParam_TwoInstantiations_Compiles()
        => TestHelper.CompileToUasm(@"using UdonSharp;
public class GRefLf : UdonSharpBehaviour {
    public int result;
    void Start(){
        int x = 0;
        void Bump<T>(ref int n){ n += (default(T) == null ? 1 : 2); }
        Bump<string>(ref x);
        Bump<int>(ref x);
        result = x;
    }
}", "GRefLf");
}

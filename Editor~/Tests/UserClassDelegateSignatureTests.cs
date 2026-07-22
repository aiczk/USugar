using System;
using Xunit;

namespace USugar.Tests;

public class UserClassDelegateSignatureTests
{
    [Fact]
    public void PrivateDelegate_ClassParameterAndReturn_CompileLocally()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class LocalPayload { public int Value; }
public class LocalClassDelegateHost : UdonSharpBehaviour {
    Func<LocalPayload, LocalPayload> map;
    LocalPayload Increment(LocalPayload value) { value.Value++; return value; }
    void Start() {
        map = Increment;
        var payload = new LocalPayload { Value = 4 };
        var result = map(payload);
    }
}
", "LocalClassDelegateHost");

        Assert.Contains("__dlgc_SystemObjectArray__SystemObjectArray__a0", uasm);
        Assert.Contains("__dlgc_SystemObjectArray__SystemObjectArray__ret", uasm);
    }

    [Fact]
    public void LocalLambda_ClassSignature_Compiles()
    {
        var uasm = TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class LambdaPayload { public int Value; }
public class LocalClassLambdaHost : UdonSharpBehaviour {
    void Start() {
        Func<LambdaPayload, LambdaPayload> map = value => { value.Value += 2; return value; };
        var result = map(new LambdaPayload());
    }
}
", "LocalClassLambdaHost");

        Assert.Contains("__dlg_", uasm);
    }

    [Fact]
    public void PublicField_ClassSignature_RejectsCrossProgramSurface()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class PublicPayload { }
public class PublicClassDelegateHost : UdonSharpBehaviour {
    public Action<PublicPayload> Callback;
}
", "PublicClassDelegateHost"));

        Assert.Contains("must remain private", ex.Message);
    }

    [Fact]
    public void ForeignMethodGroup_ClassSignature_RejectsCrossProgramTarget()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(new[] { @"
using UdonSharp;
public class ForeignPayload { }
public class ForeignClassDelegateTarget : UdonSharpBehaviour {
    public ForeignPayload Echo(ForeignPayload value) { return value; }
}
", @"
using UdonSharp;
using System;
public class ForeignClassDelegateHost : UdonSharpBehaviour {
    public ForeignClassDelegateTarget Target;
    void Start() { Func<ForeignPayload, ForeignPayload> map = Target.Echo; }
}
" }, "ForeignClassDelegateHost"));

        Assert.Contains("cannot bind a cross-program target", ex.Message);
    }

    [Fact]
    public void PublicMethod_ClassSignatureDelegateParameter_RejectsCrossProgramSurface()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class MethodPayload { }
public class PublicClassDelegateMethodHost : UdonSharpBehaviour {
    public void Register(Action<MethodPayload> callback) { }
}
", "PublicClassDelegateMethodHost"));

        Assert.Contains("valid only inside this Udon program", ex.Message);
    }

    [Fact]
    public void PublicProperty_ClassSignatureDelegate_RejectsCrossProgramSurface()
    {
        var ex = Assert.Throws<NotSupportedException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
using System;
public class PropertyPayload { }
public class PublicClassDelegatePropertyHost : UdonSharpBehaviour {
    public Func<PropertyPayload> Factory { get; set; }
}
", "PublicClassDelegatePropertyHost"));

        Assert.Contains("must remain private", ex.Message);
    }
}

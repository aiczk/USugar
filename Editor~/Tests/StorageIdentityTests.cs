using System;
using Xunit;

namespace USugar.Tests;

public class StorageIdentityTests
{
    [Fact]
    public void UserAndGeneratedStorageWithSameNameAndType_Reject()
    {
        var storage = new StorageContext(new CModule());
        storage.DeclareField("collision", StorageTypes.Int32);

        var error = Assert.Throws<InvalidOperationException>(
            () => storage.DeclareVar("collision", StorageTypes.Int32));

        Assert.Contains("User/SystemInt32", error.Message);
        Assert.Contains("Generated/SystemInt32", error.Message);
    }

    [Fact]
    public void RepeatedGeneratedAbiDeclaration_IsIdempotent()
    {
        var module = new CModule();
        var storage = new StorageContext(module);

        Assert.True(storage.TryDeclareVar("__abi_value", StorageTypes.Int32));
        Assert.False(storage.TryDeclareVar("__abi_value", StorageTypes.Int32));
        Assert.Single(module.Fields);
    }

    [Fact]
    public void UserFieldCannotAliasReflectionStorage()
    {
        var error = Assert.Throws<InvalidOperationException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class StorageReflectionCollision : UdonSharpBehaviour
{
    public long __refl_typeid;
}", "StorageReflectionCollision"));

        Assert.Contains("__refl_typeid", error.Message);
        Assert.Contains("conflicts", error.Message);
    }

    [Fact]
    public void UserFieldCannotAliasTypeObjectStorage()
    {
        var error = Assert.Throws<InvalidOperationException>(() => TestHelper.CompileToUasm(@"
using UdonSharp;
public class StorageTypeObjectPayload { public int value; }
public class StorageTypeObjectCollision : UdonSharpBehaviour
{
    public object[] __typeobj_StorageTypeObjectPayload;
    void Start() { var value = new StorageTypeObjectPayload(); value.value = 1; }
}", "StorageTypeObjectCollision"));

        Assert.Contains("__typeobj_StorageTypeObjectPayload", error.Message);
        Assert.Contains("conflicts", error.Message);
    }

    [Fact]
    public void PinnedSlotRequiresDeclaredHeapStorage()
    {
        var module = new CModule();
        var builder = new CoreBuilder(module);
        builder.BeginFunction("missing_pinned");
        builder.AllocPinned(StorageTypes.Int32, "missing");

        var error = Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
        Assert.Contains("Undeclared field 'missing'", error.Message);
    }

    [Fact]
    public void PinnedSlotMustMatchHeapStorageType()
    {
        var module = new CModule();
        module.Fields.Add(new FieldDecl("pinned", StorageTypes.String));
        var builder = new CoreBuilder(module);
        builder.BeginFunction("mismatched_pinned");
        builder.AllocPinned(StorageTypes.Int32, "pinned");

        var error = Assert.Throws<VerificationException>(() => CoreVerify.Verify(module));
        Assert.Contains("Type mismatch", error.Message);
    }
}

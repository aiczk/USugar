using System;
using System.IO;
using Xunit;

namespace USugar.Tests;

public class ManagedModuleIdentityTests
{
    [Fact]
    public void ReadsManagedPeMvidWithoutLoadingAssembly()
    {
        var module = typeof(ManagedModuleIdentityTests).Module;

        Assert.Equal(
            "mvid:" + module.ModuleVersionId.ToString("N"),
            ManagedModuleIdentity.GetIdentity(module.FullyQualifiedName));
    }

    [Fact]
    public void FallsBackToContentIdentityForNonManagedFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "first");
            var first = ManagedModuleIdentity.GetIdentity(path);
            File.WriteAllText(path, "second");
            var second = ManagedModuleIdentity.GetIdentity(path);

            Assert.StartsWith("sha256:", first);
            Assert.StartsWith("sha256:", second);
            Assert.NotEqual(first, second);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

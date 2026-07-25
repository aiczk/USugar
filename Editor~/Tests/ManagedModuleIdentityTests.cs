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

    /// <summary>Arms the HasMetadata gate in ReadMvid. A structurally valid PE carrying no CLI
    /// header must reach the content-hash fallback; without the gate, GetMetadataReader throws
    /// InvalidOperationException, a type GetIdentity's catch filter deliberately does not list, so
    /// every non-managed file in a reference set would escape uncaught instead of hashing.</summary>
    [Fact]
    public void FallsBackToContentIdentityForPeWithoutCliHeader()
    {
        var managed = typeof(ManagedModuleIdentityTests).Module.FullyQualifiedName;
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, StripCliHeader(File.ReadAllBytes(managed)));

            Assert.StartsWith("mvid:", ManagedModuleIdentity.GetIdentity(managed));
            Assert.StartsWith("sha256:", ManagedModuleIdentity.GetIdentity(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Zeroes a managed PE's CLI header data directory, leaving an image that still parses
    /// but reports zero metadata. The offsets are walked here rather than obtained from the reader
    /// under test, so the fixture stays independent of the code it checks.</summary>
    static byte[] StripCliHeader(byte[] image)
    {
        var peOffset = BitConverter.ToInt32(image, 0x3c);
        Assert.Equal(0x00004550, BitConverter.ToInt32(image, peOffset));
        var optionalHeader = peOffset + 24;
        var magic = BitConverter.ToUInt16(image, optionalHeader);
        var dataDirectories = optionalHeader + magic switch
        {
            0x10b => 96,
            0x20b => 112,
            _ => throw new InvalidOperationException(
                $"Unexpected PE optional-header magic 0x{magic:x}."),
        };
        Array.Clear(image, dataDirectories + 14 * 8, 8);
        return image;
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

using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

/// <summary>
/// Reads a managed PE's module version id without loading the assembly. Non-managed or malformed
/// inputs fall back to a full content hash.
/// </summary>
public static class ManagedModuleIdentity
{
    public static string GetIdentity(string path)
    {
        try
        {
            var mvid = ReadMvid(path);
            if (mvid != Guid.Empty)
                return "mvid:" + mvid.ToString("N");
        }
        catch (Exception ex) when (
            ex is IOException
            || ex is UnauthorizedAccessException
            || ex is BadImageFormatException
            || ex is ArgumentException)
        {
            // Fall through to byte identity.
        }

        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(path);
        return "sha256:" + BitConverter.ToString(sha256.ComputeHash(stream))
            .Replace("-", "").ToLowerInvariant();
    }

    static Guid ReadMvid(string path)
    {
        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
        if (!peReader.HasMetadata) return Guid.Empty;
        var metadata = peReader.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
    }
}

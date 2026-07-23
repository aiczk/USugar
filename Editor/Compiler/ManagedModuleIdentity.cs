using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Reads a managed PE's module version id without loading the assembly. Non-managed or malformed
/// inputs fall back to a full content hash.
/// </summary>
public static class ManagedModuleIdentity
{
    struct Section
    {
        public uint VirtualAddress;
        public uint VirtualSize;
        public uint RawSize;
        public uint RawOffset;
    }

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
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        Require(stream.Length >= 0x40, "Truncated DOS header.");
        stream.Position = 0;
        Require(reader.ReadUInt16() == 0x5a4d, "Missing MZ header.");
        stream.Position = 0x3c;
        var peOffset = reader.ReadUInt32();
        Require(peOffset <= stream.Length - 24, "Invalid PE header offset.");
        stream.Position = peOffset;
        Require(reader.ReadUInt32() == 0x00004550, "Missing PE signature.");

        stream.Position = peOffset + 6;
        var sectionCount = reader.ReadUInt16();
        stream.Position = peOffset + 20;
        var optionalHeaderSize = reader.ReadUInt16();
        var optionalHeader = peOffset + 24;
        Require(optionalHeader <= stream.Length - optionalHeaderSize,
            "Truncated optional header.");
        stream.Position = optionalHeader;
        var magic = reader.ReadUInt16();
        var dataDirectoryOffset = magic switch
        {
            0x10b => optionalHeader + 96u,
            0x20b => optionalHeader + 112u,
            _ => throw new BadImageFormatException("Unknown PE optional-header format.")
        };
        Require(dataDirectoryOffset + 15u * 8u <= optionalHeader + optionalHeaderSize,
            "CLI data directory is missing.");

        stream.Position = optionalHeader + 60;
        var sizeOfHeaders = reader.ReadUInt32();
        stream.Position = dataDirectoryOffset + 14u * 8u;
        var cliRva = reader.ReadUInt32();
        Require(cliRva != 0, "PE has no CLI header.");

        var sections = new Section[sectionCount];
        var sectionTable = optionalHeader + optionalHeaderSize;
        Require(sectionTable + sectionCount * 40L <= stream.Length,
            "Truncated section table.");
        for (int i = 0; i < sections.Length; i++)
        {
            stream.Position = sectionTable + i * 40L + 8;
            sections[i].VirtualSize = reader.ReadUInt32();
            sections[i].VirtualAddress = reader.ReadUInt32();
            sections[i].RawSize = reader.ReadUInt32();
            sections[i].RawOffset = reader.ReadUInt32();
        }

        var cliOffset = RvaToOffset(cliRva, sizeOfHeaders, sections, stream.Length);
        Require(cliOffset <= stream.Length - 16, "Truncated CLI header.");
        stream.Position = cliOffset + 8;
        var metadataRva = reader.ReadUInt32();
        var metadataSize = reader.ReadUInt32();
        var metadataOffset = RvaToOffset(
            metadataRva, sizeOfHeaders, sections, stream.Length);
        Require(metadataSize > 0 && metadataOffset <= stream.Length - metadataSize,
            "Truncated metadata root.");

        stream.Position = metadataOffset;
        Require(reader.ReadUInt32() == 0x424a5342, "Missing metadata signature.");
        stream.Position = metadataOffset + 12;
        var versionLength = reader.ReadUInt32();
        var streamsHeader = Align4(metadataOffset + 16 + versionLength);
        Require(streamsHeader <= metadataOffset + metadataSize - 4,
            "Invalid metadata version string.");
        stream.Position = streamsHeader + 2;
        var streamCount = reader.ReadUInt16();

        uint tablesOffset = 0, guidOffset = 0, guidSize = 0;
        var headerPosition = stream.Position;
        for (int i = 0; i < streamCount; i++)
        {
            Require(headerPosition <= metadataOffset + metadataSize - 8,
                "Truncated metadata stream header.");
            stream.Position = headerPosition;
            var offset = reader.ReadUInt32();
            var size = reader.ReadUInt32();
            var nameStart = stream.Position;
            var nameBytes = new MemoryStream();
            byte b;
            while ((b = reader.ReadByte()) != 0)
                nameBytes.WriteByte(b);
            var name = Encoding.ASCII.GetString(nameBytes.ToArray());
            headerPosition = nameStart + Align4(stream.Position - nameStart);
            if (name is "#~" or "#-") tablesOffset = offset;
            if (name == "#GUID")
            {
                guidOffset = offset;
                guidSize = size;
            }
        }
        Require(tablesOffset != 0 && guidOffset != 0, "Required metadata streams are missing.");

        stream.Position = metadataOffset + tablesOffset;
        reader.ReadUInt32();
        reader.ReadByte();
        reader.ReadByte();
        var heapSizes = reader.ReadByte();
        reader.ReadByte();
        var validTables = reader.ReadUInt64();
        reader.ReadUInt64();
        Require((validTables & 1ul) != 0, "Module metadata table is missing.");

        uint moduleRows = 0;
        for (int table = 0; table < 64; table++)
        {
            if ((validTables & (1ul << table)) == 0) continue;
            var rows = reader.ReadUInt32();
            if (table == 0) moduleRows = rows;
        }
        Require(moduleRows > 0, "Module metadata table is empty.");

        reader.ReadUInt16(); // Generation
        ReadHeapIndex(reader, (heapSizes & 0x01) != 0); // Name
        var mvidIndex = ReadHeapIndex(reader, (heapSizes & 0x02) != 0);
        Require(mvidIndex > 0, "Module MVID is empty.");
        var guidPosition = metadataOffset + guidOffset + (mvidIndex - 1u) * 16u;
        Require(guidPosition <= metadataOffset + guidOffset + guidSize - 16,
            "Module MVID points outside #GUID.");
        stream.Position = guidPosition;
        return new Guid(reader.ReadBytes(16));
    }

    static uint ReadHeapIndex(BinaryReader reader, bool wide)
        => wide ? reader.ReadUInt32() : reader.ReadUInt16();

    static long RvaToOffset(uint rva, uint sizeOfHeaders,
        Section[] sections, long fileLength)
    {
        if (rva < sizeOfHeaders) return rva;
        foreach (var section in sections)
        {
            var span = Math.Max(section.VirtualSize, section.RawSize);
            if (rva < section.VirtualAddress || rva >= section.VirtualAddress + span)
                continue;
            var offset = section.RawOffset + (rva - section.VirtualAddress);
            Require(offset < fileLength, "RVA points outside the file.");
            return offset;
        }
        throw new BadImageFormatException("RVA does not belong to a PE section.");
    }

    static long Align4(long value) => (value + 3L) & ~3L;

    static void Require(bool condition, string message)
    {
        if (!condition) throw new BadImageFormatException(message);
    }
}

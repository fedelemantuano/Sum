using System;
using System.IO;
namespace EseRecovery.Inspection;

public sealed class UnsupportedEseFormatException : NotSupportedException
{
    public UnsupportedEseFormatException(string message) : base(message) { }
}
/// <summary>Bounded inspection of the primary 4 KiB header. Leaves the caller's stream open.</summary>
public static class HeaderInspector
{
    public static EseHeader Inspect(Stream source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (!source.CanRead) throw new ArgumentException("Stream must be readable.", nameof(source));
        var header = new byte[4096];
        var offset = 0;
        while (offset < header.Length)
        {
            int count = source.Read(header, offset, header.Length - offset);
            if (count == 0) throw new InvalidDataException("Truncated primary ESE header: expected 4096 bytes.");
            offset += count;
        }
        // File-type field is deliberately at the same unaligned offset in all three formats.
        uint type = Binary.U32(header, 667);
        if (type == 1)
        {
            if (Binary.U32(header, 4) != 0x89abcdef) throw new InvalidDataException("Invalid database magic.");
            if (Binary.U32(header, 8) != 0x620) throw new UnsupportedEseFormatException("Database header version is not 0x620.");
            var result = new DatabaseHeader(header);
            CheckPageSize(result.PageSize);
            if (result.MinimumRequiredGeneration > int.MaxValue || result.MaximumRequiredGeneration > int.MaxValue ||
                result.MinimumRequiredGeneration > result.MaximumRequiredGeneration)
                throw new InvalidDataException("Invalid required generation interval.");
            return result;
        }
        if (type == 3)
        {
            if (Binary.U32(header, 32) != 8) throw new UnsupportedEseFormatException("Only major-8 log header layout is currently inspected.");
            var result = new LogHeader(header);
            CheckPageSize(result.PageSize);
            if (result.Generation == 0 || result.Generation > int.MaxValue ||
                (result.SectorSize != 512 && result.SectorSize != 4096) ||
                result.HeaderSectors * result.SectorSize != 4096 || result.FileSectors < result.HeaderSectors)
                throw new InvalidDataException("Invalid log header dimensions or generation.");
            return result;
        }
        if (type == 4) return new CheckpointHeader(header);
        throw new UnsupportedEseFormatException("Unrecognized ESE file type " + type + ".");
    }
    private static void CheckPageSize(uint value)
    {
        if (value != 2048 && value != 4096 && value != 8192 && value != 16384 && value != 32768)
            throw new InvalidDataException("Invalid database page size.");
    }
}

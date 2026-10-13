using System;
using System.IO;
using EseRecovery.Inspection;
namespace EseRecovery;

public static class EseChecksums
{
    public static uint Header(byte[] block)
    {
        if (block == null || block.Length < 4 || block.Length % 4 != 0) throw new ArgumentException("Invalid checksum block.");
        uint sum = 0x89abcdef;
        for (int i = 4; i < block.Length; i += 4) sum ^= Binary.U32(block, i);
        return sum;
    }
    // Hsiao parity over row/column bit indices, excluding the stored checksum.
    public static ulong Page(byte[] block, uint pageNumber)
    {
        if (block == null || (block.Length != 4096 && block.Length != 8192))
            throw new NotSupportedException("Page checksums currently support 4/8 KiB pages.");
        uint xor = 0, rows = 0, inverseRows = 0;
        for (uint row = 2; row < block.Length / 4; row++)
        {
            uint word = Binary.U32(block, (int)row * 4); xor ^= word;
            uint parity = word; parity ^= parity >> 16; parity ^= parity >> 8; parity ^= parity >> 4;
            if (((0x6996u >> (int)(parity & 15)) & 1) != 0) { rows ^= row; inverseRows ^= ~row; }
        }
        uint columns = 0, inverseColumns = 0;
        for (uint bit = 0; bit < 32; bit++) if ((xor & (1u << (int)bit)) != 0) { columns ^= bit; inverseColumns ^= ~bit; }
        uint mask = (uint)block.Length / 4 - 1;
        uint ecc = ((inverseRows & mask) << 21) | ((inverseColumns & 31) << 16) | ((rows & mask) << 5) | (columns & 31);
        return ((ulong)ecc << 32) | (xor ^ pageNumber);
    }
    internal static ulong U64(byte[] b, int offset) => Binary.U32(b, offset) | ((ulong)Binary.U32(b, offset + 4) << 32);
    internal static void CheckPage(byte[] b, uint number)
    {
        if ((Binary.U32(b,36) & 0x2000) != 0)
        { if (U64(b,0) != Page(b,number)) throw new InvalidDataException("ESE page checksum mismatch at page " + number + "."); }
        else if (Binary.U32(b,0) != Header(b) || Binary.U32(b,4) != number)
            throw new InvalidDataException("Legacy ESE page checksum/page-number mismatch at page " + number + ".");
    }
}

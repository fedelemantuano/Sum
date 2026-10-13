using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SumData.Readers;

// ESE values remain raw bytes in-process; text is normalized to UTF-16LE.
internal sealed class EseRecord
{
    public string Kind { get; set; }
    public int State { get; set; }
    internal EseRecovery.EseRecoveryReport Recovery { get; set; }
    public string Table { get; set; }
    public Dictionary<string, byte[]> Values { get; set; }
    public Dictionary<string, string> Columns { get; set; }

    internal byte[] Bytes(string column)
    {
        if (Values == null || !Values.TryGetValue(column, out var value))
            throw new InvalidDataException($"Missing column {Table}.{column}.");
        return value;
    }

    private byte[] Fixed(string column, int length)
    {
        var bytes = Bytes(column);
        if (bytes == null || bytes.Length != length)
            throw new InvalidDataException($"Invalid or null {Table}.{column}: expected {length} bytes.");
        if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return bytes;
    }

    internal string Text(string column) => Bytes(column) is byte[] bytes
        ? Encoding.Unicode.GetString(bytes).Trim('\0') : null;
    internal long Int64(string column) => BitConverter.ToInt64(Fixed(column, 8), 0);
    internal int Int32(string column) => BitConverter.ToInt32(Fixed(column, 4), 0);
    internal short Int16(string column) => BitConverter.ToInt16(Fixed(column, 2), 0);
    internal byte Byte(string column) => Fixed(column, 1)[0];
    internal Guid Guid(string column)
    {
        var bytes = Bytes(column);
        if (bytes == null || bytes.Length != 16)
            throw new InvalidDataException($"Invalid or null {Table}.{column}: expected a GUID.");
        return new Guid(bytes);
    }
    internal DateTimeOffset Timestamp(string column) => DateTimeOffset.FromFileTime(Int64(column)).ToUniversalTime();
}

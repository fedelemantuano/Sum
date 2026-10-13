using System;
namespace EseRecovery.Inspection;

/// <summary>File metadata only. Parsing a header never establishes integrity or recoverability.</summary>
public abstract class EseHeader
{
    public string Kind { get; }
    public uint StoredChecksum { get; }
    public string Integrity => "NotValidated";
    protected EseHeader(string kind, uint checksum) { Kind = kind; StoredChecksum = checksum; }
}
public sealed class DatabaseHeader : EseHeader
{
    public uint Version { get; }
    public uint UpdateMajor { get; }
    public uint UpdateMinor { get; }
    public uint PageSize { get; }
    public uint State { get; }
    public string StateName => State == 2 ? "DirtyShutdown" : State == 3 ? "CleanShutdown" : "Other";
    public uint DatabaseId { get; }
    public string DatabaseSignature { get; }
    public string AttachPositionBytes { get; }
    public string ConsistentPositionBytes { get; }
    public string LogSignature { get; }
    public uint MinimumRequiredGeneration { get; }
    public uint MaximumRequiredGeneration { get; }
    internal DatabaseHeader(byte[] h) : base("Database", Binary.U32(h, 0))
    {
        Version = Binary.U32(h, 8); UpdateMajor = Binary.U32(h, 232); UpdateMinor = Binary.U32(h, 644);
        PageSize = Binary.PageSize(Binary.U32(h, 236)); State = Binary.U32(h, 52);
        ConsistentPositionBytes = Binary.Hex(h, 56, 8);
        AttachPositionBytes = Binary.Hex(h, 80, 8);
        DatabaseId = Binary.U32(h, 104); DatabaseSignature = Binary.Hex(h, 24, 28);
        LogSignature = Binary.Hex(h, 108, 28);
        MinimumRequiredGeneration = Binary.U32(h, 296); MaximumRequiredGeneration = Binary.U32(h, 300);
    }
}
public sealed class LogHeader : EseHeader
{
    public uint Generation { get; }
    public uint VersionMajor { get; }
    public uint VersionMinor { get; }
    public uint UpdateMajor { get; }
    public uint UpdateMinor { get; }
    public uint SectorSize { get; }
    public uint HeaderSectors { get; }
    public uint FileSectors { get; }
    public uint PageSize { get; }
    public string LogSignature { get; }
    public string CreationTimeBytes { get; }
    public string PreviousGenerationTimeBytes { get; }
    internal LogHeader(byte[] h) : base("Log", Binary.U32(h, 0))
    {
        Generation = Binary.U32(h, 4); SectorSize = Binary.U16(h, 8);
        HeaderSectors = Binary.U16(h, 10); FileSectors = Binary.U16(h, 12);
        PageSize = Binary.PageSize(Binary.U16(h, 14)); VersionMajor = Binary.U32(h, 32);
        VersionMinor = Binary.U32(h, 36); UpdateMajor = Binary.U32(h, 40); UpdateMinor = Binary.U32(h, 648);
        LogSignature = Binary.Hex(h, 44, 28); CreationTimeBytes = Binary.Hex(h, 16, 8);
        PreviousGenerationTimeBytes = Binary.Hex(h, 24, 8);
    }
}
public sealed class CheckpointHeader : EseHeader
{
    public uint Generation { get; }
    public uint Sector { get; }
    public uint ByteOffset { get; }
    public string LogSignature { get; }
    internal CheckpointHeader(byte[] h) : base("Checkpoint", Binary.U32(h, 0))
    {
        ByteOffset = Binary.U16(h, 12); Sector = Binary.U16(h, 14);
        Generation = Binary.U32(h, 16); LogSignature = Binary.Hex(h, 20, 28);
    }
}
internal static class Binary
{
    internal static uint U16(byte[] b, int i) => (uint)(b[i] | b[i + 1] << 8);
    internal static uint U32(byte[] b, int i) => U16(b, i) | U16(b, i + 2) << 16;
    internal static uint PageSize(uint value) => value == 0 ? 4096u : value;
    internal static string Hex(byte[] b, int i, int length) => BitConverter.ToString(b, i, length).Replace("-", "").ToLowerInvariant();
}

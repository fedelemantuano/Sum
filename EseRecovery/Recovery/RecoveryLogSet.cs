using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EseRecovery.Inspection;
namespace EseRecovery;

public sealed class EseRecoveryOptions
{
    /// <summary>Defaults to the database directory. Acquired files are opened read-only.</summary>
    public string? LogDirectory { get; set; }
    /// <summary>Three-character ESE log basename, for example SRU. Null selects an unambiguous matching current log.</summary>
    public string? LogBaseName { get; set; }
}
public sealed class EseRecoveryReport
{
    public uint FirstGeneration { get; internal set; }
    public uint LastGeneration { get; internal set; }
    public int RedoneOperations { get; internal set; }
    public int AlreadyAppliedOperations { get; internal set; }
    public int UndoneOperations { get; internal set; }
    public int CommittedTransactions { get; internal set; }
    public int RolledBackTransactions { get; internal set; }
    public int ModifiedPages { get; internal set; }
    public int RecordsBeforeAttachment { get; internal set; }
    public string Scope => "SupportedSingleDatabaseLeafOperations";
}
internal static class RecoveryLogSet
{
    internal static List<EseLogScan> Read(string databasePath,DatabaseHeader database,EseRecoveryOptions options)
    {
        if(database.State!=2)throw new NotSupportedException("Recovery supports DirtyShutdown, not database state "+database.State+".");
        if(database.AttachPositionBytes.All(c=>c=='0'))throw new RecoveryRequiredException("Dirty database has no attachment position; recovery cannot establish its history.");
        if(database.MinimumRequiredGeneration==0)throw new RecoveryRequiredException("Dirty database declares no usable required log interval.");
        string directory=options.LogDirectory??Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        string? basename=options.LogBaseName;
        if(basename!=null&&!Regex.IsMatch(basename,"^[A-Za-z0-9]{3}$"))throw new ArgumentException("ESE log basename must contain exactly three ASCII letters or digits.");
        var candidates=new List<string>();int files=0;
        foreach(string path in Directory.EnumerateFiles(directory))
        {
            if(++files>10000)throw new InvalidDataException("Recovery directory file budget exceeded.");
            string name=Path.GetFileName(path);
            if(!Regex.IsMatch(name,@"^[A-Za-z0-9]{3}\.log$",RegexOptions.IgnoreCase))continue;
            if(basename!=null&&!name.Equals(basename+".log",StringComparison.OrdinalIgnoreCase))continue;
            if(basename==null&&new FileInfo(path).Length<4096)continue;
            byte[] raw=ReadHeader(path,false);
            if(Binary.Hex(raw,44,28)!=database.LogSignature)continue;
            if(Binary.U32(raw,0)!=EseChecksums.Header(raw))throw new InvalidDataException("Matching current log header checksum mismatch.");
            var h=HeaderInspector.Inspect(new MemoryStream(raw)) as LogHeader;
            if(h!=null&&h.LogSignature==database.LogSignature)candidates.Add(path);
        }
        if(candidates.Count!=1)throw new RecoveryRequiredException(candidates.Count==0?
            "Required current ESE log is missing or has a different signature; recovery was not performed.":
            "Multiple matching current ESE logs; specify LogDirectory and LogBaseName.");
        string current=candidates[0];basename=Path.GetFileNameWithoutExtension(current);
        var currentHeader=(LogHeader)HeaderInspector.Inspect(new MemoryStream(ReadHeader(current)));
        if(currentHeader.Generation<database.MaximumRequiredGeneration)throw new RecoveryRequiredException("Current log ends before the database's required generation range.");
        if((ulong)currentHeader.Generation-database.MinimumRequiredGeneration>127)throw new NotSupportedException("Recovery log generation budget exceeded.");
        var byGeneration=new Dictionary<uint,string>{{currentHeader.Generation,current}};
        var pattern=new Regex("^"+Regex.Escape(basename)+@"([0-9a-f]{5}|[0-9a-f]{8})\.log$",RegexOptions.IgnoreCase);
        foreach(string path in Directory.EnumerateFiles(directory))
        {
            var match=pattern.Match(Path.GetFileName(path));if(!match.Success)continue;
            uint generation=Convert.ToUInt32(match.Groups[1].Value,16);
            if(generation<database.MinimumRequiredGeneration)continue;
            if(generation>=currentHeader.Generation)throw new NotSupportedException("Ambiguous archived/current log rollover; recovery-aware selection is required.");
            if(byGeneration.ContainsKey(generation))throw new InvalidDataException("Duplicate required log generation "+generation+".");
            byGeneration.Add(generation,path);
        }
        var scans=new List<EseLogScan>();long bytes=0;int recordCount=0;LogHeader? previous=null;
        for(uint generation=database.MinimumRequiredGeneration;generation<=currentHeader.Generation;generation++)
        {
            if(!byGeneration.TryGetValue(generation,out var path))throw new RecoveryRequiredException("Missing required ESE log generation "+generation+".");
            bytes+=new FileInfo(path).Length;if(bytes>256L*1024*1024)throw new InvalidDataException("Recovery log byte budget exceeded.");
            var scan=EseLogScanner.Scan(path);recordCount=checked(recordCount+scan.Records.Count);
            if(recordCount>1000000)throw new InvalidDataException("Recovery log record budget exceeded.");
            var header=scan.Header;
            if((scan.HeaderBytes[639]&~7)!=0)throw new NotSupportedException("Unsupported ESE log header flags.");
            if(header.Generation!=generation||header.LogSignature!=database.LogSignature||header.PageSize!=database.PageSize)
                throw new InvalidDataException("Log identity, generation or page size mismatch: "+Path.GetFileName(path)+".");
            if(header.VersionMinor!=4000||(scan.Records.Count>0&&(header.UpdateMajor!=5||header.UpdateMinor!=16)))
                throw new NotSupportedException("Transaction replay currently supports log version 8.4000.5.16; encountered "+header.VersionMajor+"."+header.VersionMinor+"."+header.UpdateMajor+"."+header.UpdateMinor+" in generation "+generation+".");
            if(previous!=null&&header.PreviousGenerationTimeBytes!=previous.CreationTimeBytes)
                throw new InvalidDataException("Log generation creation-time chain mismatch.");
            if(previous!=null&&header.UpdateMinor>=13&&EseChecksums.U64(scan.HeaderBytes,653)!=scans[scans.Count-1].SegmentChecksum)
                throw new InvalidDataException("Previous-generation accumulated segment checksum mismatch.");
            ValidateAttachment(scan.HeaderBytes,database,scans.Count>0&&scan.Records.Count==0);
            previous=header;scans.Add(scan);
        }
        // The database header attachment is a redo boundary, not a reason to replay earlier incarnations.
        // Decode LGPOS as persisted little endian byte/sector/generation fields.
        var position=Enumerable.Range(0,8).Select(i=>Convert.ToByte(database.AttachPositionBytes.Substring(i*2,2),16)).ToArray();
        uint attachGeneration=Binary.U32(position,4);
        if(attachGeneration>=database.MinimumRequiredGeneration)
        {
            var attach=scans.SelectMany(scan=>scan.Records).SingleOrDefault(record=>record.Generation==attachGeneration&&record.Sector==Binary.U16(position,2)&&record.Offset==Binary.U16(position,0));
            if(attach==null||attach.Type!=15)throw new InvalidDataException("Database attachment position is absent from the validated logs.");
            var raw=attach.Bytes;
            if(raw[3]!=database.DatabaseId||Binary.Hex(raw,16,28)!=database.DatabaseSignature||Binary.Hex(raw,44,28)!=database.LogSignature)
                throw new InvalidDataException("AttachDB record identity mismatch.");
            if(Binary.Hex(raw,8,8)!=database.ConsistentPositionBytes)
                throw new NotSupportedException("AttachDB consistency position requires recovery-aware attachment reconciliation.");
        }
        return scans;
    }
    private static byte[] ReadHeader(string path,bool validate=true)
    {
        using var stream=File.OpenRead(path);var bytes=new byte[4096];int read=0;
        while(read<bytes.Length){int n=stream.Read(bytes,read,bytes.Length-read);if(n==0)throw new InvalidDataException("Truncated recovery log header.");read+=n;}
        if(validate&&Binary.U32(bytes,0)!=EseChecksums.Header(bytes))throw new InvalidDataException("Recovery log header checksum mismatch: "+Path.GetFileName(path)+".");
        return bytes;
    }
    private static void ValidateAttachment(byte[] header,DatabaseHeader database,bool emptyLog)
    {
        const int limit=671+2048;int at=671;bool matched=false;
        while(at<limit&&header[at]!=0)
        {
            if(at>limit-64)throw new InvalidDataException("Truncated ESE attachment metadata.");
            int names=(int)Binary.U16(header,at+2);
            if(names>limit-at-64)throw new InvalidDataException("Attachment name exceeds header.");
            if(header[at]!=database.DatabaseId)throw new NotSupportedException("Recovery log set contains another database attachment; multi-database recovery is unsupported.");
            if(matched||Binary.Hex(header,at+36,28)!=database.DatabaseSignature)
                throw new InvalidDataException("Database attachment signature mismatch.");
            if(Binary.Hex(header,at+20,8)!=database.AttachPositionBytes)
            {
                var position=Enumerable.Range(0,8).Select(i=>Convert.ToByte(database.AttachPositionBytes.Substring(i*2,2),16)).ToArray();
                uint generation=Binary.U32(header,4),targetGeneration=Binary.U32(position,4);
                if(generation>targetGeneration||targetGeneration<database.MinimumRequiredGeneration)
                    throw new NotSupportedException("Database attachment changed outside the validated recovery range.");
                ulong declared=((ulong)Binary.U32(header,at+24)<<32)|((ulong)Binary.U16(header,at+22)<<16)|Binary.U16(header,at+20);
                ulong target=((ulong)targetGeneration<<32)|((ulong)Binary.U16(position,2)<<16)|Binary.U16(position,0);
                if(declared>=target)throw new InvalidDataException("Log header attachment is newer than the database attachment.");
            }
            if((header[at+1]&~0x60)!=0)throw new NotSupportedException("Unsupported attachment flags.");
            matched=true;at+=64+names;
        }
        if(!matched&&!emptyLog)throw new NotSupportedException("Database is absent from log-header attachments; attach/create replay is required.");
        if(at>=limit)throw new InvalidDataException("Unterminated log attachment list.");
    }
}

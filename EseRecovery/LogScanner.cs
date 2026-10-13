using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EseRecovery.Inspection;
namespace EseRecovery;

/// <summary>A decoded record boundary, not an applied transaction.</summary>
public sealed class EseLogRecord
{
    internal byte[] Bytes { get; }
    public uint Generation { get; }
    public uint Sector { get; }
    public int Offset { get; }
    public byte Type { get; }
    public int Length { get; }
    public uint? DatabaseId { get; }
    public uint? PageNumber { get; }
    public ulong? DatabaseTime { get; }
    internal EseLogRecord(uint generation,uint sector,int offset,byte[] record)
    {
        Bytes=(byte[])record.Clone();Generation=generation;Sector=sector;Offset=offset;Type=record[0];Length=record.Length;
        if((Type>=29&&Type<=38)||Type==62)
        { DatabaseId=record[3];PageNumber=Binary.U32(record,4);DatabaseTime=EseChecksums.U64(record,8); }
    }
}
public sealed class EseLogScan
{
    internal byte[] HeaderBytes { get; }
    public LogHeader Header { get; }
    public IReadOnlyList<EseLogRecord> Records { get; }
    internal ulong SegmentChecksum { get; }
    public int ValidatedSegments { get; }
    public int ZeroSegments { get; }
    public string Recovery => "NotPerformed";
    internal EseLogScan(LogHeader header,List<EseLogRecord> records,int validated,int zero,byte[] headerBytes,ulong segmentChecksum)
    {SegmentChecksum=segmentChecksum;HeaderBytes=(byte[])headerBytes.Clone();Header=header;Records=records.AsReadOnly();ValidatedSegments=validated;ZeroSegments=zero;}
}
/// <summary>Read-only structural inspection of v8 log segments and selected record layouts.
/// Does not perform redo, undo, torn-write repair, or establish transaction consistency.</summary>
public static class EseLogScanner
{
    private const int RecordLimit=16*1024*1024;
    public static EseLogScan Scan(string path)
    {using var stream=File.OpenRead(path);return Scan(stream);}
    public static EseLogScan Scan(Stream stream)
    {
        if(!stream.CanSeek)throw new ArgumentException("A seekable log stream is required.");
        if(stream.Length>256L*1024*1024)throw new InvalidDataException("Log inspection size budget exceeded.");
        byte[] head=Read(stream,4096);
        if(Binary.U32(head,0)!=EseChecksums.Header(head))throw new InvalidDataException("Log header checksum mismatch.");
        var header=HeaderInspector.Inspect(new MemoryStream(head)) as LogHeader ?? throw new InvalidDataException("Expected an ESE log.");
        if(header.SectorSize!=4096||header.HeaderSectors!=1)throw new NotSupportedException("Log inspection requires 4096-byte segments and one header sector.");
        if(stream.Length!=(long)header.FileSectors*4096)throw new InvalidDataException("Log size differs from declared sector count.");
        var records=new List<EseLogRecord>();byte[]? fragment=null;int written=0;uint firstSector=0;int firstOffset=0;
        int validated=0,zero=0;bool tail=false;ulong accumulatedChecksum=0;
        for(uint sector=1;sector<header.FileSectors;sector++)
        {
            byte[] block=Read(stream,4096);
            if(block.All(b=>b==0)){tail=true;zero++;continue;}
            if(tail)throw new InvalidDataException("Nonzero segment after zero tail; recovery-aware tail selection is required.");
            if(EseChecksums.U64(block,0)!=EseChecksums.Page(block,sector))
                throw new InvalidDataException("Log segment checksum mismatch at sector "+sector+"; shadow/torn-write repair is unsupported.");
            if(Binary.U16(block,8)!=0||Binary.U16(block,10)!=sector||Binary.U32(block,12)!=header.Generation)
                throw new InvalidDataException("Log segment position mismatch at sector "+sector+".");
            if(Binary.U32(block,36)!=0x2000)throw new NotSupportedException("Unsupported log segment flags.");
            if(sector==header.FileSectors-1)throw new NotSupportedException("Nonzero reserved shadow sector requires recovery-aware selection.");
            accumulatedChecksum^=EseChecksums.U64(block,0);validated++;int offset=40;
            while(offset<block.Length)
            {
                byte type=block[offset];int origin=offset;
                if(type==76)
                {
                    if(fragment!=null)throw new InvalidDataException("Nested log fragments.");
                    EseDatabase.Require(block,offset,7);uint length=Binary.U32(block,offset+1);int part=(int)Binary.U16(block,offset+5);
                    if(length<1||length>RecordLimit||part<1||part>=length)throw new InvalidDataException("Invalid fragmented record length.");
                    fragment=new byte[length];written=part;firstSector=sector;firstOffset=offset;
                    EseDatabase.Require(block,offset+7,part);Buffer.BlockCopy(block,offset+7,fragment,0,part);offset+=7+part;
                }
                else if(type==77)
                {
                    if(fragment==null)throw new NotSupportedException("Fragment starts before supplied log; preceding generation is required.");
                    EseDatabase.Require(block,offset,3);int part=(int)Binary.U16(block,offset+1);
                    if(part<1||part>fragment.Length-written)throw new InvalidDataException("Invalid continuation length.");
                    EseDatabase.Require(block,offset+3,part);Buffer.BlockCopy(block,offset+3,fragment,written,part);written+=part;offset+=3+part;
                    if(written==fragment.Length)
                    {
                        if(RecordSize(fragment,0)!=fragment.Length)throw new InvalidDataException("Fragmented record size mismatch.");
                        Add(records,new EseLogRecord(header.Generation,firstSector,firstOffset,fragment));fragment=null;
                    }
                }
                else
                {
                    if(fragment!=null)throw new InvalidDataException("Record interrupts an unfinished log fragment.");
                    int length;
                    try{length=RecordSize(block,offset);}
                    catch(NotSupportedException ex){throw new NotSupportedException("Generation "+header.Generation+", sector "+sector+", offset "+offset+": "+ex.Message,ex);}
                    EseDatabase.Require(block,offset,length);
                    if(type!=0&&type!=4)Add(records,new EseLogRecord(header.Generation,sector,offset,EseDatabase.Slice(block,offset,length)));
                    offset+=length;
                }
                if(offset<=origin)throw new InvalidDataException("Log parser made no progress.");
            }
        }
        if(fragment!=null)throw new NotSupportedException("Unfinished record at log end; cross-generation recovery is unsupported.");
        return new EseLogScan(header,records,validated,zero,head,accumulatedChecksum);
    }
    private static void Add(List<EseLogRecord> records,EseLogRecord record)
    {if(records.Count>=1000000)throw new InvalidDataException("Log record budget exceeded.");records.Add(record);}
    private static byte[] Read(Stream s,int count)
    {var b=new byte[count];int at=0;while(at<count){int n=s.Read(b,at,count-at);if(n==0)throw new InvalidDataException("Truncated log.");at+=n;}return b;}
    internal static int RecordSize(byte[] b,int at)
    {
        EseDatabase.Require(b,at,1);int type=b[at];
        int Word(int offset){EseDatabase.Require(b,at+offset,2);return (int)Binary.U16(b,at+offset);}
        int Dword(int offset){EseDatabase.Require(b,at+offset,4);uint n=Binary.U32(b,at+offset);if(n>RecordLimit)throw new InvalidDataException("Log record size exceeds budget.");return (int)n;}
        switch(type)
        {
            case 0:case 4:case 17:case 23:return 1;
            case 1:return 568;
            case 2:case 18:return 18;
            case 5:case 6:case 7:case 21:case 79:return 4;
            case 8:case 9:case 53:return 8;
            case 10:return 7;
            case 11:case 12:case 13:return 11;
            case 15:return 75+Word(72);
            case 16:return 7+Word(4);
            case 22:case 61:return 5+Word(3);
            case 29:return 50+Word(44)+Word(46)+Word(48);
            case 30:return 48+Word(44)+Word(46);
            case 31:return 52+Word(44)+Word(46);
            case 32:case 35:return 44;
            case 33:case 34:return 50+(Word(44)&0x7fff);
            case 36:return 56+Word(44)+Word(46)+Word(54);
            case 38:return 46+Word(44);
            case 39:return 51+Word(44)+Word(46);
            case 37:return 54+Word(44)+Word(46);
            case 93:return 58+Word(44)+Word(46);
            case 49:return 576;
            case 50:case 52:case 80:return 26;
            case 51:return 9;
            case 55:return 3;
            case 62:return 110+Word(96)+Word(98)+Word(100)+Dword(106);
            case 95:return 20;
            default:
                if(type>=67&&type<=75||type>=82&&type<=91)return 5+Dword(1);
                throw new NotSupportedException("Unsupported log record type "+type+"; no bytes were skipped.");
        }
    }
}

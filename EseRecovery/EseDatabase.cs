using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EseRecovery.Inspection;
namespace EseRecovery;

/// <summary>Read-only page reader. Stored-page inspection never claims transaction recovery.</summary>
public sealed class EseDatabase : IDisposable
{
    private readonly FileStream stream;
    private readonly Dictionary<uint,byte[]> pages = new Dictionary<uint,byte[]>();
    private readonly Dictionary<uint,Dictionary<string,byte[]>> longValues = new Dictionary<uint,Dictionary<string,byte[]>>();
    private readonly List<EseTable> tables = new List<EseTable>();
    public DatabaseHeader Header { get; }
    public string HeaderSource { get; }
    public IReadOnlyList<EseTable> Tables => tables.AsReadOnly();
    private readonly Dictionary<uint,byte[]> recoveredPages = new Dictionary<uint,byte[]>();
    public EseRecoveryReport? Recovery { get; }
    public bool IsRecovered => Recovery != null;
    public uint EffectiveState => IsRecovered ? 3u : Header.State;
    public string Consistency => IsRecovered ? "RecoveredSupportedTransactions" : Header.State == 3 ? "HeaderDeclaresCleanShutdown" : "UnrecoveredStoredPages";
    public int ValidatedPageCount => pages.Count;
    private readonly uint pageCount;
    private const int ValueLimit = 64 * 1024 * 1024;

    public static EseDatabase Open(string path) => new EseDatabase(path,false,null);
    public static EseDatabase Open(string path,EseRecoveryOptions options) => new EseDatabase(path,false,options??throw new ArgumentNullException(nameof(options)));
    /// <summary>Explicit forensic inspection of stored pages, including dirty databases. Not recovered records.</summary>
    public static EseDatabase InspectStoredPages(string path) => new EseDatabase(path,true,null);
    private EseDatabase(string path,bool allowDirty,EseRecoveryOptions? options)
    {
        stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        try
        {
            var primary=ReadAt(0,4096);
            bool valid=Binary.U32(primary,0)==EseChecksums.Header(primary);
            byte[] chosen=primary; string source="Primary";
            uint rawSize=valid ? Binary.U32(primary,236) : 0;
            if (valid && rawSize!=0 && rawSize!=4096 && rawSize!=8192)
                throw new NotSupportedException("Only 4/8 KiB pages are supported by the record reader.");
            int size=rawSize==0?4096:checked((int)rawSize);
            if (!valid)
            {
                var candidates=new List<byte[]>();
                foreach (int possible in new[]{4096,8192})
                {
                    if(stream.Length<possible+4096)continue;
                    var shadow=ReadAt(possible,4096);
                    if(Binary.U32(shadow,4)==0x89abcdef && Binary.U32(shadow,0)==EseChecksums.Header(shadow) && Binary.PageSize(Binary.U32(shadow,236))==possible) candidates.Add(shadow);
                }
                if(candidates.Count!=1)throw new InvalidDataException("Cannot select a valid unambiguous ESE header.");
                chosen=candidates[0];source="Shadow";size=(int)Binary.PageSize(Binary.U32(chosen,236));
            }
            else
            {
                var shadow=ReadAt(size,4096);
                if(Binary.U32(shadow,0)==EseChecksums.Header(shadow) && !primary.SequenceEqual(shadow))
                    throw new NotSupportedException("Two valid divergent ESE headers require recovery-aware selection.");
            }
            Header=(DatabaseHeader)HeaderInspector.Inspect(new MemoryStream(chosen));HeaderSource=source;
            if(Header.PageSize!=4096 && Header.PageSize!=8192)throw new NotSupportedException("Only 4/8 KiB page records supported.");
            if(stream.Length%Header.PageSize!=0 || stream.Length<3*Header.PageSize)throw new InvalidDataException("Invalid database length.");
            pageCount=checked((uint)(stream.Length/Header.PageSize-1));
            if(Header.State!=3&&!allowDirty)
            {
                var logs=RecoveryLogSet.Read(path,Header,options??new EseRecoveryOptions());
                var result=new TransactionReplay(Header,Page).Run(logs);
                recoveredPages=result.Pages;Recovery=result.Report;
            }
            LoadCatalog();
        }
        catch { stream.Dispose(); throw; }
    }
    public IEnumerable<EseRow> ReadRows(EseTable table)
    {
        if(!tables.Contains(table))throw new ArgumentException("Table belongs to another database.",nameof(table));
        foreach(var node in Nodes(table.RootPage))
        {
            var values=new Dictionary<string,byte[]?>(StringComparer.Ordinal);
            foreach(var col in table.columns)
            {
                var value=ReadField(node.Data,col, out byte flags);
                if(value!=null)
                {
                    if((flags & 0xc0)!=0)throw new NotSupportedException("Unsupported tagged-column flags.");
                    if((flags & (8|16))!=0)throw new NotSupportedException("Multi-valued column requires explicit sequence selection: " + table.Name+"."+col.Name);
                    if((flags & 4)!=0)value=GetLongValue(table,value);
                    else if((flags & 2)!=0)value=ValueCompression.Decode(value,ValueLimit);
                    // Keep decompressed stored bytes; consumers choose text normalization.
                }
                values.Add(col.Name,value);
            }
            yield return new EseRow(values,node.Page,node.Tag);
        }
    }
    public static byte[] NormalizeText(byte[] value,uint codePage)
    {
        if(codePage==1200)
        {
            if(value.Length%2!=0)throw new InvalidDataException("Odd UTF-16 value length.");
            var strict=new UnicodeEncoding(false,false,true);strict.GetString(value);return value;
        }
        if(codePage==20127 && value.Any(b=>b>127))throw new InvalidDataException("Non-ASCII byte in an ASCII column.");
        if(codePage==0||codePage==1252||codePage==20127)
        {
            const string cp="€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008dŽ\u008f\u0090‘’“”•–—˜™š›œ\u009džŸ";
            var chars=new char[value.Length];
            for(int i=0;i<value.Length;i++)chars[i]=value[i]>=128&&value[i]<=159?cp[value[i]-128]:(char)value[i];
            return Encoding.Unicode.GetBytes(chars);
        }
        if(codePage==65001)return Encoding.Unicode.GetBytes(new UTF8Encoding(false,true).GetString(value));
        throw new NotSupportedException("Text code page " + codePage + " is not supported.");
    }
    private byte[] ReadAt(long offset,int count)
    {
        if(offset<0 || offset>stream.Length-count)throw new InvalidDataException("ESE offset outside file.");
        var data=new byte[count];stream.Position=offset;int read=0;
        while(read<count){int n=stream.Read(data,read,count-read);if(n==0)throw new EndOfStreamException();read+=n;}
        return data;
    }
    private byte[] Page(uint number)
    {
        if(number==0||number>=pageCount)throw new InvalidDataException("Page number outside database.");
        if(recoveredPages.TryGetValue(number,out var recovered))return recovered;
        if(!pages.TryGetValue(number,out var bytes))
        {
            bytes=ReadAt(((long)number+1)*Header.PageSize,(int)Header.PageSize);
            EseChecksums.CheckPage(bytes,number);
            if(pages.Count>=16384)throw new InvalidDataException("Page cache limit exceeded.");
            pages.Add(number,bytes);
        }
        return bytes;
    }
    internal sealed class Node
    {
        internal byte[] Key=Array.Empty<byte>();internal byte[] Data=Array.Empty<byte>();internal uint Page;internal int Tag;
    }
    private IEnumerable<Node> Nodes(uint root)
    {
        var pending=new Stack<uint>();var visited=new HashSet<uint>();pending.Push(root);uint? objectId=null;
        while(pending.Count>0)
        {
            uint number=pending.Pop();if(!visited.Add(number))throw new InvalidDataException("Cycle or duplicate child in ESE tree.");
            var page=Page(number);uint flags=Binary.U32(page,36);uint owner=Binary.U32(page,24);
            if(objectId==null)objectId=owner;else if(objectId!=owner)throw new InvalidDataException("Page belongs to a different ESE tree.");
            int count=(int)(Binary.U16(page,34)&0xfff);int reserved=Math.Max(1,(int)(Binary.U16(page,34)>>12));
            int used=(int)Binary.U16(page,32);
            if(count<reserved||40+used>page.Length-count*4)throw new InvalidDataException("Invalid page tag boundaries.");
            if((flags&8)!=0)continue;
            byte[] prefix=(flags&1)==0?TagBytes(page,0,count,used,out _):Array.Empty<byte>();
            var children=new List<uint>();
            for(int tag=reserved;tag<count;tag++)
            {
                byte[] data=TagBytes(page,tag,count,used,out int tagFlags);
                if((tagFlags&2)!=0)continue;
                int at=0;int common=0;
                if((tagFlags&4)!=0){common=U16(data,at)&0x1fff;at+=2;if(common>prefix.Length)throw new InvalidDataException("Invalid compressed key prefix.");}
                int suffix=U16(data,at)&0x1fff;at+=2;
                var key=new byte[common+suffix];Buffer.BlockCopy(prefix,0,key,0,common);
                var keyPart=Slice(data,at,suffix);Buffer.BlockCopy(keyPart,0,key,common,suffix);at+=suffix;
                var body=Slice(data,at,data.Length-at);
                if((flags&2)!=0)yield return new Node{Key=key,Data=body,Page=number,Tag=tag};
                else {if(body.Length!=4)throw new InvalidDataException("Invalid branch child.");children.Add(Binary.U32(body,0));}
            }
            for(int i=children.Count-1;i>=0;i--)pending.Push(children[i]);
        }
    }
    private static byte[] TagBytes(byte[] page,int tag,int count,int used,out int flags)
    {
        int pos=page.Length-4*(tag+1);int size=U16(page,pos)&0x1fff;int rawOffset=U16(page,pos+2);
        int offset=rawOffset&0x1fff;flags=rawOffset>>13;
        if(offset>used-size || (size==0&&tag!=0))throw new InvalidDataException("Tag outside page data.");
        return Slice(page,40+offset,size);
    }
    private void LoadCatalog()
    {
        var catalog=new List<EseColumn>();int offset=4;
        int[] sizes={4,2,4,4,4,4,4,1,2,4,2,4};
        for(uint i=1;i<=sizes.Length;i++){catalog.Add(new EseColumn(i,"F"+i,9,(uint)sizes[i-1],0,0,offset,null));offset+=sizes[i-1];}
        for(uint i=128;i<=137;i++)catalog.Add(new EseColumn(i,"V"+i,9,255,0,0,0,null));
        var records=Nodes(4).Select(n=>n.Data).ToList();var byId=new Dictionary<uint,EseTable>();
        byte[]? Field(byte[] row,uint id)=>ReadField(row,catalog.First(c=>c.Id==id),out _);
        uint Number(byte[] row,uint id){var v=Field(row,id);return v==null?0:v.Length==2?Binary.U16(v,0):Binary.U32(v,0);}
        foreach(var row in records)
        {
            if(Number(row,2)!=1)continue;
            var name=Field(row,128)??throw new InvalidDataException("Unnamed catalog table.");
            if(Field(row,130)?.Length>0)throw new NotSupportedException("Template tables are not supported.");
            var table=new EseTable(Encoding.ASCII.GetString(name),Number(row,1),Number(row,4));
            if(byId.ContainsKey(table.ObjectId))throw new InvalidDataException("Duplicate table ID.");
            byId.Add(table.ObjectId,table);tables.Add(table);
        }
        foreach(var row in records)
        {
            uint kind=Number(row,2);if(!byId.TryGetValue(Number(row,1),out var table))continue;
            if(kind==4)table.LongValueRoot=Number(row,4);
            if(kind!=2)continue;
            var name=Field(row,128)??throw new InvalidDataException("Unnamed column.");
            var column=new EseColumn(Number(row,3),Encoding.ASCII.GetString(name),Number(row,4),Number(row,5),Number(row,7),Number(row,6),(int)Number(row,9),Field(row,131));
            if(table.columns.Any(c=>c.Id==column.Id||c.Name==column.Name))throw new InvalidDataException("Duplicate column.");
            table.columns.Add(column);
        }
        foreach(var table in tables)
        {
            table.columns.Sort((a,b)=>a.Id.CompareTo(b.Id));
            int fixedOffset=4;uint previous=0;
            foreach(var column in table.columns.Where(c=>c.Id<128))
            {
                if(column.Id!=previous+1)throw new NotSupportedException("Sparse fixed-column schema requires deleted-column metadata.");
                column.Offset=fixedOffset;fixedOffset=checked(fixedOffset+(int)column.Size);previous=column.Id;
            }
        }
    }
    private static int U16(byte[] b,int at){Require(b,at,2);return (int)Binary.U16(b,at);}
    internal static void Require(byte[] b,int at,int size)
    {if(at<0||size<0||at>b.Length-size)throw new InvalidDataException("Record field outside bounds.");}
    internal static byte[] Slice(byte[] b,int at,int size){Require(b,at,size);var result=new byte[size];Buffer.BlockCopy(b,at,result,0,size);return result;}
    internal static byte[]? ReadField(byte[] record,EseColumn column,out byte flags)
    {
        flags=0;Require(record,0,4);int fixedLast=record[0];int variableLast=record[1];int varAt=U16(record,2);
        int bitmap=(fixedLast+7)/8;Require(record,varAt-bitmap,bitmap);
        if(varAt-bitmap<4)throw new InvalidDataException("Invalid fixed-record boundary.");
        uint id=column.Id;
        if(id<128)
        {
            if(id==0)throw new InvalidDataException("Invalid column ID.");
            if(id>fixedLast)return column.Default==null?null:(byte[])column.Default.Clone();
            if((record[varAt-bitmap+(id-1)/8]&(1<<(int)((id-1)%8)))!=0)return null;
            if(column.Offset<4||column.Size>int.MaxValue||column.Offset>varAt-bitmap-(int)column.Size)
                throw new InvalidDataException("Fixed column exceeds record boundary.");
            return Slice(record,column.Offset,(int)column.Size);
        }
        int count=Math.Max(0,variableLast-127);int valuesAt=varAt+count*2;Require(record,varAt,count*2);
        if(id<256)
        {
            if(id>variableLast)return column.Default==null?null:(byte[])column.Default.Clone();
            int index=(int)id-128;int end=U16(record,varAt+index*2);int start=index==0?0:U16(record,varAt+(index-1)*2)&0x7fff;
            if((end&0x8000)!=0)return null;
            return Slice(record,valuesAt+start,(end&0x7fff)-start);
        }
        int taggedAt=valuesAt+(count==0?0:U16(record,varAt+(count-1)*2)&0x7fff);
        Require(record,taggedAt,0);if(taggedAt==record.Length)return column.Default==null?null:(byte[])column.Default.Clone();
        int first=U16(record,taggedAt+2)&0x3fff;
        if(first<4||first%4!=0)throw new InvalidDataException("Invalid tagged-column directory.");
        Require(record,taggedAt,first);
        for(int i=0;i<first;i+=4)
        {
            int fieldId=U16(record,taggedAt+i);int off=U16(record,taggedAt+i+2);int start=off&0x3fff;
            int end=i+4==first?record.Length-taggedAt:U16(record,taggedAt+i+6)&0x3fff;
            if(start<first||end<start)throw new InvalidDataException("Invalid tagged value boundary.");
            Require(record,taggedAt+start,end-start);
            if(fieldId!=id)continue;
            if((off&0x4000)!=0){if(end==start)throw new InvalidDataException("Missing tagged flags.");flags=record[taggedAt+start++];}
            if((flags&0x20)!=0)return null;
            return Slice(record,taggedAt+start,end-start);
        }
        return column.Default==null?null:(byte[])column.Default.Clone();
    }
    private byte[] GetLongValue(EseTable table,byte[] lid)
    {
        if(lid.Length!=4&&lid.Length!=8)throw new InvalidDataException("Invalid long-value identifier.");
        if(table.LongValueRoot==0)throw new InvalidDataException("Missing long-value tree.");
        if(!longValues.TryGetValue(table.ObjectId,out var entries))
        {
            entries=new Dictionary<string,byte[]>(StringComparer.Ordinal);long total=0;
            foreach(var node in Nodes(table.LongValueRoot))
            {total+=node.Data.Length;if(total>ValueLimit)throw new InvalidDataException("Long-value tree budget exceeded.");entries.Add(Binary.Hex(node.Key,0,node.Key.Length),node.Data);}
            longValues.Add(table.ObjectId,entries);
        }
        var reversed=(byte[])lid.Clone();Array.Reverse(reversed);string key=Binary.Hex(reversed,0,reversed.Length);
        if(!entries.TryGetValue(key,out var root)||root.Length<8)throw new InvalidDataException("Missing long-value root.");
        uint length=Binary.U32(root,4);if(length>ValueLimit)throw new InvalidDataException("Long-value size limit exceeded.");
        if(root.Length>8&&root[8]!=0)throw new NotSupportedException("Encrypted/flagged long values are unsupported.");
        var parts=entries.Where(p=>p.Key.Length==key.Length+8&&p.Key.StartsWith(key,StringComparison.Ordinal)).OrderBy(p=>p.Key,StringComparer.Ordinal).ToArray();
        var result=new byte[length];int written=0;
        for(int i=0;i<parts.Length;i++)
        {
            uint start=Convert.ToUInt32(parts[i].Key.Substring(key.Length),16);
            uint end=i+1==parts.Length?length:Convert.ToUInt32(parts[i+1].Key.Substring(key.Length),16);
            if(start!=written||end<=start||end>length)throw new InvalidDataException("Non-contiguous long value.");
            var chunk=parts[i].Value;if(chunk.Length!=end-start)chunk=ValueCompression.Decode(chunk,ValueLimit);
            if(chunk.Length!=end-start)throw new InvalidDataException("Long-value chunk size mismatch.");
            Buffer.BlockCopy(chunk,0,result,written,chunk.Length);written+=chunk.Length;
        }
        if(written!=length)throw new InvalidDataException("Incomplete long value.");return result;
    }
    public void Dispose()=>stream.Dispose();
}

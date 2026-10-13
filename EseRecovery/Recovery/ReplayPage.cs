using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EseRecovery.Inspection;
namespace EseRecovery;

// Private working page. Never writes an acquired database or shares its byte buffers.
internal sealed class ReplayPage
{
    internal sealed class Entry
    {
        internal byte[] Key, Data;
        internal int Flags;
        internal Entry(byte[] key, byte[] data, int flags) { Key=key;Data=data;Flags=flags; }
        internal Entry Copy() => new Entry((byte[])Key.Clone(),(byte[])Data.Clone(),Flags);
    }
    private readonly byte[] original;
    private readonly byte[] prefix;
    internal readonly List<Entry> Entries = new List<Entry>();
    internal ulong Time;
    internal uint Owner => Binary.U32(original,24);
    internal ReplayPage(byte[] page)
    {
        original=(byte[])page.Clone();Time=EseChecksums.U64(page,8);
        uint flags=Binary.U32(page,36);
        if((flags&2)==0 || (flags&8)!=0)throw new NotSupportedException("Replay requires a nonempty leaf page; structural tree recovery is unsupported.");
        int count=(int)(Binary.U16(page,34)&0xfff),reserved=(int)(Binary.U16(page,34)>>12);
        if(count<1||reserved>1)throw new NotSupportedException("Replay requires one reserved page tag.");
        int used=(int)Binary.U16(page,32);
        if(40+used>page.Length-count*4)throw new InvalidDataException("Invalid replay page boundaries.");
        byte[] Tag(int index,out int tagFlags)
        {
            int at=page.Length-4*(index+1),size=(int)(Binary.U16(page,at)&0x1fff);
            int raw=(int)Binary.U16(page,at+2),offset=raw&0x1fff;tagFlags=raw>>13;
            if(offset>used-size)throw new InvalidDataException("Replay tag outside page.");
            return EseDatabase.Slice(page,40+offset,size);
        }
        prefix=Tag(0,out _);
        var ranges=new List<(int Start,int End)>();
        for(int i=0;i<count;i++)
        {
            int at=page.Length-4*(i+1),offset=(int)(Binary.U16(page,at+2)&0x1fff),size=(int)(Binary.U16(page,at)&0x1fff);
            if(size>0)ranges.Add((offset,offset+size));
        }
        var sorted=ranges.OrderBy(r=>r.Start).ToArray();
        for(int i=1;i<sorted.Length;i++)if(sorted[i].Start<sorted[i-1].End)throw new InvalidDataException("Overlapping replay page tags.");
        for(int i=1;i<count;i++)
        {
            byte[] raw=Tag(i,out int tagFlags);int at=0,common=0;
            if((tagFlags&4)!=0)
            {
                EseDatabase.Require(raw,0,2);common=(int)(Binary.U16(raw,0)&0x1fff);at+=2;
                if((flags&1)!=0||common>prefix.Length)throw new InvalidDataException("Invalid replay key prefix.");
            }
            EseDatabase.Require(raw,at,2);int suffix=(int)(Binary.U16(raw,at)&0x1fff);at+=2;
            var key=new byte[common+suffix];Buffer.BlockCopy(prefix,0,key,0,common);
            Buffer.BlockCopy(EseDatabase.Slice(raw,at,suffix),0,key,common,suffix);at+=suffix;
            Entries.Add(new Entry(key,EseDatabase.Slice(raw,at,raw.Length-at),tagFlags&3));
        }
    }
    internal int Find(byte[] key)
    {
        int found=-1;
        for(int i=0;i<Entries.Count;i++)if(Entries[i].Key.SequenceEqual(key))
        {if(found>=0)throw new NotSupportedException("Replay undo requires unique bookmarks.");found=i;}
        return found;
    }
    internal Entry At(int line)
    {if(line<0||line>=Entries.Count)throw new InvalidDataException("Replay line outside page.");return Entries[line];}
    internal byte[] Serialize(uint number)
    {
        var result=new byte[original.Length];Buffer.BlockCopy(original,0,result,0,40);
        var tags=new List<(byte[] Data,int Flags)>{(prefix,0)};
        foreach(var entry in Entries)
        {
            int common=0;
            if((Binary.U32(original,36)&1)==0)
                while(common<entry.Key.Length&&common<prefix.Length&&entry.Key[common]==prefix[common])common++;
            if(common<=2)common=0;
            int suffix=entry.Key.Length-common;
            if(entry.Key.Length>0x1fff)throw new InvalidDataException("Replay key exceeds page format.");
            var raw=new byte[(common>0?4:2)+suffix+entry.Data.Length];int at=0;
            if(common>0){Put16(raw,at,common);at+=2;}
            Put16(raw,at,suffix);at+=2;Buffer.BlockCopy(entry.Key,common,raw,at,suffix);at+=suffix;
            Buffer.BlockCopy(entry.Data,0,raw,at,entry.Data.Length);tags.Add((raw,entry.Flags|(common>0?4:0)));
        }
        int used=tags.Sum(t=>t.Data.Length),free=result.Length-40-used-tags.Count*4;
        if(free<0||tags.Count>0xfff)throw new NotSupportedException("Recovered page needs a structural split; no partial state was exposed.");
        int position=0;
        for(int i=0;i<tags.Count;i++)
        {
            var tag=tags[i];if(tag.Data.Length>0x1fff)throw new InvalidDataException("Replay tag exceeds format.");
            Buffer.BlockCopy(tag.Data,0,result,40+position,tag.Data.Length);
            int at=result.Length-4*(i+1);Put16(result,at,tag.Data.Length);Put16(result,at+2,position|(tag.Flags<<13));position+=tag.Data.Length;
        }
        Put64(result,8,Time);Put16(result,28,free);Put16(result,30,0);Put16(result,32,used);Put16(result,34,tags.Count);
        if((Binary.U32(result,36)&0x2000)!=0)Put64(result,0,EseChecksums.Page(result,number));
        else {Put32(result,4,number);Put32(result,0,EseChecksums.Header(result));}
        return result;
    }
    internal static void Put16(byte[] b,int at,int value){b[at]=(byte)value;b[at+1]=(byte)(value>>8);}
    internal static void Put32(byte[] b,int at,uint value){for(int i=0;i<4;i++)b[at+i]=(byte)(value>>(8*i));}
    internal static void Put64(byte[] b,int at,ulong value){for(int i=0;i<8;i++)b[at+i]=(byte)(value>>(8*i));}
}

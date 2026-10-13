using System;
using System.IO;
using EseRecovery.Inspection;
namespace EseRecovery;

// Persisted DIFFHDR / DIFFHDR2 layouts from Microsoft's logapi.hxx and logdiff.cxx.
internal static class RecordDelta
{
    internal static byte[] Apply(byte[] before,byte[] diff,bool format2,int expectedLength,int limit)
    {
        if(expectedLength<0||expectedLength>limit)throw new InvalidDataException("Delta after-image exceeds the page budget.");
        var result=new byte[expectedLength];int at=0,oldAt=0,newAt=0;
        void Copy(byte[] source,int from,int count)
        {
            EseDatabase.Require(source,from,count);EseDatabase.Require(result,newAt,count);
            Buffer.BlockCopy(source,from,result,newAt,count);newAt+=count;
        }
        int Length(bool wide)
        {
            EseDatabase.Require(diff,at,wide?2:1);int n=wide?(int)Binary.U16(diff,at):diff[at];at+=wide?2:1;return n;
        }
        while(at<diff.Length)
        {
            EseDatabase.Require(diff,at,format2?3:2);int header=(int)Binary.U16(diff,at);
            int offset=format2?header:header&0x1fff;
            int flags=format2?diff[at+2]:header>>8;at+=format2?3:2;
            if(format2&&(flags&31)!=0)throw new NotSupportedException("Unknown DIFFHDR2 flags.");
            if(offset<oldAt||offset>before.Length)throw new InvalidDataException("Delta offsets overlap or exceed the before-image.");
            Copy(before,oldAt,offset-oldAt);oldAt=offset;
            bool wide=(flags&0x80)!=0,insert=(flags&0x40)!=0,same=(flags&0x20)!=0;
            int added=Length(wide),removed=insert?0:same?added:Length(wide);
            EseDatabase.Require(before,oldAt,removed);Copy(diff,at,added);at+=added;oldAt+=removed;
        }
        Copy(before,oldAt,before.Length-oldAt);
        if(newAt!=expectedLength)throw new InvalidDataException("Delta decoded length differs from logged after-image size.");
        return result;
    }
}

using System;
using System.IO;
using EseRecovery.Inspection;
namespace EseRecovery;
internal static class ValueCompression
{
    internal static byte[] Decode(byte[] data,int limit)
    {
        if(data.Length<2)throw new InvalidDataException("Truncated compressed value.");
        int mode=data[0]>>3;
        if(mode==1||mode==2)
        {
            int bits=(data.Length-2)*8+(data[0]&7)+1;int count=bits/7;int width=mode==2?2:1;
            if(count>limit/width)throw new InvalidDataException("Decompression limit exceeded.");
            var output=new byte[count*width];
            for(int i=0;i<count;i++)
            {
                int bit=i*7;int at=1+bit/8;int value=data[at]>>(bit%8);
                if(bit%8>1&&at+1<data.Length)value|=data[at+1]<<(8-bit%8);
                output[i*width]=(byte)(value&127);
            }
            return output;
        }
        if(mode!=3||data[0]!=0x18)throw new NotSupportedException("Unsupported ESE compression scheme " + mode + ".");
        EseDatabase.Require(data,0,3);int size=(int)Binary.U16(data,1);
        if(size>limit)throw new InvalidDataException("Decompression limit exceeded.");
        var result=new byte[size];int source=3,dest=0,nibble=-1;uint indicator=0;int remaining=0;
        int ReadByte(){EseDatabase.Require(data,source,1);return data[source++];}
        int ReadWord(){int a=ReadByte();return a|(ReadByte()<<8);}
        while(dest<size)
        {
            if(remaining==0){EseDatabase.Require(data,source,4);indicator=Binary.U32(data,source);source+=4;remaining=32;}
            remaining--;
            if((indicator&(1u<<remaining))==0){result[dest++]=(byte)ReadByte();continue;}
            int token=ReadWord();int distance=(token>>3)+1;int length=token&7;
            if(length==7)
            {
                if(nibble<0){nibble=source;length=ReadByte()&15;}else{length=data[nibble]>>4;nibble=-1;}
                if(length==15)
                {
                    length=ReadByte();
                    if(length==255)
                    {
                        length=ReadWord();
                        if(length==0){EseDatabase.Require(data,source,4);uint wide=Binary.U32(data,source);source+=4;if(wide>65535)throw new InvalidDataException("Invalid match length.");length=(int)wide;}
                        if(length<22)throw new InvalidDataException("Invalid extended match.");length-=22;
                    }
                    length+=15;
                }
                length+=7;
            }
            length+=3;
            if(distance>dest||length>size-dest)throw new InvalidDataException("Invalid XPRESS back-reference.");
            for(int i=0;i<length;i++){result[dest]=result[dest-distance];dest++;}
        }
        return result;
    }
}

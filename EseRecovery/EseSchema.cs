using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
namespace EseRecovery;

public sealed class EseColumn
{
    public uint Id { get; }
    public string Name { get; }
    public uint Type { get; }
    public uint Size { get; }
    public uint CodePage { get; }
    public uint Flags { get; }
    internal int Offset { get; set; }
    internal byte[]? Default { get; }
    internal EseColumn(uint id,string name,uint type,uint size,uint codePage,uint flags,int offset,byte[]? defaultValue)
    { Id=id; Name=name; Type=type; Size=size; CodePage=codePage; Flags=flags; Offset=offset; Default=defaultValue; }
    public string TypeName => Type < Names.Length ? Names[Type] : "Unknown";
    private static readonly string[] Names = {"Nil","Bit","UnsignedByte","Short","Long","Currency","IEEESingle","IEEEDouble","DateTime","Binary","Text","LongBinary","LongText","SLV","UnsignedLong","LongLong","GUID","UnsignedShort","UnsignedLongLong"};
}
public sealed class EseTable
{
    public string Name { get; }
    public uint ObjectId { get; }
    public uint RootPage { get; }
    public IReadOnlyList<EseColumn> Columns => columns.AsReadOnly();
    internal readonly List<EseColumn> columns = new List<EseColumn>();
    internal uint LongValueRoot;
    internal EseTable(string name,uint objectId,uint rootPage) { Name=name; ObjectId=objectId; RootPage=rootPage; }
}
public sealed class EseRow
{
    public IReadOnlyDictionary<string, byte[]?> Values { get; }
    public uint PageNumber { get; }
    public int Tag { get; }
    internal EseRow(Dictionary<string,byte[]?> values,uint page,int tag)
    { Values = new ReadOnlyDictionary<string,byte[]?>(values); PageNumber=page;Tag=tag; }
}
public sealed class RecoveryRequiredException : InvalidOperationException
{
    public RecoveryRequiredException(string message):base(message) { }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EseRecovery.Inspection;
namespace EseRecovery;

internal sealed class TransactionReplay
{
    private sealed class Transaction
    {
        internal uint Id;
        internal int Level;
        internal Transaction(uint id,int level){Id=id;Level=level;}
    }
    private sealed class Undo
    {
        internal ushort Session;
        internal uint Transaction,Id,Page;
        internal int Level,Operation;
        internal byte[]? Key,Before;
        internal int BeforeFlags;
        internal bool Active=true;
    }
    private readonly uint databaseId;
    private readonly ulong attachment;
    private readonly Func<uint,byte[]> read;
    private readonly Dictionary<uint,ReplayPage> pages=new Dictionary<uint,ReplayPage>();
    private readonly HashSet<uint> changed=new HashSet<uint>();
    private readonly Dictionary<ushort,Transaction> transactions=new Dictionary<ushort,Transaction>();
    private readonly List<Undo> undo=new List<Undo>();
    private readonly Dictionary<(ushort,uint,uint),Undo> versions=new Dictionary<(ushort,uint,uint),Undo>();
    private readonly EseRecoveryReport report=new EseRecoveryReport();
    private long undoBytes;
    internal TransactionReplay(DatabaseHeader database,Func<uint,byte[]> read)
    {
        databaseId=database.DatabaseId;this.read=read;
        var p=Enumerable.Range(0,8).Select(i=>Convert.ToByte(database.AttachPositionBytes.Substring(i*2,2),16)).ToArray();
        attachment=((ulong)Binary.U32(p,4)<<32)|((ulong)Binary.U16(p,2)<<16)|Binary.U16(p,0);
    }
    internal (Dictionary<uint,byte[]> Pages,EseRecoveryReport Report) Run(IReadOnlyList<EseLogScan> logs)
    {
        if(logs.Count==0)throw new RecoveryRequiredException("No validated logs supplied for replay.");
        report.FirstGeneration=logs[0].Header.Generation;report.LastGeneration=logs[logs.Count-1].Header.Generation;
        foreach(var log in logs)foreach(var record in log.Records)
        {
            ulong position=((ulong)record.Generation<<32)|((ulong)record.Sector<<16)|(uint)record.Offset;
            if(position<=attachment){report.RecordsBeforeAttachment++;continue;}
            try{Process(record.Bytes);}
            catch(NotSupportedException ex){throw new NotSupportedException(Location(record)+ex.Message,ex);}
            catch(InvalidDataException ex){throw new InvalidDataException(Location(record)+ex.Message,ex);}
        }
        // Undo losers in reverse global operation order, preserving changes committed by other sessions.
        for(int i=undo.Count-1;i>=0;i--)if(undo[i].Active)UndoOne(undo[i]);
        report.RolledBackTransactions+=transactions.Count;
        var result=new Dictionary<uint,byte[]>();
        foreach(uint number in changed)result.Add(number,pages[number].Serialize(number));
        report.ModifiedPages=result.Count;
        return (result,report);
    }
    private static string Location(EseLogRecord r)=>"Generation "+r.Generation+", sector "+r.Sector+", offset "+r.Offset+", type "+r.Type+": ";
    private ReplayPage Page(uint number)
    {
        if(!pages.TryGetValue(number,out var page))
        {
            if(pages.Count>=8192)throw new InvalidDataException("Replay page budget exceeded.");
            page=new ReplayPage(read(number));pages.Add(number,page);
        }
        return page;
    }
    private Transaction Active(ushort session)
    {
        if(!transactions.TryGetValue(session,out var trx))throw new NotSupportedException("Transaction start is absent from the supplied recovery range.");
        return trx;
    }
    private void Process(byte[] b)
    {
        int type=b[0];ushort session=b.Length>=3?(ushort)Binary.U16(b,1):(ushort)0;
        switch(type)
        {
            case 5:case 8:
            {
                int from=b[3]&15,count=b[3]>>4;
                if(count==0||from+count>15)throw new InvalidDataException("Invalid transaction begin levels.");
                if(type==8)
                {
                    if(from!=0||transactions.ContainsKey(session))throw new InvalidDataException("Overlapping top-level transaction.");
                    if(transactions.Count>=4096)throw new InvalidDataException("Active transaction budget exceeded.");
                    transactions.Add(session,new Transaction(Binary.U32(b,4),count));
                }
                else
                {
                    var trx=Active(session);if(trx.Level!=from)throw new InvalidDataException("Nested transaction level mismatch.");
                    trx.Level+=count;
                }
                return;
            }
            case 6:case 9:
            {
                var trx=Active(session);int to=b[3];
                if(type==9&&unchecked((int)(Binary.U32(b,4)-trx.Id))<=0)throw new InvalidDataException("Commit timestamp does not follow transaction begin.");
                if(to>=trx.Level||(type==9)!=(to==0))throw new InvalidDataException("Invalid transaction commit level.");
                foreach(var entry in undo.Where(u=>u.Active&&u.Session==session&&u.Transaction==trx.Id))
                {if(to==0)entry.Active=false;else if(entry.Level>to)entry.Level=to;}
                if(to==0){transactions.Remove(session);report.CommittedTransactions++;}
                else trx.Level=to;
                return;
            }
            case 7:
            {
                var trx=Active(session);int count=b[3];
                if(count==0||count>trx.Level)throw new InvalidDataException("Invalid rollback level count.");
                int to=trx.Level-count;
                for(int i=undo.Count-1;i>=0;i--)
                {var entry=undo[i];if(entry.Active&&entry.Session==session&&entry.Transaction==trx.Id&&entry.Level>to)UndoOne(entry);}
                if(to==0){transactions.Remove(session);report.RolledBackTransactions++;}else trx.Level=to;
                return;
            }
            case 0:case 4:case 22:case 61:case 71:return; // padding, trace and rollover metadata only
            case 23:
                if(transactions.Count!=0)throw new InvalidDataException("Shutdown marker with unfinished transactions.");
                return;
            case 29:case 30:case 32:case 33:case 34:case 35:case 36:
                Node(b,session);return;
            default:throw new NotSupportedException("Replay of this ESE operation is not implemented; no recovered state was exposed.");
        }
    }
    private void Node(byte[] b,ushort session)
    {
        if(b[3]!=databaseId)throw new NotSupportedException("Operation references a different database.");
        uint number=Binary.U32(b,4),trxId=Binary.U32(b,24),rce=Binary.U32(b,32);
        int level=b[28],flags=b[31],type=b[0],line=(int)Binary.U16(b,29);
        // Secondary indexes, space trees and concurrent/system operations need their own recovery rules.
        if((flags&~5)!=0||(flags&4)==0)throw new NotSupportedException("Replay currently requires unique leaf records without space/system/concurrent-index flags.");
        bool versioned=(flags&1)!=0;
        if(versioned)
        {
            var trx=Active(session);
            if(level!=trx.Level||trx.Id!=trxId||rce==0)throw new InvalidDataException("Node transaction/version identity mismatch.");
        }
        else if(level!=0)
        {
            var trx=Active(session);
            if(level!=trx.Level||trx.Id!=trxId)throw new InvalidDataException("Nonversioned operation transaction mismatch.");
        }
        if(type==36){UndoInfo(b,session,trxId,rce);return;}
        if(type==35&&versioned)throw new InvalidDataException("Physical deletion cannot be versioned.");
        var page=Page(number);
        if(page.Owner!=Binary.U32(b,40))throw new InvalidDataException("Replay page belongs to a different tree.");
        ulong after=EseChecksums.U64(b,8),before=EseChecksums.U64(b,16);
        if(before==0||after<=before)throw new InvalidDataException("Invalid operation dbtime interval.");
        bool redo=page.Time<after;
        if(redo&&page.Time!=before)throw new InvalidDataException("Page dbtime does not match the log before-image; required history is absent or inconsistent.");
        Undo? entry=null;
        if(versioned)
        {
            var identity=(session,trxId,rce);
            if(versions.ContainsKey(identity))throw new InvalidDataException("Duplicate version identity.");
            if(undo.Count>=100000)throw new InvalidDataException("Replay version budget exceeded.");
            entry=new Undo{Session=session,Transaction=trxId,Id=rce,Page=number,Level=level,Operation=type};
            versions.Add(identity,entry);undo.Add(entry);
        }
        byte[]? loggedKey=null,loggedData=null;
        if(type==29)
        {
            int keyLength=(int)(Binary.U16(b,44)+Binary.U16(b,46));
            loggedKey=EseDatabase.Slice(b,50,keyLength);loggedData=EseDatabase.Slice(b,50+keyLength,(int)Binary.U16(b,48));
        }
        else if(type==30)
        {
            int keyLength=(int)Binary.U16(b,44);loggedKey=EseDatabase.Slice(b,48,keyLength);
            loggedData=EseDatabase.Slice(b,48+keyLength,(int)Binary.U16(b,46));
        }
        if(entry!=null&&loggedKey!=null){entry.Key=loggedKey;Reserve(loggedKey.Length);}
        if(!redo){report.AlreadyAppliedOperations++;return;}
        ReplayPage.Entry? previous=type==29?null:page.At(line).Copy();
        byte[] key=loggedKey??previous!.Key;
        if(previous!=null&&loggedKey!=null&&!previous.Key.SequenceEqual(loggedKey))throw new InvalidDataException("Logged bookmark differs from page line.");
        // ESE unique-record write locks prevent simultaneous writers to the same bookmark.
        if(undo.Any(u=>u!=entry&&u.Active&&u.Page==number&&u.Key!=null&&u.Key.SequenceEqual(key)&&(u.Session!=session||u.Transaction!=trxId)))
            throw new NotSupportedException("Concurrent versions of the same bookmark require version-store conflict handling.");
        if(entry!=null)
        {
            if(entry.Key==null){entry.Key=(byte[])key.Clone();Reserve(key.Length);}
            if(previous!=null){entry.Before=previous.Data;entry.BeforeFlags=previous.Flags;Reserve(previous.Data.Length);}
        }
        switch(type)
        {
            case 29:
                if(line>page.Entries.Count||page.Find(key)>=0)throw new InvalidDataException("Invalid insertion position or duplicate unique key.");
                page.Entries.Insert(line,new ReplayPage.Entry(key,loggedData!,0));break;
            case 30:
                if((previous!.Flags&2)==0||!previous.Data.SequenceEqual(loggedData!))throw new InvalidDataException("FlagInsert requires a matching deleted record.");
                page.At(line).Flags&=~2;break;
            case 32:
                if((previous!.Flags&2)!=0)throw new InvalidDataException("FlagDelete references an already deleted record.");
                page.At(line).Flags|=2;break;
            case 33:
                int length=(int)(Binary.U16(b,44)&0x7fff);
                if((Binary.U16(b,44)&0x8000)!=0||length!=Binary.U16(b,48)||previous!.Data.Length!=Binary.U16(b,46))
                    throw new InvalidDataException("Replace before/after length mismatch.");
                page.At(line).Data=EseDatabase.Slice(b,50,length);break;
            case 34:
                if(previous!.Data.Length!=Binary.U16(b,46))throw new InvalidDataException("ReplaceD before-image length mismatch.");
                page.At(line).Data=RecordDelta.Apply(previous.Data,EseDatabase.Slice(b,50,(int)(Binary.U16(b,44)&0x7fff)),(Binary.U16(b,44)&0x8000)!=0,(int)Binary.U16(b,48),8192);break;
            case 35:page.Entries.RemoveAt(line);break;
        }
        page.Time=after;changed.Add(number);report.RedoneOperations++;
    }
    private void UndoInfo(byte[] b,ushort session,uint trxId,uint rce)
    {
        if(!versions.TryGetValue((session,trxId,rce),out var entry)||!entry.Active)
            throw new NotSupportedException("Deferred undo references an operation outside the supported recovery history.");
        int oper=(int)Binary.U16(b,48),keyLength=(int)Binary.U16(b,44),bookmarkData=(int)Binary.U16(b,46);
        if(bookmarkData!=0||entry.Page!=Binary.U32(b,4)||!((oper==2&&(entry.Operation==33||entry.Operation==34))||(oper==3&&entry.Operation==32)))
            throw new NotSupportedException("Unsupported deferred undo operation/bookmark.");
        var key=EseDatabase.Slice(b,56,keyLength);
        var before=EseDatabase.Slice(b,56+keyLength,(int)Binary.U16(b,54));
        if(entry.Key!=null&&!entry.Key.SequenceEqual(key))throw new InvalidDataException("Deferred undo bookmark mismatch.");
        if(entry.Before!=null&&oper==2&&!entry.Before.SequenceEqual(before))throw new InvalidDataException("Deferred undo before-image mismatch.");
        if(entry.Key==null){entry.Key=key;Reserve(key.Length);}
        if(entry.Before==null&&oper==2){entry.Before=before;Reserve(before.Length);}
    }
    private void UndoOne(Undo entry)
    {
        if(entry.Key==null)throw new NotSupportedException("Uncommitted flushed change has no supported deferred undo bookmark.");
        var page=Page(entry.Page);int line=page.Find(entry.Key);
        if(line<0)throw new NotSupportedException("Undo bookmark moved or is absent; structural undo is required.");
        var node=page.At(line);
        switch(entry.Operation)
        {
            case 29:case 30:node.Flags|=2;break; // ESE insert undo marks the node deleted; cleanup is independent.
            case 32:node.Flags&=~2;break;
            case 33:case 34:
                if(entry.Before==null)throw new NotSupportedException("Uncommitted flushed replacement lacks a before-image.");
                node.Data=(byte[])entry.Before.Clone();break;
            default:throw new NotSupportedException("Undo operation is not supported.");
        }
        entry.Active=false;changed.Add(entry.Page);report.UndoneOperations++;
    }
    private void Reserve(int bytes)
    {undoBytes+=bytes;if(undoBytes>64L*1024*1024)throw new InvalidDataException("Replay undo byte budget exceeded.");}
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Isam.Esent.Interop;

namespace SumData.Readers;

internal sealed class ManagedEsentReader : IEseReader
{
    public IEnumerable<EseRecord> ReadDatabase(string fileName, string[] tables)
    {
        // Preserve the existing Windows attachment behavior. Work on acquired copies.
        using var instance = new Instance("sum_" + Guid.NewGuid().ToString("N"));
        instance.Parameters.LogFileDirectory = Path.GetDirectoryName(fileName);
        instance.Parameters.SystemDirectory = Path.GetDirectoryName(fileName);
        instance.Parameters.TempDirectory = Path.GetDirectoryName(fileName);
        var identity = string.Equals(Path.GetFileName(fileName), "SystemIdentity.mdb", StringComparison.OrdinalIgnoreCase);
        if (!identity) instance.Parameters.EnableIndexCleanup = true;
        instance.Init();
        using var session = new Session(instance);
        Api.JetAttachDatabase(session, fileName, identity ? AttachDatabaseGrbit.ReadOnly : AttachDatabaseGrbit.DeleteCorruptIndexes);
        Api.JetOpenDatabase(session, fileName, null, out var dbid, OpenDatabaseGrbit.ReadOnly);
        yield return new EseRecord { Kind = "database", Version = 1, State = 3 };
        var available = new HashSet<string>(Api.GetTableNames(session, dbid), StringComparer.Ordinal);
        foreach (var name in tables ?? available.ToArray())
        {
            if (!available.Contains(name))
            {
                yield return new EseRecord { Kind = "missing", Table = name };
                continue;
            }
            using var table = new Table(session, dbid, name, OpenTableGrbit.ReadOnly);
            var columns = Api.GetTableColumns(session, dbid, name).ToArray();
            yield return new EseRecord { Kind = "table", Table = name,
                Columns = columns.ToDictionary(c => c.Name, c => c.Coltyp.ToString()) };
            // A null selection is used by DumpTableInfo: schema only.
            if (tables == null) continue;
            Api.MoveBeforeFirst(session, table);
            while (Api.TryMoveNext(session, table))
            {
                var values = new Dictionary<string, string>();
                foreach (var column in columns)
                {
                    var id = Api.GetTableColumnid(session, table, column.Name);
                    byte[] value;
                    if (column.Coltyp == JET_coltyp.Text || column.Coltyp == JET_coltyp.LongText)
                    {
                        var text = Api.RetrieveColumnAsString(session, table, id);
                        value = text == null ? null : Encoding.Unicode.GetBytes(text);
                    }
                    else value = Api.RetrieveColumn(session, table, id);
                    values.Add(column.Name, value == null ? null : Convert.ToBase64String(value));
                }
                yield return new EseRecord { Kind = "row", Table = name, Values = values };
            }
        }
        Api.JetCloseDatabase(session, dbid, CloseDatabaseGrbit.None);
        yield return new EseRecord { Kind = "end" };
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using EseRecovery;

namespace SumData.Readers;

// All decoding and supported recovery run in-process.
internal sealed class PortableEseReader
{
    public IEnumerable<EseRecord> ReadDatabase(string fileName, string[] tables)
    {
        using var database = EseDatabase.Open(fileName);
        yield return new EseRecord { Kind = "database", State = (int)database.Header.State, Recovery = database.Recovery };
        foreach (var name in tables ?? database.Tables.Select(t => t.Name).ToArray())
        {
            var table = database.Tables.SingleOrDefault(t => t.Name == name);
            if (table == null) { yield return new EseRecord { Kind = "missing", Table = name }; continue; }
            yield return new EseRecord { Kind = "table", Table = name,
                Columns = table.Columns.ToDictionary(c => c.Name, c => c.TypeName) };
            foreach (var row in database.ReadRows(table))
            {
                var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var column in table.Columns)
                {
                    var value = row.Values[column.Name];
                    if (value != null && (column.Type == 10 || column.Type == 12))
                        value = EseDatabase.NormalizeText(value, column.CodePage);
                    values.Add(column.Name, value);
                }
                yield return new EseRecord { Kind = "row", Table = name, Values = values };
            }
        }
    }
}

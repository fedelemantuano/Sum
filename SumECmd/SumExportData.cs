using System.Collections.Generic;
using SumData;

namespace SumECmd;

// Keeps export independent of the Windows-only ESE parser.
internal sealed class SumExportData
{
    internal SumExportData(Sum sum) : this(sum.SystemIdentityInfos, sum.RoleInfos, sum.ChainedDbs,
        sum.ProcessedDatabases)
    {
    }

    internal SumExportData(IEnumerable<SystemIdentInfo> identities, IEnumerable<RoleInfo> roles,
        IEnumerable<ChainedDbInfo> chainedDbs, IEnumerable<ChainedDatabase> databases)
    {
        SystemIdentityInfos = identities;
        RoleInfos = roles;
        ChainedDbs = chainedDbs;
        ProcessedDatabases = databases;
    }

    internal IEnumerable<SystemIdentInfo> SystemIdentityInfos { get; }
    internal IEnumerable<RoleInfo> RoleInfos { get; }
    internal IEnumerable<ChainedDbInfo> ChainedDbs { get; }
    internal IEnumerable<ChainedDatabase> ProcessedDatabases { get; }
}

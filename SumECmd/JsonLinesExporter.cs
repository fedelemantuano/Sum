using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ServiceStack.Text;

namespace SumECmd;

internal static class JsonLinesExporter
{
    internal static string Export(SumExportData data, string directory, string fileName,
        DateTimeOffset timestamp, bool withDetail)
    {
        var name = string.IsNullOrEmpty(fileName)
            ? timestamp.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "_SumECmd_Output.json"
            : fileName;
        var outputPath = Path.Combine(directory, name);
        Directory.CreateDirectory(directory);
        WriteAtomically(outputPath, GetRecords(data, withDetail));
        return outputPath;
    }

    internal static void WriteAtomically(string outputPath, IEnumerable<object> records)
    {
        var temporaryPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath)),
            ".SumECmd_" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var config = JsConfig.With(new Config { IncludeNullValues = true, ExcludeTypeInfo = true }))
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true))
                {
                    writer.NewLine = "\n";
                    foreach (var record in records)
                    {
                        writer.WriteLine(JsonSerializer.SerializeToString(record));
                    }
                }
                stream.Flush(flushToDisk: true);
            }

            // Both files are on the same filesystem. Never delete the previous output first.
            if (File.Exists(outputPath))
            {
                File.Replace(temporaryPath, outputPath, null);
            }
            else
            {
                File.Move(temporaryPath, outputPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime()
        .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static IEnumerable<object> GetRecords(SumExportData data, bool withDetail)
    {
        foreach (var identity in data.SystemIdentityInfos)
        {
            yield return new
            {
                RecordType = "SystemIdentInfo", CreationTime = Timestamp(identity.CreationTime),
                identity.OsMajor, identity.OsMinor, identity.OsBuild
            };
        }
        var roles = data.RoleInfos.ToList();
        foreach (var role in roles)
        {
            yield return new { RecordType = "RoleInfos", role.RoleGuid, role.RoleName, role.ProductName };
        }
        foreach (var db in data.ChainedDbs)
        {
            yield return new { RecordType = "ChainedDbInfo", db.Year, db.FileName };
        }
        foreach (var database in data.ProcessedDatabases)
        {
            var sourceFile = Path.GetFileName(database.FileName);
            foreach (var client in database.Clients)
            {
                var roleDescription = roles.SingleOrDefault(t => t.RoleGuid == client.Key)?.RoleName
                                      ?? "(Unknown Role Guid)";
                foreach (var entry in client.Value)
                {
                    yield return new
                    {
                        RecordType = "Clients", RoleGuid = client.Key.ToString(), RoleDescription = roleDescription,
                        entry.AuthenticatedUserName, entry.TotalAccesses, InsertDate = Timestamp(entry.InsertDate),
                        LastAccess = Timestamp(entry.LastAccess), entry.IpAddress, entry.ClientName, entry.TenantId,
                        SourceFile = sourceFile
                    };
                    if (withDetail)
                    {
                        foreach (var day in entry.DayInfo)
                        {
                            yield return new
                            {
                                RecordType = "ClientsDetailed", RoleGuid = client.Key.ToString(), RoleDescription = roleDescription,
                                entry.AuthenticatedUserName, entry.TotalAccesses, InsertDate = Timestamp(entry.InsertDate),
                                LastAccess = Timestamp(entry.LastAccess), entry.IpAddress, entry.ClientName, entry.TenantId,
                                SourceFile = sourceFile, Date = day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                                day.Count, day.DayNumber
                            };
                        }
                    }
                }
            }
            foreach (var dns in database.DnsInfo)
            {
                yield return new
                {
                    RecordType = "DnsInfo", dns.HostName, dns.Address, LastSeen = Timestamp(dns.LastSeen),
                    SourceFile = sourceFile
                };
            }
            foreach (var role in database.RoleAccesses)
            {
                yield return new
                {
                    RecordType = "RoleAccesses", role.RoleGuid,
                    RoleDescription = roles.FirstOrDefault(t => t.RoleGuid == role.RoleGuid)?.RoleName ?? "(Unknown Role Guid)",
                    FirstSeen = Timestamp(role.FirstSeen), LastSeen = Timestamp(role.LastSeen), SourceFile = sourceFile
                };
            }
            foreach (var vm in database.VmInfo)
            {
                yield return new
                {
                    RecordType = "VmInfo", vm.VmGuid, CreationTime = Timestamp(vm.CreationTime),
                    LastSeenActive = Timestamp(vm.LastSeenActive), vm.BiosGuid, vm.SerialNumber, SourceFile = sourceFile
                };
            }
        }
    }
}

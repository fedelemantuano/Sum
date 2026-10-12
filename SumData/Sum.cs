using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;
using SumData.Readers;

namespace SumData
{
    public class Sum
    {
        private readonly IEseReader _reader;
        private static readonly string[] IdentityTables = { "SYSTEM_IDENTITY", "ROLE_IDS", "CHAINED_DATABASES" };
        private static readonly string[] ActivityTables = { "CLIENTS", "DNS", "ROLE_ACCESS", "VIRTUALMACHINES" };

        private static readonly Dictionary<string, string[]> RequiredColumns = new Dictionary<string, string[]>
        {
            ["SYSTEM_IDENTITY"] = new[] { "CreationTime", "OSMajor", "OSMinor", "OSBuildNumber" },
            ["ROLE_IDS"] = new[] { "RoleGuid", "ProductName", "RoleName" },
            ["CHAINED_DATABASES"] = new[] { "Year", "FileName" },
            ["CLIENTS"] = new[] { "RoleGuid", "TenantId", "Address", "AuthenticatedUserName", "ClientName", "InsertDate", "LastAccess", "TotalAccesses" }
                .Concat(Enumerable.Range(1, 366).Select(day => "Day" + day)).ToArray(),
            ["DNS"] = new[] { "LastSeen", "Address", "HostName" },
            ["ROLE_ACCESS"] = new[] { "RoleGuid", "FirstSeen", "LastSeen" },
            ["VIRTUALMACHINES"] = new[] { "VmGuid", "BIOSGuid", "CreationTime", "LastSeenActive", "SerialNumber" }
        };

        public Sum(string directory) : this(directory, EseReaderKind.Auto) { }

        public Sum(string directory, EseReaderKind reader)
            : this(directory, CreateReader(reader)) { }

        internal Sum(string directory, IEseReader reader)
        {
            _reader = reader;
            SystemIdentityInfos = new List<SystemIdentInfo>();
            RoleInfos = new List<RoleInfo>();
            ChainedDbs = new List<ChainedDbInfo>();
            ProcessedDatabases = new List<ChainedDatabase>();
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"Directory '{directory}' not found!");
            directory = Path.GetFullPath(directory);
            var sysIdent = FindDatabase(directory, "SystemIdentity.mdb");
            var current = FindDatabase(directory, "Current.mdb");
            if (sysIdent == null) throw new FileNotFoundException("SystemIdentity.mdb not found.");
            if (current == null) throw new FileNotFoundException("Current.mdb not found.");
            Read(sysIdent, IdentityTables, null, 0);
            // Preserve SumECmd's existing Current.mdb year convention.
            ReadActivity(current, DateTime.Now.Year);
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current };
            foreach (var chain in ChainedDbs)
            {
                // Database metadata must never select a file outside the evidence directory.
                if (string.IsNullOrWhiteSpace(chain.FileName) || chain.FileName == "." || chain.FileName == ".." ||
                    chain.FileName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                    throw new InvalidDataException("CHAINED_DATABASES contains an invalid file name.");
                var path = FindDatabase(directory, chain.FileName);
                if (path == null)
                {
                    HasIncompleteData = true;
                    Log.Warning("Chained database '{FileName}' for year {Year} is missing. Output is incomplete.", chain.FileName, chain.Year);
                    continue;
                }
                if (processed.Add(path)) ReadActivity(path, chain.Year);
            }
        }

        private static string FindDatabase(string directory, string name) => Directory.EnumerateFiles(directory)
            .SingleOrDefault(file => string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase));

        internal static IEseReader CreateReader(EseReaderKind kind)
        {
            var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            if (kind == EseReaderKind.Auto) kind = windows ? EseReaderKind.Esent : EseReaderKind.Dissect;
            if (kind == EseReaderKind.Dissect) return new DissectReader();
            if (kind != EseReaderKind.Esent) throw new ArgumentOutOfRangeException(nameof(kind));
            if (!windows) throw new PlatformNotSupportedException("The esent reader requires Windows. Use --reader dissect or auto.");
            return new ManagedEsentReader();
        }

        public string GetRoleDescFromGuid(Guid roleGuid) => RoleInfos.SingleOrDefault(r => r.RoleGuid == roleGuid)?.RoleName;
        public List<SystemIdentInfo> SystemIdentityInfos { get; }
        public List<RoleInfo> RoleInfos { get; }
        public List<ChainedDbInfo> ChainedDbs { get; }
        public List<ChainedDatabase> ProcessedDatabases { get; }
        public bool HasIncompleteData { get; private set; }

        private void ReadActivity(string path, int year)
        {
            var db = new ChainedDatabase(path);
            Read(path, ActivityTables, db, year);
            ProcessedDatabases.Add(db);
        }

        private void Read(string path, string[] tables, ChainedDatabase db, int year)
        {
            Log.Information("Reading '{FileName}'...", path);
            var counts = tables.ToDictionary(name => name, _ => 0);
            foreach (var record in _reader.ReadDatabase(path, tables))
            {
                if (record.Kind == "database")
                {
                    if (record.State != 3)
                    {
                        HasIncompleteData = true;
                        Log.Warning("'{FileName}': ESE state {State} ({StateName}). Reading stored pages only; transaction logs are not replayed. Output may be incomplete.",
                            path, record.State, record.State == 2 ? "Dirty Shutdown" : "not Clean Shutdown");
                    }
                }
                else if (record.Kind == "table")
                {
                    if (record.Columns == null || !RequiredColumns.TryGetValue(record.Table, out var required) ||
                        required.Any(column => !record.Columns.ContainsKey(column)))
                        throw new InvalidDataException($"Unsupported schema for {record.Table} in '{path}': a required column is missing.");
                }
                else if (record.Kind == "missing")
                {
                    HasIncompleteData = true;
                    Log.Warning("'{FileName}': table {Table} is missing. Output is incomplete.", path, record.Table);
                }
                else if (record.Kind == "row")
                {
                    try { AddRecord(record, db, year); }
                    catch (Exception ex)
                    {
                        throw new InvalidDataException($"Cannot decode {record.Table}, row {counts[record.Table] + 1}, in '{path}'.", ex);
                    }
                    counts[record.Table]++;
                }
            }
            foreach (var count in counts) Log.Information("  {Table}: {Count} records", count.Key, count.Value);
        }

        private void AddRecord(EseRecord row, ChainedDatabase db, int year)
        {
            switch (row.Table)
            {
                case "SYSTEM_IDENTITY":
                    SystemIdentityInfos.Add(new SystemIdentInfo(row.Timestamp("CreationTime"), row.Byte("OSMajor"), row.Byte("OSMinor"), row.Int16("OSBuildNumber")));
                    break;
                case "ROLE_IDS":
                    RoleInfos.Add(new RoleInfo(row.Guid("RoleGuid"), row.Text("ProductName"), row.Text("RoleName")));
                    break;
                case "CHAINED_DATABASES":
                    ChainedDbs.Add(new ChainedDbInfo(row.Int16("Year"), row.Text("FileName")));
                    break;
                case "CLIENTS":
                    var roleGuid = row.Guid("RoleGuid");
                    var client = new ClientEntry(ConvertBytesToIpAddress(row.Bytes("Address")), row.Text("AuthenticatedUserName"),
                        row.Text("ClientName"), row.Timestamp("InsertDate"), row.Timestamp("LastAccess"), row.Guid("TenantId"), row.Int32("TotalAccesses"));
                    for (var day = 1; day <= 366; day++)
                    {
                        var column = "Day" + day;
                        if (row.Bytes(column) != null)
                            client.DayInfo.Add(new DayEntry(day, new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(day - 1), row.Int16(column)));
                    }
                    if (!db.Clients.TryGetValue(roleGuid, out var clients))
                        db.Clients.Add(roleGuid, clients = new List<ClientEntry>());
                    clients.Add(client);
                    break;
                case "DNS":
                    db.DnsInfo.Add(new DnsEntry(row.Timestamp("LastSeen"), row.Text("Address"), row.Text("HostName")));
                    break;
                case "ROLE_ACCESS":
                    db.RoleAccesses.Add(new RoleAccessEntry(row.Timestamp("FirstSeen"), row.Timestamp("LastSeen"), row.Guid("RoleGuid")));
                    break;
                case "VIRTUALMACHINES":
                    db.VmInfo.Add(new VmEntry(row.Guid("BIOSGuid"), row.Guid("VmGuid"), row.Timestamp("CreationTime"), row.Timestamp("LastSeenActive"), row.Text("SerialNumber")));
                    break;
                default: throw new InvalidDataException("Unexpected UAL table.");
            }
        }

        private static string ConvertBytesToIpAddress(byte[] rawBytes)
        {
            if (rawBytes == null) return null;
            if (rawBytes.Length != 4 && rawBytes.Length != 16)
                throw new InvalidDataException("An IP address must contain 4 or 16 bytes.");
            // Preserve the existing CSV/JSON address representation.
            if (rawBytes.Length == 4) return string.Join(".", rawBytes);
            var result = new StringBuilder();
            for (var i = 0; i < rawBytes.Length; i++)
            {
                if (i > 0 && i % 2 == 0) result.Append(':');
                result.Append(rawBytes[i].ToString("X2"));
            }
            return result.ToString();
        }

        public static void DumpTableInfo(string fileName)
        {
            if (!File.Exists(fileName)) throw new FileNotFoundException("Database not found.", fileName);
            foreach (var record in CreateReader(EseReaderKind.Auto).ReadDatabase(fileName, null))
            {
                if (record.Kind != "table") continue;
                Console.WriteLine($"TABLE: {record.Table}");
                foreach (var column in record.Columns) Console.WriteLine("\t{0}: {1}", column.Key, column.Value);
                Console.WriteLine("------------------------------------");
            }
        }
    }

    public class ChainedDatabase
    {
        public ChainedDatabase(string fileName)
        {
            FileName = fileName;

            DnsInfo = new List<DnsEntry>();
            RoleAccesses = new List<RoleAccessEntry>();
            Clients = new Dictionary<Guid, List<ClientEntry>>();
            VmInfo = new List<VmEntry>();
        }

        public string FileName {get;set;}

        public List<DnsEntry> DnsInfo { get; }
        public List<RoleAccessEntry> RoleAccesses { get; }
        public List<VmEntry> VmInfo { get; }

        public Dictionary<Guid,List<ClientEntry>> Clients { get; }

        public override string ToString()
        {
            var sb = new StringBuilder();

            sb.AppendLine($"File Name {FileName}");
            sb.AppendLine();

            if (RoleAccesses.Any())
            {
                sb.AppendLine($"** ROLE ACCESSES **");
                foreach (var ra in RoleAccesses)
                {
                    sb.AppendLine(ra.ToString());
                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("!!!! No Role Accesses present !!!!\r\n");
            }

            if (VmInfo.Any())
            {
                sb.AppendLine($"** VIRTUAL MACHINES **");
                foreach (var vm in VmInfo)
                {
                    sb.AppendLine(vm.ToString());
                }

                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("!!!! No VM Info present !!!!\r\n");
            }

            if (DnsInfo.Any())
            {
                sb.AppendLine($"** DNS INFO **");
                foreach (var dns in DnsInfo)
                {
                    sb.AppendLine(dns.ToString());
                    
                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("!!!! No DNS Info present !!!!\r\n");
            }

            if (Clients.Any())
            {
                sb.AppendLine($"** CLIENTS **");
                foreach (var client in Clients)
                {
                    sb.AppendLine($"Role Guid: {client.Key}");

                    foreach (var clientEntry in client.Value)
                    {
                        sb.AppendLine(clientEntry.ToString());
                    }
                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("!!!! No Client Info present !!!!\r\n");
            }

            return sb.ToString();
        }
    }

    public class ClientEntryDayDetail:ClientEntry
    {
        public ClientEntryDayDetail(ClientEntry clientEntry,DayEntry dayEntry) : base(clientEntry.IpAddress, clientEntry.AuthenticatedUserName, clientEntry.ClientName, clientEntry.InsertDate, clientEntry.LastAccess, clientEntry.TenantId, clientEntry.TotalAccesses)
        {
            DayNumber = dayEntry.DayNumber;
            Date = dayEntry.Date;
            Count = dayEntry.Count;
            SourceFile = clientEntry.SourceFile;
            RoleDescription = clientEntry.RoleDescription;
            RoleGuid = clientEntry.RoleGuid;
        }

        public DateTimeOffset Date {get;}
        public int Count { get; }

        public int DayNumber { get;  }
      
 

    }

    public class ClientEntry
    {
        public ClientEntry(string ipAddress, string authenticatedUserName, string clientName, DateTimeOffset insertDate, DateTimeOffset lastAccess, Guid tenantId, int totalAccesses)
        {
            IpAddress = ipAddress;
            AuthenticatedUserName = authenticatedUserName;
            ClientName = clientName;
            InsertDate = insertDate;
            LastAccess = lastAccess;
            TenantId = tenantId;
            TotalAccesses = totalAccesses;

            DayInfo = new List<DayEntry>();
        }

        public string RoleGuid { get;set; }
        public string RoleDescription { get;set; }

        public string AuthenticatedUserName { get; }

        public int TotalAccesses { get; }

        public DateTimeOffset InsertDate { get; }
        public DateTimeOffset LastAccess { get; }

        public string IpAddress { get; }
     
        public string ClientName { get; }


        public Guid TenantId { get; }

  

        public List<DayEntry> DayInfo { get; }

     

        public string SourceFile { get;set; }

        public override string ToString()
        {
            var sb = new StringBuilder();

            sb.AppendLine($"User Name: {AuthenticatedUserName}");
            sb.AppendLine($"IP Address: {IpAddress}");
            if (ClientName != null)
            {
                sb.AppendLine($"Client Name: {ClientName}");
            }
            sb.AppendLine($"Insert Date: {InsertDate:yyyy-MM-dd HH:mm:ss.fffffff}");
            sb.AppendLine($"Last Access Date: {LastAccess:yyyy-MM-dd HH:mm:ss.fffffff}");
            sb.AppendLine($"Tenant Id {TenantId}");

            if (DayInfo.Any())
            {
                foreach (var dayEntry in DayInfo)
                {
                    sb.AppendLine(dayEntry.ToString());
                }
            }
            else
            {
                sb.AppendLine("No day info present");
            }
            

            return sb.ToString();
        }
    }

    public class DayEntry
    {
        public DayEntry(int dayNumber, DateTimeOffset date, int count)
        {
            DayNumber = dayNumber;
            Date = date;
            Count = count;
        }

        public int DayNumber { get; }
        public DateTimeOffset Date {get;}
        public int Count { get; }

        public override string ToString()
        {
            return $"Date: {Date:yyyy-MM-dd} (Day #: {DayNumber}), Count: {Count:N0}";
        }
    }

    public class VmEntry
    {
        public VmEntry(Guid biosGuid, Guid vmGuid, DateTimeOffset creationTime, DateTimeOffset lastSeenActive, string serialNumber)
        {
            BiosGuid = biosGuid;
            VmGuid = vmGuid;
            CreationTime = creationTime;
            LastSeenActive = lastSeenActive;
            SerialNumber = serialNumber;
        }

        public Guid VmGuid { get; }
        public DateTimeOffset CreationTime { get; }
        public DateTimeOffset LastSeenActive { get; }

        public Guid BiosGuid { get; }
 

        public string SerialNumber { get; }

        public string SourceFile { get;set; }

        public override string ToString()
        {
            return $"VM Guid: {VmGuid} Created: {CreationTime:yyyy-MM-dd HH:mm:ss.fffffff} Last Seen: {LastSeenActive:yyyy-MM-dd HH:mm:ss.fffffff} BIOS Guid: {BiosGuid}";
        }
    }

    

    public class RoleAccessEntry
    {
        public RoleAccessEntry(DateTimeOffset firstSeen, DateTimeOffset lastSeen, Guid roleGuid)
        {
            FirstSeen = firstSeen;
            LastSeen = lastSeen;
            RoleGuid = roleGuid;
        }

        public Guid RoleGuid { get; }

        public string RoleDescription { get;set; }

        public DateTimeOffset FirstSeen { get; }
        public DateTimeOffset LastSeen { get; }

 

        public string SourceFile { get;set; }

       

        public override string ToString()
        {
            return $"Role Guid: {RoleGuid} First Seen: {FirstSeen:yyyy-MM-dd HH:mm:ss.fffffff} Last Seen: {LastSeen:yyyy-MM-dd HH:mm:ss.fffffff}";
        }
    }

    public class DnsEntry
    {
        public DnsEntry(DateTimeOffset lastSeen, string address, string hostName)
        {
            LastSeen = lastSeen;
            Address = address;
            HostName = hostName;
        }
        public string HostName { get; }
      
        public string Address { get; }
      

        public DateTimeOffset LastSeen { get; }

        public string SourceFile { get;set; }

        public override string ToString()
        {
            return $"Host Name: {HostName} --> {Address} Last Seen: {LastSeen:yyyy-MM-dd HH:mm:ss.fffffff}";
        }
    }

    public class ChainedDbInfo
    {
        public ChainedDbInfo(int year, string fileName)
        {
            Year = year;
            FileName = fileName.Trim('\0');
        }

        public int Year { get; internal set; }
        public string FileName { get; internal set; }

        public override string ToString()
        {
            return $"Year: {Year}, File Name: {FileName}";
        }
    }

    public class RoleInfo
    {
        public RoleInfo(Guid roleGuid, string productName, string roleName)
        {
            RoleGuid = roleGuid;
            ProductName = productName.Trim('\0');
            RoleName = roleName.Trim('\0');
        }

        public Guid RoleGuid { get; internal set; }
        public string RoleName { get; internal set; }

        public string ProductName { get; internal set; }
     

        public override string ToString()
        {
            return $"Role Guid: {RoleGuid}, Product Name: {ProductName}, Role Name: {RoleName}";
        }
    }

    public class SystemIdentInfo
    {
        public SystemIdentInfo(DateTimeOffset creationTime, int osMajor, int osMinor, int osBuild)
        {
            CreationTime = creationTime;
            OsMajor = osMajor;
            OsMinor = osMinor;
            OsBuild = osBuild;
        }

        public DateTimeOffset CreationTime { get; }

        public int OsMajor { get; internal set; }
        public int OsMinor { get; internal set; }
        public int OsBuild { get; internal set; }

        public override string ToString()
        {
            return $"{CreationTime.ToUniversalTime():yyyy-MM-dd HH:mm:ss.fffffff}, OS Major: {OsMajor}, OS Minor: {OsMinor}, OS Build: {OsBuild}";
        }
    }
}

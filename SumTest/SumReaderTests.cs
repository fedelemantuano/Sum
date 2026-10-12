using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using SumData;
using SumData.Readers;
using SumECmd;

namespace SumTest;

[TestFixture]
public class SumReaderTests
{
    private string _directory;
    private static readonly Guid Role = Guid.Parse("12345678-9abc-def0-1234-56789abcdef0");
    private static readonly DateTimeOffset Instant = new DateTimeOffset(2024, 12, 31, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "SumReader_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        foreach (var name in new[] { "SystemIdentity.mdb", "Current.mdb", "history.mdb" })
            File.WriteAllBytes(Path.Combine(_directory, name), Array.Empty<byte>());
    }
    [TearDown] public void TearDown() => Directory.Delete(_directory, true);

    private static EseRecord Row(string table, params (string, object)[] fields) => new EseRecord
    {
        Kind = "row", Table = table, Values = fields.ToDictionary(f => f.Item1, f => Encode(f.Item2))
    };
    private static string Encode(object value)
    {
        var bytes = value switch
        {
            null => null,
            string text => Encoding.Unicode.GetBytes(text),
            byte b => new[] { b },
            short number => BitConverter.GetBytes(number),
            int number => BitConverter.GetBytes(number),
            long number => BitConverter.GetBytes(number),
            Guid guid => guid.ToByteArray(),
            byte[] binary => binary,
            _ => throw new ArgumentException()
        };
        return bytes == null ? null : Convert.ToBase64String(bytes);
    }

    private sealed class FixtureReader : IEseReader
    {
        internal bool Dirty, MissingTable, Malformed, MissingColumn;
        internal string ChainName = "history.mdb";
        internal readonly List<string> Reads = new List<string>();
        public IEnumerable<EseRecord> ReadDatabase(string fileName, string[] tables)
        {
            var name = Path.GetFileName(fileName);
            Reads.Add(name);
            yield return new EseRecord { Kind = "database", State = Dirty ? 2 : 3, Version = 1 };
            if (name == "SystemIdentity.mdb")
            {
                yield return Row("SYSTEM_IDENTITY", ("CreationTime", Instant.ToFileTime()), ("OSMajor", (byte)10), ("OSMinor", (byte)0), ("OSBuildNumber", (short)20348));
                yield return Row("ROLE_IDS", ("RoleGuid", Role), ("ProductName", "Windows"), ("RoleName", "Server è 東京"));
                yield return Row("CHAINED_DATABASES", ("Year", (short)2024), ("FileName", ChainName));
            }
            else if (name == "history.mdb")
            {
                if (MissingColumn)
                {
                    yield return new EseRecord { Kind = "table", Table = "CLIENTS", Columns = new Dictionary<string, string>() };
                    yield break;
                }
                var client = Row("CLIENTS", ("RoleGuid", Role), ("TenantId", Guid.Empty), ("Address", new byte[] { 192, 0, 2, 1 }),
                    ("AuthenticatedUserName", "EXAMPLE\\utente"), ("ClientName", null), ("InsertDate", Instant.ToFileTime()),
                    ("LastAccess", Instant.ToFileTime()), ("TotalAccesses", 7));
                for (var i = 1; i <= 366; i++) client.Values["Day" + i] = null;
                client.Values["Day1"] = Encode((short)0);
                client.Values["Day366"] = Encode((short)7);
                if (Malformed) client.Values["LastAccess"] = Encode(new byte[] { 1 });
                yield return client;
                yield return Row("DNS", ("Address", "192.0.2.1"), ("HostName", "host è 東京"), ("LastSeen", Instant.ToFileTime()));
                yield return Row("ROLE_ACCESS", ("RoleGuid", Role), ("FirstSeen", Instant.ToFileTime()), ("LastSeen", Instant.ToFileTime()));
                if (MissingTable) yield return new EseRecord { Kind = "missing", Table = "VIRTUALMACHINES" };
                else yield return Row("VIRTUALMACHINES", ("VmGuid", Role), ("BIOSGuid", Guid.Empty), ("CreationTime", Instant.ToFileTime()),
                    ("LastSeenActive", Instant.ToFileTime()), ("SerialNumber", "serial"));
            }
            yield return new EseRecord { Kind = "end" };
        }
    }

    [Test]
    public void MapsEveryUalTableAndPreservesExportContract()
    {
        var sum = new Sum(_directory, new FixtureReader());
        Assert.That(sum.HasIncompleteData, Is.False);
        Assert.That(sum.ProcessedDatabases.Count, Is.EqualTo(2));
        var db = sum.ProcessedDatabases.Last();
        var client = db.Clients[Role].Single();
        Assert.That(client.LastAccess, Is.EqualTo(Instant));
        Assert.That(client.IpAddress, Is.EqualTo("192.0.2.1"));
        Assert.That(client.ClientName, Is.Null);
        Assert.That(client.DayInfo.Select(d => d.Count), Is.EqualTo(new[] { 0, 7 }));
        Assert.That(client.DayInfo.Last().Date, Is.EqualTo(new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero)));
        var data = new SumExportData(sum);
        var output = Path.Combine(_directory, "output");
        var path = JsonLinesExporter.Export(data, output, "sum.json", Instant, true);
        var records = File.ReadAllLines(path).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Assert.That(records.Select(r => r.GetProperty("RecordType").GetString()).Distinct(), Is.EquivalentTo(new[]
            { "SystemIdentInfo", "RoleInfos", "ChainedDbInfo", "Clients", "ClientsDetailed", "DnsInfo", "RoleAccesses", "VmInfo" }));
        Assert.That(records.Single(r => r.GetProperty("RecordType").GetString() == "Clients").GetProperty("LastAccess").GetString(),
            Is.EqualTo("2024-12-31T12:00:00.1234567Z"));
        Program.ExportCsv(data, output, "yyyy-MM-dd HH:mm:ss", true, Instant);
        Assert.That(Directory.GetFiles(output, "*.csv"), Has.Length.EqualTo(8));
        path = JsonLinesExporter.Export(data, output, "no-days.json", Instant, false);
        Assert.That(File.ReadAllText(path), Does.Not.Contain("ClientsDetailed"));
    }

    [Test]
    public void DirtyDatabaseAndMissingTablesAreReportedAsIncomplete()
    {
        Assert.That(new Sum(_directory, new FixtureReader { Dirty = true }).HasIncompleteData, Is.True);
        Assert.That(new Sum(_directory, new FixtureReader { MissingTable = true }).HasIncompleteData, Is.True);
    }

    [Test]
    public void MissingChainIsReportedAndCurrentIsNotProcessedTwice()
    {
        Assert.That(new Sum(_directory, new FixtureReader { ChainName = "absent.mdb" }).HasIncompleteData, Is.True);
        var reader = new FixtureReader { ChainName = "Current.mdb" };
        Assert.That(new Sum(_directory, reader).ProcessedDatabases.Count, Is.EqualTo(1));
        Assert.That(reader.Reads.Count(n => n == "Current.mdb"), Is.EqualTo(1));
    }

    [TestCase("../outside.mdb")]
    [TestCase("..\\outside.mdb")]
    [TestCase("C:outside.mdb")]
    public void RejectsChainedPathsOutsideEvidenceDirectory(string fileName)
    {
        Assert.That(() => new Sum(_directory, new FixtureReader { ChainName = fileName }), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void RejectsUnsupportedSchemaEvenWhenTableHasNoRows()
    {
        Assert.That(() => new Sum(_directory, new FixtureReader { MissingColumn = true }),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("required column"));
    }

    [Test]
    public void MalformedRequiredValuesFailInsteadOfReturningPartialSuccess()
    {
        Assert.That(() => new Sum(_directory, new FixtureReader { Malformed = true }),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("CLIENTS"));
    }

    [Test]
    public void ParsesExternalSamplesWhenConfigured()
    {
        var samples = Environment.GetEnvironmentVariable("SUM_TEST_SAMPLE_DIR");
        if (string.IsNullOrEmpty(samples)) Assert.Ignore("Set SUM_TEST_SAMPLE_DIR to a private SUM sample directory.");
        var sum = new Sum(samples, EseReaderKind.Dissect);
        Assert.That(sum.SystemIdentityInfos, Is.Not.Empty);
        Assert.That(sum.RoleInfos, Is.Not.Empty);
        var path = JsonLinesExporter.Export(new SumExportData(sum), _directory, "real.json", Instant, true);
        Assert.That(File.ReadAllLines(path).Length, Is.GreaterThanOrEqualTo(sum.SystemIdentityInfos.Count + sum.RoleInfos.Count));
    }
}

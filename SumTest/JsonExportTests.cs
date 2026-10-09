using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CsvHelper;
using NUnit.Framework;
using SumData;
using SumECmd;

namespace SumTest;

[TestFixture]
public class JsonExportTests
{
    private string _directory;
    private static readonly Guid RoleGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Instant = new DateTimeOffset(2026, 10, 9, 11, 45, 0,
        TimeSpan.FromHours(2)).AddTicks(1234567);

    [SetUp]
    public void SetUp() => _directory = Path.Combine(Path.GetTempPath(), "SumECmdTests_" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private static SumExportData Fixture()
    {
        var database = new ChainedDatabase(Path.Combine("evidence", "Current.mdb"));
        var client = new ClientEntry("192.0.2.10", "EXAMPLE\\utente", null, Instant, Instant,
            Guid.Empty, 7);
        client.DayInfo.Add(new DayEntry(282, new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero), 7));
        database.Clients.Add(RoleGuid, new List<ClientEntry> { client });
        database.DnsInfo.Add(new DnsEntry(Instant, "192.0.2.10", "server\"\nè\\東京"));
        database.RoleAccesses.Add(new RoleAccessEntry(Instant, Instant, RoleGuid));
        database.VmInfo.Add(new VmEntry(Guid.Empty, RoleGuid, Instant, Instant, null));
        return new SumExportData(new[] { new SystemIdentInfo(Instant, 10, 0, 20348) },
            new[] { new RoleInfo(RoleGuid, "Windows Server", "File Server") },
            new[] { new ChainedDbInfo(2026, "Current.mdb") }, new[] { database });
    }

    private static JsonElement[] ReadRecords(string file) => File.ReadAllLines(file)
        .Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();

    [Test]
    public void ExportsAllRecordTypesAsFlatJsonLinesWithOriginalFieldNamesAndTypes()
    {
        var file = JsonLinesExporter.Export(Fixture(), _directory, null, Instant, true);
        Assert.That(Path.GetFileName(file), Is.EqualTo("20261009094500_SumECmd_Output.json"));
        var bytes = File.ReadAllBytes(file);
        Assert.That(bytes.Take(3), Is.Not.EqualTo(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.That(bytes.Last(), Is.EqualTo((byte)'\n'));
        var records = ReadRecords(file);
        var expected = new Dictionary<string, string[]>
        {
            ["SystemIdentInfo"] = new[] { "CreationTime", "OsMajor", "OsMinor", "OsBuild" },
            ["RoleInfos"] = new[] { "RoleGuid", "RoleName", "ProductName" },
            ["ChainedDbInfo"] = new[] { "Year", "FileName" },
            ["Clients"] = new[] { "RoleGuid", "RoleDescription", "AuthenticatedUserName", "TotalAccesses", "InsertDate", "LastAccess", "IpAddress", "ClientName", "TenantId", "SourceFile" },
            ["ClientsDetailed"] = new[] { "RoleGuid", "RoleDescription", "AuthenticatedUserName", "TotalAccesses", "InsertDate", "LastAccess", "IpAddress", "ClientName", "TenantId", "SourceFile", "Date", "Count", "DayNumber" },
            ["DnsInfo"] = new[] { "HostName", "Address", "LastSeen", "SourceFile" },
            ["RoleAccesses"] = new[] { "RoleGuid", "RoleDescription", "FirstSeen", "LastSeen", "SourceFile" },
            ["VmInfo"] = new[] { "VmGuid", "CreationTime", "LastSeenActive", "BiosGuid", "SerialNumber", "SourceFile" }
        };
        Assert.That(records.Select(r => r.GetProperty("RecordType").GetString()), Is.EquivalentTo(expected.Keys));
        foreach (var record in records)
        {
            var type = record.GetProperty("RecordType").GetString();
            Assert.That(record.EnumerateObject().Select(p => p.Name),
                Is.EquivalentTo(expected[type].Append("RecordType")), type);
            if (record.TryGetProperty("SourceFile", out var source))
                Assert.That(source.GetString(), Is.EqualTo("Current.mdb"));
        }
        var client = records.Single(r => r.GetProperty("RecordType").GetString() == "Clients");
        Assert.That(client.GetProperty("RoleGuid").GetString(), Is.EqualTo(RoleGuid.ToString()));
        Assert.That(client.GetProperty("RoleDescription").GetString(), Is.EqualTo("File Server"));
        Assert.That(client.GetProperty("AuthenticatedUserName").GetString(), Is.EqualTo("EXAMPLE\\utente"));
        Assert.That(client.GetProperty("TotalAccesses").GetInt32(), Is.EqualTo(7));
        Assert.That(client.GetProperty("ClientName").ValueKind, Is.EqualTo(JsonValueKind.Null));
        var dns = records.Single(r => r.GetProperty("RecordType").GetString() == "DnsInfo");
        Assert.That(dns.GetProperty("HostName").GetString(), Is.EqualTo("server\"\nè\\東京"));
        var daily = records.Single(r => r.GetProperty("RecordType").GetString() == "ClientsDetailed");
        Assert.That(daily.GetProperty("Count").GetInt32(), Is.EqualTo(7));
        Assert.That(daily.GetProperty("DayNumber").GetInt32(), Is.EqualTo(282));
        var identity = records.Single(r => r.GetProperty("RecordType").GetString() == "SystemIdentInfo");
        Assert.That(identity.GetProperty("OsBuild").GetInt32(), Is.EqualTo(20348));
    }

    [TestCase("it-IT")]
    [TestCase("ar-SA")]
    public void AllTimestampsAreUtcIso8601WithFullPrecisionRegardlessOfCulture(string culture)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var file = JsonLinesExporter.Export(Fixture(), _directory, "dates.json", Instant, true);
            foreach (var record in ReadRecords(file))
            foreach (var property in record.EnumerateObject())
            {
                if (new[] { "InsertDate", "LastAccess", "FirstSeen", "LastSeen", "CreationTime", "LastSeenActive" }.Contains(property.Name))
                {
                    Assert.That(property.Value.GetString(), Is.EqualTo("2026-10-09T09:45:00.1234567Z"));
                    Assert.That(property.Value.GetDateTimeOffset(), Is.EqualTo(Instant));
                }
                if (property.Name == "Date")
                    Assert.That(property.Value.GetString(), Is.EqualTo("2026-10-09"));
            }
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    [Test]
    public void DisablingDailyDetailKeepsClientsButRemovesDailyRecordsAndNestedDays()
    {
        var records = ReadRecords(JsonLinesExporter.Export(Fixture(), _directory, "no-days.json", Instant, false));
        Assert.That(records.Length, Is.EqualTo(7));
        Assert.That(records.Count(r => r.GetProperty("RecordType").GetString() == "Clients"), Is.EqualTo(1));
        Assert.That(records.Any(r => r.GetProperty("RecordType").GetString() == "ClientsDetailed"), Is.False);
        Assert.That(records.Any(r => r.TryGetProperty("DayInfo", out _)), Is.False);
    }

    [Test]
    public void PreservesHistoricalFiletimeDatesAndUnknownRolesAcrossDatabases()
    {
        var historical = DateTimeOffset.FromFileTime(0).ToUniversalTime();
        var first = new ChainedDatabase("Current.mdb");
        var second = new ChainedDatabase("archive.mdb");
        foreach (var database in new[] { first, second })
        {
            database.RoleAccesses.Add(new RoleAccessEntry(historical, historical, RoleGuid));
            database.Clients.Add(RoleGuid, new List<ClientEntry>
            {
                new ClientEntry("192.0.2.1", "user", null, historical, historical, Guid.Empty, 0)
            });
        }
        var data = new SumExportData(Array.Empty<SystemIdentInfo>(), Array.Empty<RoleInfo>(),
            Array.Empty<ChainedDbInfo>(), new[] { first, second });
        var records = ReadRecords(JsonLinesExporter.Export(data, _directory, "history.json", Instant, true));
        Assert.That(records.Length, Is.EqualTo(4));
        Assert.That(records.Select(r => r.GetProperty("SourceFile").GetString()),
            Is.EquivalentTo(new[] { "Current.mdb", "Current.mdb", "archive.mdb", "archive.mdb" }));
        foreach (var record in records)
        {
            Assert.That(record.GetProperty("RoleDescription").GetString(), Is.EqualTo("(Unknown Role Guid)"));
            var timestamp = record.TryGetProperty("InsertDate", out var insert) ? insert : record.GetProperty("FirstSeen");
            Assert.That(timestamp.GetString(), Is.EqualTo("1601-01-01T00:00:00.0000000Z"));
        }
    }

    [Test]
    public void ReplacesExistingFileWithoutLeavingTrailingData()
    {
        var file = JsonLinesExporter.Export(Fixture(), _directory, "replace.json", Instant, true);
        var originalLength = new FileInfo(file).Length;
        JsonLinesExporter.Export(Fixture(), _directory, "replace.json", Instant, false);
        Assert.That(new FileInfo(file).Length, Is.LessThan(originalLength));
        Assert.That(ReadRecords(file).Length, Is.EqualTo(7));
        Assert.That(Directory.GetFiles(_directory, ".SumECmd_*.tmp"), Is.Empty);
    }

    private static IEnumerable<object> FailingRecords()
    {
        yield return new { RecordType = "partial" };
        throw new IOException("Simulated export failure");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void FailedWritingDoesNotPublishPartialOutputOrDestroyPreviousFile(bool existing)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "failed.json");
        var original = Encoding.UTF8.GetBytes("previous output\n");
        if (existing) File.WriteAllBytes(path, original);
        Assert.Throws<IOException>(() => JsonLinesExporter.WriteAtomically(path, FailingRecords()));
        if (existing) Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
        else Assert.That(File.Exists(path), Is.False);
        Assert.That(Directory.GetFiles(_directory, ".SumECmd_*.tmp"), Is.Empty);
    }

    [Test]
    public void FailedPublicationPreservesDestinationAndCleansTemporaryFile()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "occupied.json");
        Directory.CreateDirectory(path);
        var sentinel = Path.Combine(path, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        Assert.That(() => JsonLinesExporter.WriteAtomically(path, new object[] { new { RecordType = "example" } }),
            Throws.InstanceOf<IOException>());
        Assert.That(File.ReadAllText(sentinel), Is.EqualTo("keep"));
        Assert.That(Directory.GetFiles(_directory, ".SumECmd_*.tmp"), Is.Empty);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void CombinedOutputKeepsCsvSchemaAndCustomDateFormatWithoutAffectingJson(bool withDetail)
    {
        var data = Fixture();
        var csvDirectory = Path.Combine(_directory, "csv");
        const string dateFormat = "dd/MM/yyyy HH:mm:ss.fffffff";
        Program.ExportCsv(data, csvDirectory, dateFormat, withDetail, Instant.ToUniversalTime());
        var jsonRecords = ReadRecords(JsonLinesExporter.Export(data, _directory, "combined.json", Instant, withDetail));
        var csvFiles = Directory.GetFiles(csvDirectory, "*.csv");
        Assert.That(csvFiles.Length, Is.EqualTo(withDetail ? 8 : 7));
        foreach (var csvFile in csvFiles)
        {
            using var reader = new StreamReader(csvFile);
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            csv.Read();
            csv.ReadHeader();
            csv.Read();
            var type = jsonRecords.Single(r => Path.GetFileName(csvFile).EndsWith("_" + r.GetProperty("RecordType").GetString() + "_Output.csv", StringComparison.Ordinal));
            Assert.That(csv.HeaderRecord, Is.EquivalentTo(type.EnumerateObject().Where(p => p.Name != "RecordType").Select(p => p.Name)));
            foreach (var field in new[] { "InsertDate", "LastAccess", "FirstSeen", "LastSeen", "CreationTime", "LastSeenActive" })
            {
                if (!type.TryGetProperty(field, out var jsonDate)) continue;
                Assert.That(csv.GetField(field), Is.EqualTo(Instant.ToString(dateFormat)));
                Assert.That(jsonDate.GetString(), Is.EqualTo("2026-10-09T09:45:00.1234567Z"));
            }
            if (type.TryGetProperty("Date", out _)) Assert.That(csv.GetField("Date"), Is.EqualTo("2026-10-09"));
        }
    }

    [Test]
    [Category("Elasticsearch")]
    public async Task ElasticsearchIndexesExportedDatesUsingTheDefaultDateMapping()
    {
        var endpoint = Environment.GetEnvironmentVariable("SUM_ELASTICSEARCH_URL");
        if (string.IsNullOrEmpty(endpoint))
            Assert.Ignore("Set SUM_ELASTICSEARCH_URL to a disposable Elasticsearch instance to run ingestion verification.");

        var file = JsonLinesExporter.Export(Fixture(), _directory, "ingest.json", Instant, true);
        var historical = new SumExportData(new[] { new SystemIdentInfo(DateTimeOffset.FromFileTime(0), 0, 0, 0) },
            Array.Empty<RoleInfo>(), Array.Empty<ChainedDbInfo>(), Array.Empty<ChainedDatabase>());
        var historyFile = JsonLinesExporter.Export(historical, _directory, "historical.json", Instant, true);
        var lines = File.ReadAllLines(file).Concat(File.ReadAllLines(historyFile)).ToArray();
        var dateFields = new[] { "InsertDate", "LastAccess", "FirstSeen", "LastSeen", "CreationTime", "LastSeenActive", "Date" };
        var index = "sum-json-test-" + Guid.NewGuid().ToString("N");
        using var client = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        var mapping = System.Text.Json.JsonSerializer.Serialize(new
        {
            mappings = new { properties = dateFields.ToDictionary(field => field, _ => new { type = "date" }) }
        });
        using var created = await client.PutAsync(index, new StringContent(mapping, Encoding.UTF8, "application/json"));
        Assert.That(created.IsSuccessStatusCode, Is.True, await created.Content.ReadAsStringAsync());
        try
        {
            var bulk = new StringBuilder();
            foreach (var line in lines)
            {
                bulk.AppendLine(System.Text.Json.JsonSerializer.Serialize(new { index = new { _index = index } }));
                bulk.AppendLine(line);
            }
            using var response = await client.PostAsync("_bulk?refresh=wait_for",
                new StringContent(bulk.ToString(), Encoding.UTF8, "application/x-ndjson"));
            var body = await response.Content.ReadAsStringAsync();
            Assert.That(response.IsSuccessStatusCode, Is.True, body);
            using var result = JsonDocument.Parse(body);
            Assert.That(result.RootElement.GetProperty("errors").GetBoolean(), Is.False, body);
            Assert.That(result.RootElement.GetProperty("items").GetArrayLength(), Is.EqualTo(lines.Length));

            var query = System.Text.Json.JsonSerializer.Serialize(new
            {
                size = lines.Length,
                docvalue_fields = dateFields.Select(field => new { field, format = "epoch_millis" }).ToArray()
            });
            using var search = await client.PostAsync(index + "/_search", new StringContent(query, Encoding.UTF8, "application/json"));
            var searchBody = await search.Content.ReadAsStringAsync();
            Assert.That(search.IsSuccessStatusCode, Is.True, searchBody);
            using var hits = JsonDocument.Parse(searchBody);
            Assert.That(hits.RootElement.GetProperty("hits").GetProperty("total").GetProperty("value").GetInt32(), Is.EqualTo(lines.Length));
            foreach (var hit in hits.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
            {
                var source = hit.GetProperty("_source");
                if (!hit.TryGetProperty("fields", out var fields)) continue;
                foreach (var field in fields.EnumerateObject())
                {
                    var sourceDate = DateTimeOffset.Parse(source.GetProperty(field.Name).GetString(),
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                    var indexedMillis = long.Parse(field.Value[0].GetString(), CultureInfo.InvariantCulture);
                    Assert.That(indexedMillis, Is.EqualTo(sourceDate.ToUnixTimeMilliseconds()), field.Name);
                }
            }
        }
        finally
        {
            using var deleted = await client.DeleteAsync(index);
            Assert.That(deleted.IsSuccessStatusCode, Is.True, await deleted.Content.ReadAsStringAsync());
        }
    }
}

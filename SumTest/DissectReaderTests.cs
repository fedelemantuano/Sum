using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using SumData;
using SumData.Readers;

namespace SumTest;

[TestFixture]
public class DissectReaderTests
{
    private string _directory;
    private static string Python => Environment.GetEnvironmentVariable("SUM_PYTHON") ?? "python3";

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "Sum transport ' space " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }
    [TearDown] public void TearDown() => Directory.Delete(_directory, true);

    private List<EseRecord> Run(string script, int timeout = 10000) => new DissectReader(Python, timeout, script)
        .ReadDatabase(Path.Combine(_directory, "quoted database.mdb"), new[] { "DNS" }).ToList();

    [Test]
    public void ReadsProtocolWithSpacesInArgumentsAndConcurrentStderr()
    {
        var records = Run(@"import sys,json
assert sys.argv[1].endswith('quoted database.mdb')
assert sys.argv[2] == 'DNS'
sys.stderr.write('x'*100000)
for r in [{'kind':'database','version':1,'state':3},{'kind':'table','table':'DNS'}, {'kind':'row','table':'DNS','values':{'HostName':None}}, {'kind':'end'}]:
 print(json.dumps(r))");
        Assert.That(records.Count, Is.EqualTo(4));
        Assert.That(records[2].Text("HostName"), Is.Null);
    }

    [TestCase("print('{\"kind\":\"database\",\"version\":1,\"state\":3}')")]
    [TestCase("print('{\"kind\":\"database\",\"version\":9}')")]
    [TestCase("print('not JSON')")]
    [TestCase("print('{\"kind\":\"database\",\"version\":1}'); print('{\"kind\":\"end\"}')")]
    public void RejectsTruncatedInvalidAndIncompleteStreams(string script)
    {
        Assert.That(() => Run(script), Throws.Exception);
    }

    [Test]
    public void NonzeroExitInvalidatesAnOtherwiseCompleteStream()
    {
        Assert.That(() => Run(@"import json,sys
for r in [{'kind':'database','version':1,'state':3},{'kind':'table','table':'DNS'},{'kind':'end'}]:
 print(json.dumps(r))
sys.exit(7)"), Throws.TypeOf<InvalidDataException>().With.Message.Contains("exit 7"));
    }

    [Test]
    public void HungHelperIsTerminated()
    {
        Assert.That(() => Run("import time; time.sleep(60)", 250), Throws.TypeOf<TimeoutException>());
    }

    [Test]
    public void MissingInterpreterHasActionableError()
    {
        Assert.That(() => new DissectReader(Path.Combine(_directory, "no-python"))
            .ReadDatabase("input.mdb", new[] { "DNS" }).ToList(),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("SUM_PYTHON"));
    }

    [Test]
    public void AutoReaderAndExplicitWindowsSelectionRespectPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Assert.That(Sum.CreateReader(EseReaderKind.Auto), Is.TypeOf<ManagedEsentReader>());
        else
        {
            Assert.That(Sum.CreateReader(EseReaderKind.Auto), Is.TypeOf<DissectReader>());
            Assert.That(() => Sum.CreateReader(EseReaderKind.Esent), Throws.TypeOf<PlatformNotSupportedException>());
        }
    }

    [Test]
    public void BinaryTransportPreservesFiletimeGuidNullAndUnicode()
    {
        var instant = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        var guid = Guid.Parse("12345678-9abc-def0-1234-56789abcdef0");
        var row = new EseRecord { Table = "CLIENTS", Values = new Dictionary<string, string>
        {
            ["LastAccess"] = Convert.ToBase64String(BitConverter.GetBytes(instant.ToFileTime())),
            ["RoleGuid"] = Convert.ToBase64String(guid.ToByteArray()),
            ["ClientName"] = Convert.ToBase64String(Encoding.Unicode.GetBytes("è 東京\0")),
            ["Address"] = null
        }};
        Assert.That(row.Timestamp("LastAccess"), Is.EqualTo(instant));
        Assert.That(row.Guid("RoleGuid"), Is.EqualTo(guid));
        Assert.That(row.Text("ClientName"), Is.EqualTo("è 東京"));
        Assert.That(row.Bytes("Address"), Is.Null);
        Assert.That(() => row.Int64("Address"), Throws.TypeOf<InvalidDataException>());
        Assert.That(() => row.Bytes("absent"), Throws.TypeOf<InvalidDataException>());
    }
}

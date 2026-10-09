using System.Linq;
using NUnit.Framework;
using SumECmd;

namespace SumTest;

[TestFixture]
public class CommandLineTests
{
    [TestCase("--json output")]
    [TestCase("--csv output")]
    [TestCase("--csv csv-output --json json-output --jsonf custom.json")]
    public void AcceptsJsonCsvAndCombinedOutput(string options)
    {
        ApplicationArguments arguments = null;
        var command = Program.CreateCommand(value => { arguments = value; return 0; });
        var result = command.Parse("-d evidence " + options);
        Assert.That(result.Errors, Is.Empty);
        Assert.That(result.Invoke(), Is.Zero);
        Assert.That(arguments.WithDetail, Is.True);
        Assert.That(arguments.DateTimeFormat, Is.EqualTo("yyyy-MM-dd HH:mm:ss"));
        Assert.That(arguments.Directory, Is.EqualTo("evidence"));
        Assert.That(arguments.JsonDirectory, Is.EqualTo(options.Contains("json-output") ? "json-output" : options.Contains("--json") ? "output" : null));
        Assert.That(arguments.CsvDirectory, Is.EqualTo(options.Contains("csv-output") ? "csv-output" : options.Contains("--csv") ? "output" : null));
        Assert.That(arguments.JsonFileName, Is.EqualTo(options.Contains("--jsonf") ? "custom.json" : null));
    }

    [Test]
    public void PassesDailyDetailAndCsvDateFormatOptionsToTheAction()
    {
        ApplicationArguments arguments = null;
        var command = Program.CreateCommand(value => { arguments = value; return 0; });
        var result = command.Parse("-d evidence --json output --wd false --dt dd/MM/yyyy");
        Assert.That(result.Errors, Is.Empty);
        Assert.That(result.Invoke(), Is.Zero);
        Assert.That(arguments.WithDetail, Is.False);
        Assert.That(arguments.DateTimeFormat, Is.EqualTo("dd/MM/yyyy"));
    }

    [TestCase("-d evidence", "--csv or --json")]
    [TestCase("--json output", "-d")]
    [TestCase("-d evidence --csv output --jsonf custom.json", "--jsonf requires --json")]
    [TestCase("-d evidence --json output --jsonf ../escape.json", "valid file name")]
    [TestCase("-d evidence --json output --jsonf C:\\escape.json", "valid file name")]
    [TestCase("-d evidence --json output --jsonf ..", "valid file name")]
    [TestCase("-d evidence --json output --jsonf bad?.json", "valid file name")]
    [TestCase("-d evidence --json output --jsonf bad.json.", "valid file name")]
    public void RejectsInvalidOutputArgumentsBeforeCallingTheAction(string args, string message)
    {
        var invoked = false;
        var command = Program.CreateCommand(_ => { invoked = true; return 0; });
        var result = command.Parse(args);
        Assert.That(result.Errors.Any(error => error.Message.Contains(message)), Is.True);
        Assert.That(result.Invoke(), Is.Not.Zero);
        Assert.That(invoked, Is.False);
    }

    [Test]
    public void PropagatesExportFailureExitCode()
    {
        var command = Program.CreateCommand(_ => 1);
        Assert.That(command.Parse("-d evidence --json output").Invoke(), Is.EqualTo(1));
    }
}

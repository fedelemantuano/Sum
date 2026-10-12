using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SumData.Readers;

internal sealed class DissectReader : IEseReader
{
    private readonly string _python;
    private readonly int _timeoutMilliseconds;
    private readonly string _script;

    internal DissectReader(string python = null, int timeoutMilliseconds = 1800000, string script = null)
    {
        _python = python ?? Environment.GetEnvironmentVariable("SUM_PYTHON") ?? "python3";
        _timeoutMilliseconds = timeoutMilliseconds;
        if (script != null) _script = script;
        else
        {
            using var stream = typeof(DissectReader).Assembly.GetManifestResourceStream("SumData.ese_reader.py");
            using var reader = new StreamReader(stream ?? throw new InvalidOperationException("Missing embedded ESE helper."));
            _script = reader.ReadToEnd();
        }
    }

    public IEnumerable<EseRecord> ReadDatabase(string fileName, string[] tables)
    {
        var arguments = new[] { "-u", "-", Path.GetFullPath(fileName) }.Concat(tables ?? Array.Empty<string>());
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _python, Arguments = string.Join(" ", arguments.Select(Quote)),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }
        };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("Cannot start Python for Dissect. Install Python >=3.10 and the pinned " +
                "SumData/python/requirements.txt dependencies; set SUM_PYTHON to the interpreter path.", ex);
        }
        var stderr = process.StandardError.ReadToEndAsync();
        var timer = Stopwatch.StartNew();
        try
        {
            process.StandardInput.Write(_script);
            process.StandardInput.Close();
            var serializer = new DataContractJsonSerializer(typeof(EseRecord),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            var header = false;
            var ended = false;
            var seenTables = new HashSet<string>(StringComparer.Ordinal);
            string currentTable = null;
            while (true)
            {
                var pending = process.StandardOutput.ReadLineAsync();
                if (!pending.Wait(Remaining(timer))) throw new TimeoutException("Dissect database read timed out.");
                var line = pending.GetAwaiter().GetResult();
                if (line == null) break;
                if (ended) throw new InvalidDataException("Data after Dissect end marker.");
                using var buffer = new MemoryStream(Encoding.UTF8.GetBytes(line));
                var record = (EseRecord)serializer.ReadObject(buffer);
                if (!header)
                {
                    if (record?.Kind != "database" || record.Version != 1)
                        throw new InvalidDataException("Unsupported Dissect helper protocol.");
                    header = true;
                }
                else if (record.Kind == "table" || record.Kind == "missing")
                {
                    if (record.Table == null || !seenTables.Add(record.Table) ||
                        (tables != null && !tables.Contains(record.Table)))
                        throw new InvalidDataException("Unexpected Dissect table.");
                    currentTable = record.Kind == "table" ? record.Table : null;
                }
                else if (record.Kind == "row")
                {
                    if (currentTable == null || record.Table != currentTable || record.Values == null)
                        throw new InvalidDataException("Unexpected Dissect row.");
                }
                else if (record.Kind == "end") ended = true;
                else throw new InvalidDataException("Unexpected Dissect message.");
                yield return record;
            }
            if (!process.WaitForExit(Remaining(timer))) throw new TimeoutException("Dissect database read timed out.");
            var error = stderr.GetAwaiter().GetResult().Trim();
            if (process.ExitCode != 0)
                throw new InvalidDataException($"Dissect failed (exit {process.ExitCode}): {error}. " +
                    "Check the database and the pinned Python dependencies.");
            if (!header || !ended || (tables != null && !seenTables.SetEquals(tables)))
                throw new InvalidDataException("Incomplete Dissect stream; no output will be published.");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit();
                }
            }
            catch (InvalidOperationException) { /* The helper exited between the check and Kill. */ }
        }
    }

    private int Remaining(Stopwatch timer)
    {
        var remaining = _timeoutMilliseconds - timer.ElapsedMilliseconds;
        if (remaining <= 0) throw new TimeoutException("Dissect database read timed out.");
        return (int)remaining;
    }

    // ProcessStartInfo.Arguments quoting for both .NET Framework and modern .NET.
    // No command shell is involved, including for paths containing spaces/quotes.
    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }
}

# JSON output verification

Verified locally on 2026-10-09 using .NET SDK 9.0.318 on macOS arm64.

## Build and tests

- Release build of `SumECmd/SumECmd.csproj`: passed for both `net472` and `net9.0`, with zero errors.
- Release tests of `SumTest/SumTest.csproj`: 25 passed, zero failed, with the optional Elasticsearch integration test enabled.
- The two original database-sample tests remain explicit and were not executed: they require Windows ESE and database files at external hard-coded paths.
- The existing ManagedEsent dependency produces warning `NU1701` because its .NET Framework assets are used by `SumData` targeting .NET Standard 2.0. This dependency was not changed.
- The compiled .NET 9 executable's `--help` displays the new JSON options and examples successfully.

Tests cover all eight schemas, flat fields, explicit null strings, numeric values, source filenames, role descriptions and unknown roles, multiple databases, Unicode and escaped line breaks, UTF-8 without a BOM, UTC conversion and seven fractional digits, calendar dates, historical FILETIME values, both `--wd` modes, argument validation, and failure exit-code propagation. Combined CSV/JSON tests verify the CSV column sets, filenames, custom date formatting, and daily-detail selection against the JSON output.

File tests verify successful replacement with shorter content, absence of stale trailing records, preservation of a previous destination after failed writing, absence of partial output when no prior file exists, and temporary-file cleanup after writing or publication fails.

## Elasticsearch ingestion

The integration test ran against a disposable local Docker instance of Elasticsearch **8.19.0**, exposed only on the loopback interface. It created a unique test index with all seven temporal field names explicitly mapped as `date`, without custom date formats.

It ingested actual exporter output containing all eight record types plus a system-identity record dated `1601-01-01T00:00:00.0000000Z`. All nine documents were accepted without Bulk API item errors. The test retrieved indexed date values as `epoch_millis` and compared them with the source timestamps, verifying UTC conversion, fractional-second acceptance, calendar-date acceptance, and the historical date. Source JSON retains seven fractional digits; the `date` mapping indexes milliseconds.

The test deleted its index afterward. The temporary Elasticsearch container was stopped and removed after verification.

## Limits and reproduction

The Windows ESE parser was not executed against real MDB files on this Mac. The .NET Framework target was compiled, but not executed here. The local Elasticsearch test does not establish acceptance by an existing deployment with a different mapping or ingest pipeline.

```text
dotnet build SumECmd/SumECmd.csproj --configuration Release
dotnet test SumTest/SumTest.csproj --configuration Release
```

Set `SUM_ELASTICSEARCH_URL` to a disposable Elasticsearch endpoint to enable the ingestion test; otherwise that test is skipped. It creates and deletes a uniquely named test index. The usual CI test job installs .NET 9 and runs the remaining automated tests without requiring Elasticsearch.

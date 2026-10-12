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

## Cross-platform ESE reader validation — 2026-10-12

The repository now selects Dissect on non-Windows platforms and ManagedEsent on
Windows. `--reader dissect` can also be selected explicitly. The Python helper is
embedded in SumData and passes ESE values to the shared C# UAL mapping; the existing
CSV/JSON exporters are reused.

Validation performed locally in Linux ARM64 Docker on the Mac:

- 46 C# tests passed; the optional Elasticsearch and external-sample tests were
  skipped in the default suite. The original explicit Windows sample tests remain
  excluded. Five Python transport tests passed.
- Release builds succeeded for `net9.0` and `net472`. The existing ManagedEsent
  `NU1701` warning remains. Docker builds exclude `.git`, so MinVer also reports
  its default-version warning in that environment.
- The separate opt-in C# test passed with `SUM_TEST_SAMPLE_DIR` pointing to the two
  private databases mounted read-only. No private database is checked in.
- The actual published `SumECmd` executable, running in the final runtime image,
  read the same databases and produced 17 JSON records and corresponding CSV rows:
  3 `SystemIdentInfo`, 13 `RoleInfos`, and 1 `ChainedDbInfo`.
- Exported identity timestamps matched the raw FILETIME values exactly, including
  all seven fractional digits. Exported role GUID values matched their source
  bytes. Synthetic tests additionally exercise non-microsecond FILETIME values.
- `Current.mdb` had zero readable activity records and state 2 (Dirty Shutdown).
  A referenced historical database was also missing. Both conditions were logged;
  the application exported the readable records and returned exit code 2.
- Fatal cases (invalid ESE database, missing Python executable, explicit `esent`
  selection on Linux) returned exit code 1 and preserved the existing JSON file.
- Both original databases retained their SHA-256 hashes after all runs. Containers
  performing evidence reads had networking disabled and read-only evidence mounts.

This establishes real ESE reading and metadata export on the tested platform. It
is not evidence of complete activity extraction: the supplied `Current.mdb` has no
readable activity rows. Populated CLIENTS/DNS/ROLE_ACCESS/VIRTUALMACHINES samples and
comparison against a live Windows ManagedEsent run are still required for full
cross-reader acceptance. Windows execution has not been validated locally; the CI
configuration now includes Windows and Ubuntu, but no remote CI run was triggered.

The existing `Current.mdb` daily-date year convention is preserved, as documented
in the README. Dissect does not replay transaction logs or perform recovery.

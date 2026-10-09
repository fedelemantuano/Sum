# JSON output contract

The design choices below were agreed during the requested grill-with-docs session and confirmed for implementation and verification.

## Command line

- `-d` remains the source directory.
- `--json` selects the destination directory for a single JSON Lines file.
- `--jsonf` overrides its default name, `<UTC yyyyMMddHHmmss>_SumECmd_Output.json`. It requires `--json` and represents a filename within that destination directory.
- At least one of `--csv` and `--json` is required. Both may be supplied to generate both formats from the same processing run.
- `--wd` controls the additional `ClientsDetailed` records in both formats, with the existing default of `true`. `Clients` records remain present regardless of this option.
- `--dt` retains its current CSV behavior and has no effect on JSON timestamps or daily dates.

Example:

```text
SumECmd.exe -d "C:\Evidence\Sum" --json "C:\Results" --jsonf sum.json
SumECmd.exe -d "C:\Evidence\Sum" --csv "C:\Results\csv" --json "C:\Results\json" --wd false --dt "dd/MM/yyyy HH:mm:ss"
```

## Records

Each line is one complete JSON object. `RecordType` and the existing CSV fields appear at the first level. Field names retain their existing capitalization; numbers remain JSON numbers, GUIDs remain strings, and absent string values are explicit `null`.

| RecordType | Fields besides RecordType |
| --- | --- |
| SystemIdentInfo | CreationTime, OsMajor, OsMinor, OsBuild |
| RoleInfos | RoleGuid, RoleName, ProductName |
| ChainedDbInfo | Year, FileName |
| Clients | RoleGuid, RoleDescription, AuthenticatedUserName, TotalAccesses, InsertDate, LastAccess, IpAddress, ClientName, TenantId, SourceFile |
| ClientsDetailed | All Clients fields plus Date, Count, DayNumber |
| DnsInfo | HostName, Address, LastSeen, SourceFile |
| RoleAccesses | RoleGuid, RoleDescription, FirstSeen, LastSeen, SourceFile |
| VmInfo | VmGuid, CreationTime, LastSeenActive, BiosGuid, SerialNumber, SourceFile |

`SourceFile` retains the existing source database basename for detail records. Daily counts are not duplicated in a nested `DayInfo` collection. Record ordering and object-property ordering carry no meaning for consumers.

An illustrative pair of lines:

```json
{"RecordType":"SystemIdentInfo","CreationTime":"2026-10-09T09:45:00.1234567Z","OsMajor":10,"OsMinor":0,"OsBuild":20348}
{"RecordType":"DnsInfo","HostName":"server.example","Address":"192.0.2.10","LastSeen":"2026-10-09T09:45:00.1234567Z","SourceFile":"Current.mdb"}
```

## Temporal representation

Instant timestamps are normalized to UTC and serialized as `yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'`, for example `2026-10-09T09:45:00.1234567Z`. This preserves the source's 100 ns precision in the JSON without depending on the machine's locale, timezone, or `--dt`.

`ClientsDetailed.Date` is an ISO calendar date, `yyyy-MM-dd`, because it identifies an aggregation day rather than an observed access time. Existing timestamp values are preserved; the exporter does not redefine sentinel dates or parser behavior for missing database values.

Elasticsearch's default [`date` format](https://www.elastic.co/docs/reference/elasticsearch/mapping-reference/date) accepts ISO calendar dates and uses `strict_date_optional_time||epoch_millis`. Its upstream [fractional-seconds test](https://github.com/elastic/elasticsearch/blob/main/server/src/test/java/org/elasticsearch/common/time/DateFormattersTests.java#L677-L679) explicitly accepts `2019-05-06T14:52:37.1234567Z`. The `date` type indexes millisecond resolution while the JSON retains seven fractional digits. This is source-level compatibility evidence, not a live ingestion test against a particular Elasticsearch deployment.

## File completion

Output uses UTF-8 without a BOM, one compact object per line, with each record terminated by a newline. Destination directories are created when needed. No headers, log messages, outer JSON array, or Elasticsearch Bulk API action lines are written into the JSON file.

The exporter writes to a temporary file in the destination directory, completes and closes the writer, and then publishes the finished file. A pre-existing destination is replaced at that point; data is never appended. Failed writing leaves the previous destination intact and reports an error. Temporary output is cleaned up on a handled failure.

## Verification before delivery

- Verify all eight record types against representative synthetic data, including source filenames, role enrichment, explicit nulls, numeric values, and daily counts.
- Verify one independently parseable JSON object per line, Unicode and escaped characters, and UTF-8 without a BOM.
- Verify UTC conversion, seven fractional digits, daily date representation, historical timestamps, and independence from custom `--dt` values.
- Verify `--wd true` and `--wd false`, JSON-only, CSV-only, combined output, and invalid argument combinations.
- Verify successful replacement, shorter replacement without stale trailing content, and preservation of a previous destination after failed writing.
- Verify CSV fields, filenames, date formatting, and daily-detail behavior remain compatible with existing exports.
- Build both target frameworks where supported. Validate the Windows ESE processing path separately from export tests; the parser requires Windows native libraries and cannot be fully exercised on this macOS workspace.

See [verification results](json-output-validation.md) for the completed checks and their limits.

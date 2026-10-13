# SumECmd

Process Microsoft User Access Logs! Thanks to KPMG for the initial write up, found [here](https://advisory.kpmg.us/blog/2021/digital-forensics-incident-response.html).

## Command Line Interface

    SumECmd version 2026.5.0.0

    Author: Eric Zimmerman (saericzimmerman@gmail.com)
    https://github.com/EricZimmerman/Sum

        d               Directory to process, looking for SystemIdentity.mdb, Current.mdb, etc. Required.
        csv             Directory to save CSV formatted results to. This or --json is required
        json            Directory to save JSON Lines results to. This or --csv is required
        jsonf           File name to save JSON Lines results to. Overrides the default name; requires --json

        wd              Include day level details in CSV and JSON. Default is TRUE

        dt              The custom CSV date/time format. Default is: yyyy-MM-dd HH:mm:ss. JSON always uses ISO 8601

        debug           Show debug information during processing
        trace           Show trace information during processing
    Examples: SumECmd.exe -d "C:\Temp\sum" --csv "C:\Temp\"
              SumECmd.exe -d "C:\Temp\sum" --json "C:\Temp\json" --jsonf sum.json
              SumECmd.exe -d "C:\Temp\sum" --csv "C:\Temp\csv" --json "C:\Temp\json" --wd false

          Short options (single letter) are prefixed with a single dash. Long commands are prefixed with two dashes

## Included C# reader and Docker build

The source library is included in `EseRecovery/` and built by project reference. It replaces the native Windows ESE dependency while preserving SumECmd's original single reading path: clean databases are opened directly, and dirty databases are recovered automatically when the required adjacent logs are available. Each activity database is processed independently. A database that cannot be recovered is skipped, while records from `SystemIdentity.mdb` and successfully completed activity databases remain exportable. There is no backend or recovery-mode option in the public CLI.

All platforms use the same C# implementation; Python, Dissect, Windows ESENT and external build contexts are not required. JSON Lines and CSV retain their existing export contract. See the [library scope](EseRecovery/README.md); the included implementation does not yet cover every operation supported by Windows ESE.

### Comparison with Windows `esent.dll`

Windows `esent.dll` is the complete ESE database engine. The included `EseRecovery` project is a read-only forensic reader with bounded transaction recovery for the supported SUM and SRUM evidence formats. It intentionally fails instead of exposing a recovered state when a required operation is outside its implemented scope. See Microsoft's [ESE overview](https://learn.microsoft.com/en-us/windows/win32/extensible-storage-engine/extensible-storage-engine) and [ESE files and recovery](https://learn.microsoft.com/en-us/windows/win32/extensible-storage-engine/extensible-storage-engine-files).

| Capability | Windows `esent.dll` | Included `EseRecovery` |
| --- | --- | --- |
| Read clean ESE databases | Full engine support | Supported formats only |
| Read SUM and SRUM tables | Yes | Yes |
| Validate page and log checksums | Yes | Yes |
| Validate log signatures and generations | Yes | Yes |
| Replay inserts, replacements and deletes | Full engine support | Supported unique leaf operations only |
| Commit, rollback and nested transactions | Full engine support | Bounded implementation |
| Version store and concurrent updates | Full engine support | Simple validated cases only |
| B-tree page split, merge and root operations | Yes | No |
| Page allocation and space-tree recovery | Yes | No |
| Secondary and non-unique indexes | Yes | No |
| Multiple databases in one log stream | Yes | No |
| Torn-write and shadow-log recovery | Yes | No |
| Create or modify databases | Yes | No; read-only in-memory overlay |
| Backup, restore and maintenance APIs | Yes | No |
| General database API | Windows `Jet*` API | Purpose-built forensic API |
| Platforms | Windows | Linux, macOS and Windows |

Matching output on a clean database demonstrates record-reading compatibility, not complete engine equivalence. A dirty database that reports zero redo and zero undo operations validates log-chain attachment but does not exercise page-changing recovery. Full parity would require Windows-reference fixtures covering committed unflushed changes, rollback, structural B-tree changes, secondary indexes and concurrent versions. Missing required logs cannot be reconstructed by either implementation.

Run these commands from the repository root. The single Dockerfile publishes .NET 9 self-contained files and uses Ubuntu 24.04 for the Linux runtime:

```sh
docker buildx build --platform linux/amd64 --load -t sumecmd:local .
docker buildx build --platform linux/amd64 --target export \
  --output type=local,dest=artifacts/bin/linux-x64 .
docker buildx build --target export --build-arg DOTNET_RID=osx-arm64 \
  --output type=local,dest=artifacts/bin/osx-arm64 .
```

The exported directories include the executable and its .NET runtime: keep the entire directory when copying it to another host. Linux x64 requires a compatible Linux system (the container uses Ubuntu 24.04); `osx-arm64` is for Apple Silicon. After exporting the macOS build from Linux, apply an ad-hoc signature to its apphost on the Mac before running it:

```sh
codesign --force --sign - artifacts/bin/osx-arm64/SumECmd
```

Run the macOS ARM64 binary directly with the original CLI plus the JSON options:

```sh
mkdir -p artifacts/manual
./artifacts/bin/osx-arm64/SumECmd \
  -d /Users/fmantuano/Downloads \
  --json "$PWD/artifacts/manual" --jsonf sum-mac.jsonl
```

Equivalent Linux container run, with read-only evidence:

```sh
docker run --rm --platform linux/amd64 --network none --read-only \
  --user "$(id -u):$(id -g)" \
  --mount type=bind,src=/Users/fmantuano/Downloads/SystemIdentity.mdb,dst=/evidence/SystemIdentity.mdb,readonly \
  --mount type=bind,src=/Users/fmantuano/Downloads/Current.mdb,dst=/evidence/Current.mdb,readonly \
  --mount type=bind,src="$PWD/artifacts/manual",dst=/out \
  sumecmd:local -d /evidence --json /out --jsonf sum-linux.jsonl
```

On a Linux x64 host, copy `artifacts/bin/linux-x64/` and run `./SumECmd -d /path/to/evidence --json /path/to/output --jsonf sum.jsonl`. Keep matching ESE logs in the evidence directory alongside dirty databases, as on Windows. Exit code **0** means the export is complete, **2** means validated records were exported but one or more activity databases were skipped, and **1** means processing or publication failed without a usable new output.

When `SystemIdentity.mdb` opens successfully but `Current.mdb` is dirty and its matching transaction logs are absent, SumECmd reports the missing logs, continues with independently readable chained databases, exports the validated records, and exits **2**. It does not export unrecovered pages from `Current.mdb`. If no usable database can be processed, it exits **1** without publishing a new output. The original MDB files remain unchanged. No automated tests are run as part of the Docker build.

The original daily-date convention is preserved: `Current.mdb` uses the analysis machine's current year; historical databases use their recorded year.

## JSON Lines output

`--json` writes one UTF-8 file containing one JSON object per line, without a BOM or an outer array. The default filename is `<UTC yyyyMMddHHmmss>_SumECmd_Output.json`; `--jsonf` supplies a filename within the output directory. CSV and JSON can be requested separately or together.

Each object has `RecordType` alongside its fields. The types are `SystemIdentInfo`, `RoleInfos`, `ChainedDbInfo`, `Clients`, `ClientsDetailed`, `DnsInfo`, `RoleAccesses`, and `VmInfo`. Field names match the corresponding CSV columns, numbers remain numbers, and absent string values are `null`. Daily counts appear only in `ClientsDetailed` when `--wd` is true; `DayInfo` is not duplicated inside client objects.

```json
{"RecordType":"DnsInfo","HostName":"server.example","Address":"192.0.2.10","LastSeen":"2026-10-09T09:45:00.1234567Z","SourceFile":"Current.mdb"}
```

Timestamps always use UTC ISO 8601 with seven fractional digits and `Z`, independently of `--dt` and the machine's locale. `ClientsDetailed.Date` uses `yyyy-MM-dd` to identify the aggregation day. Existing CSV date formatting is preserved.

Use Elasticsearch `date` mappings for timestamp and daily-date fields. Its default format accepts these representations, although indexed dates have millisecond resolution. Seven fractional digits remain in the original JSON. The export is JSON Lines; an Elasticsearch Bulk API client must add its own action lines.

An existing JSON file is replaced only after the new file is completely written and closed. Output is never appended; a failed write preserves the previous file and reports an error.

The [output contract](docs/json-output-contract.md) lists all fields and validation requirements.

## Export tests

```text
dotnet test SumTest/SumTest.csproj
dotnet build SumECmd/SumECmd.csproj --configuration Release
```

Existing export and command-line tests are retained. The original database sample tests remain explicit because they require external databases. The current local acceptance uses the supplied real databases; no automated tests are run as part of the Docker build.

For the optional ingestion test, set `SUM_ELASTICSEARCH_URL` to a disposable Elasticsearch instance before running the tests. The test creates a uniquely named index, checks all exported timestamp fields using the default `date` mapping, and deletes the index afterward.

## Documentation

[Windows User Access Logs (UAL)](https://svch0st.medium.com/windows-user-access-logs-ual-9580f1100635)

## Repairing the SUM Database

Until a proper guide can be made with test data, follow the guide for repairing the SRUM database found [here](https://github.com/EricZimmerman/Srum). Both the SUM and SRUM databases are ESE so the process will be identical. Additionally, a walkthrough on how to repair the SUM database was included in [this](https://svch0st.medium.com/windows-user-access-logs-ual-9580f1100635) blog post.

# Download Eric Zimmerman's Tools

All of Eric Zimmerman's tools can be downloaded [here](https://ericzimmerman.github.io/#!index.md). 

# Special Thanks

Open Source Development funding and support provided by the following contributors: [SANS Institute](http://sans.org/) and [SANS DFIR](http://dfir.sans.org/).

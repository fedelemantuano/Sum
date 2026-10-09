# JSON timestamps independent of the CSV date format

JSON output will use one JSON Lines file containing one JSON object per line, with `RecordType` identifying each of the eight existing CSV record types. It will cover all existing CSV exports, with `--wd` controlling daily client detail records in both formats and retaining its default of true.

JSON timestamps use UTC ISO 8601 with seven fractional digits independently of `--dt`, so ingestion into Elasticsearch does not depend on a user-selected display format; `--dt` retains its existing CSV behavior to preserve integrations. Daily aggregation dates use an ISO calendar date. Elasticsearch's `strict_date_optional_time` parser accepts this timestamp representation, although `date` indexes with millisecond resolution.

Each record has its existing CSV field names at the same level as `RecordType`; daily client counts appear only as `ClientsDetailed` records, without a nested `DayInfo` collection. An existing JSON output file is replaced only after the new output has been written completely, preserving the previous file if writing fails. These choices establish an ingestion contract that should not change silently after consumers adopt it.

The complete contract and verification plan are recorded in [JSON output contract](../json-output-contract.md). The user confirmed the complete contract and authorized implementation and verification.

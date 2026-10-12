"""Read-only ESE transport for SumData. No UAL date/IP/day interpretation here."""
import base64
import json
import sys

from dissect.database.ese.ese import ESE


def emit(**record):
    print(json.dumps(record, ensure_ascii=True, separators=(",", ":")), flush=True)


def encode_value(record, column):
    # Resolve long/compressed text and binary through Dissect. Raw fixed values
    # retain FILETIME's 100 ns ticks, GUID byte order and integer signedness.
    if column.is_text:
        value = record.get(column.name)
        if value is not None:
            if not isinstance(value, str):
                raise ValueError("Multi-valued text is not supported")
            value = value.encode("utf-16-le")
    elif column.ctype.name in ("Binary", "Long Binary"):
        value = record.get(column.name)
    else:
        value = record.get(column.name, raw=True)
    if value is None:
        return None
    if not isinstance(value, (bytes, bytearray, memoryview)):
        raise ValueError("Unsupported ESE value")
    return base64.b64encode(value).decode("ascii")


def read_database(path, names):
    with open(path, "rb") as stream:
        db = ESE(stream)
        emit(kind="database", version=1, state=db.header.dbstate)
        available = {table.name: table for table in db.tables()}
        schema_only = not names
        for name in names or available:
            if name not in available:
                emit(kind="missing", table=name)
                continue
            table = available[name]
            emit(kind="table", table=name,
                 columns={c.name: c.ctype.name for c in table.columns})
            if schema_only:
                continue
            for record in table.records():
                emit(kind="row", table=name,
                     values={c.name: encode_value(record, c) for c in table.columns})
        emit(kind="end")


if __name__ == "__main__":
    try:
        read_database(sys.argv[1], sys.argv[2:])
    except Exception as exc:
        # Keep evidence values out of diagnostics. A nonzero status invalidates
        # the entire stream, even if some rows were already delivered.
        print("ESE reader failed: " + type(exc).__name__, file=sys.stderr)
        sys.exit(1)

# Included ESE library

Core C# source copied from the local `EseRecovery` 0.3.0-alpha.1 implementation on 2026-10-13. Sum references this project directly; it does not require the standalone repository, its CLI, Python, a package feed, or Windows `esent.dll`.

Clean database reading and bounded single-database transaction recovery are supported. Recovery validates required log generations, signatures, checksums and the attachment boundary, then applies supported leaf inserts, replacements and deletions with commit/rollback handling in a private page overlay. Acquired files are not modified.

This is not a complete ESE engine. Structural split/merge/root operations, page allocation, secondary/nonunique indexes, logged physical undo/CLR, multiple-database histories, unsupported log formats or missing required logs fail explicitly. Nonempty replay currently supports log format 8.4000.5.16 with 4096-byte segments. Database pages are restricted to 4/8 KiB. A newer empty current log can terminate a supported chain; it does not enable replay of that newer format.

`EseDatabase.Open` requires successful supported recovery for dirty databases. `EseDatabase.InspectStoredPages` exists as a library diagnostic API, but SumECmd does not expose it or export unrecovered records. When recovery fails, SumECmd skips that activity database and may export records from `SystemIdentity.mdb` and other independently completed databases, marking the result incomplete with exit code `2`.

The binary layouts were interpreted using Microsoft's published [ESE structures](https://github.com/microsoft/Extensible-Storage-Engine/blob/main/dev/ese/src/inc/logapi.hxx), [recovery code](https://github.com/microsoft/Extensible-Storage-Engine/blob/main/dev/ese/src/ese/_log/logredo.cxx), and [record deltas](https://github.com/microsoft/Extensible-Storage-Engine/blob/main/dev/ese/src/ese/logdiff.cxx). The native engine and Windows libraries are not included.

# Portable ESE Evidence Processing

Terms used when Sum describes ESE consistency, recovery and forensic extraction.

## Language

**ESE engine**:
Microsoft's complete Windows storage engine exposed through `esent.dll` and the `Jet*` API.
_Avoid_: Portable engine

**Portable ESE reader**:
The cross-platform, read-only reader that supports a declared subset of ESE formats and recovery operations.
_Avoid_: ESE engine, `esent.dll` replacement

**Complete recovery**:
A recovered database view for which all required log history was validated and every required operation was handled.
_Avoid_: Successful attachment

**Salvage record**:
A structurally valid record decoded from stored database pages without a complete transaction-recovery guarantee.
_Avoid_: Recovered record

**Salvage export**:
An export containing salvage records and explicit consistency provenance.
_Avoid_: Partial recovery

**Partial export**:
An export containing usable records while one or more independent sources or extraction stages could not be completed.
_Avoid_: Partial recovery

**Missing recovery evidence**:
The absence of required matching logs or other history needed to establish a transactionally consistent database view.
_Avoid_: Unsupported operation

# SumECmd

SumECmd extracts Windows User Access Logging (UAL) records from SUM databases for forensic analysis.

## Language

**Client entry**:
A UAL record describing a client's access to a server role, including its identity, first recorded insertion, last access, and total access count.

**Client day detail**:
A daily access count associated with a client entry. Its date identifies an aggregation day, rather than the time of an individual access.
_Avoid_: Individual access event.

**Role access entry**:
A UAL record identifying a server role and the first and last times it was seen.

**Source database**:
The SUM database from which an exported record was extracted.

**Recovered state**:
The database state exposed after the supported transaction history has been applied and unfinished transactions rolled back. Missing required evidence or unsupported operations prevent declaring that state recovered.

# MongoCustody — Backup and Recovery Plan

## Azure Cosmos DB for MongoDB

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Scope and Assumptions](#2-scope-and-assumptions)
3. [Regulatory Context](#3-regulatory-context)
4. [Backup Architecture](#4-backup-architecture)
5. [Backup Schedule and Retention Policy](#5-backup-schedule-and-retention-policy)
6. [How Backups Are Generated](#6-how-backups-are-generated)
7. [Storage Structure and Security](#7-storage-structure-and-security)
8. [Data Consistency Verification](#8-data-consistency-verification)
9. [Restore Procedure](#9-restore-procedure)
10. [Failure Handling and Recovery](#10-failure-handling-and-recovery)
11. [Backup Metadata and Audit Trail](#11-backup-metadata-and-audit-trail)
12. [HIPAA Compliance Analysis](#12-hipaa-compliance-analysis)
13. [Known Gaps and Accepted Risks](#13-known-gaps-and-accepted-risks)
14. [Open Items Requiring Compliance Sign-off](#14-open-items-requiring-compliance-sign-off)

---

## 1. Executive Summary

This document describes the backup and recovery architecture implemented by MongoCustody for a MongoDB database hosted on Azure Cosmos DB for MongoDB. The target system stores Protected Health Information (PHI) and is subject to HIPAA's Security Rule, including requirements for data backup, disaster recovery, and audit controls. This document is intended as a compliance template; adopting organisations should substitute their own system name, database configuration, and Azure resource details before submitting for compliance review.

The design uses a **hybrid incremental backup model** comprising three backup types:

- **Quarterly full backups** — a complete export of every collection, taken on the first day of each calendar quarter
- **Daily incremental backups** — a capture of all documents changed since the previous backup, taken nightly during off-hours
- **Weekly checkpoint backups** — a deduplicated merge of the preceding six daily incrementals, produced once per week to bound restore complexity

This approach provides a **Recovery Point Objective (RPO) of 24 hours** (the maximum data loss window between nightly incrementals), a bounded restore chain of at most 19 backup operations regardless of the restore target date, and tamper-evident backup artifacts stored in immutable Azure Blob Storage.

All three backup types — full, incremental, and checkpoint — use the same structural convention: **one or more sequentially numbered chunk files per collection**. Large collections are split into chunks of configurable size (default 10 GB uncompressed) at document boundaries — no document is ever split across chunks. This approach allows any individual collection or subset of collections to be restored independently, enables parallel generation and verification across collections, bounds memory and upload size per operation, and supports resume on failure at the chunk level.

All three backup types use **BSON format**, gzip-compressed — preserving all MongoDB native types faithfully and eliminating any risk of type fidelity loss during restore.

All backup metadata is stored exclusively in **blob-resident manifest files** written alongside the backup chunks in Azure Blob Storage. There is no separate metadata database — the backup storage account is the single, self-contained source of truth. This eliminates the circular dependency that would arise if metadata were stored in the same database being backed up, and ensures full restore capability even if the source database is completely lost.

The solution is designed to be HIPAA-compliant by construction: all backups are encrypted at rest, accessible only via managed identity (no shared keys), and produce durable audit records for every operation.

---

## 2. Scope and Assumptions

### 2.1 In Scope

- All collections within the target Azure Cosmos DB for MongoDB database
- Backup generation, storage, verification, and restore procedures
- Backup metadata storage via blob-resident manifest files
- Audit trail storage in blob manifests and Azure Storage diagnostic logs
- Failure handling and administrative notification procedures
- HIPAA compliance posture of the backup system itself

### 2.2 Out of Scope

- Application-level disaster recovery (failover, DNS rerouting, multi-region replication)
- Backup of application configuration, secrets, or infrastructure state
- Interpretation or processing of application data beyond what is required for backup and restore

### 2.3 Confirmed Assumptions

The following assumptions were confirmed during design and must remain true for this plan to be valid. Any change to these assumptions requires a plan revision.

| # | Assumption | Impact if violated |
|---|---|---|
| A1 | All collections implement soft delete. Documents are never hard-deleted; instead an `isDeleted` flag is set and `updatedAt` is stamped. | Deletions would become invisible to the incremental backup, creating silent data loss in restores. |
| A2 | All user-initiated write activity occurs during business hours. Off-hours writes are exclusively from controlled system processes under administrator supervision. | The off-hours backup window is treated as low-contention. High off-hours write volume would increase incremental backup size and duration. |
| A3 | Every document in every collection carries a `_ts` field maintained by the Cosmos DB engine (server-set Unix timestamp, updated on every write). | The incremental query predicate depends on `_ts`. Application-managed timestamps are not used for this purpose because they are subject to clock skew and application bugs. |
| A4 | Every mutable document carries an application-managed `updatedAt` field. Every immutable document carries a `createdAt` field. | These fields are used for human-readable audit and verification queries. They are supplementary to `_ts`, not a substitute. |
| A5 | Write volume does not reach extremes (thousands of documents per second) that would cause multiple documents to share an identical `_ts` value within the 5-minute buffer window. | Confirmed as not applicable to current or near-future scale. |
| A6 | The Azure subscription and Cosmos DB account used for backup storage are covered by a signed HIPAA Business Associate Agreement (BAA) with Microsoft. | Without a BAA, storing PHI in any Azure service is a HIPAA violation regardless of technical controls. |

---

## 3. Regulatory Context

### 3.1 Applicable HIPAA Rules

This backup plan is designed to satisfy the following provisions of the HIPAA Security Rule (45 CFR Part 164):

| Rule reference | Requirement | How addressed |
|---|---|---|
| §164.308(a)(7)(ii)(A) | Data backup plan — establish and implement procedures to create and maintain retrievable exact copies of ePHI | Quarterly full + daily incremental + weekly checkpoint backups; all retrievable from Azure Blob Storage |
| §164.308(a)(7)(ii)(B) | Disaster recovery plan — establish procedures to restore loss of data | Documented restore procedure with step-by-step runbook; quarterly restore dry-run |
| §164.308(a)(7)(ii)(D) | Testing and revision — implement procedures for periodic testing of contingency plans | Quarterly automated restore dry-run to ephemeral account with documented results |
| §164.312(a)(1) | Access control — implement technical policies restricting access to ePHI to authorized users | Managed identity access to all backup storage; no shared keys; RBAC-scoped roles |
| §164.312(b) | Audit controls — implement hardware, software, and procedural mechanisms to record and examine access to ePHI | Structured audit record for every backup operation, every restore request, every verification result |
| §164.312(c)(1) | Integrity — implement policies to protect ePHI from improper alteration or destruction | SHA-256 hash of every backup artifact stored at write time; WORM immutability on Blob Storage prevents post-write modification |
| §164.312(e)(1) | Transmission security — implement technical security measures guarding against unauthorized access to ePHI transmitted over a network | TLS enforced for all Cosmos DB and Azure Blob Storage communication; no plaintext transmission |
| §164.530(j) | Documentation retention — retain documentation for six years | Blob lifecycle policy retains backup artifacts and metadata for six years minimum |

### 3.2 RPO and RTO Targets

| Metric | Target | Basis |
|---|---|---|
| Recovery Point Objective (RPO) | 24 hours | Maximum gap between nightly incremental backups |
| Recovery Time Objective (RTO) | To be defined by compliance team | Depends on dataset size at time of restore; see Section 9 for procedure |

> **Action required:** The adopting organisation's compliance team must formally document and approve the 24-hour RPO as acceptable under their HIPAA disaster recovery plan. If 24 hours is not acceptable, the backup frequency must increase (e.g., hourly incrementals), which is technically supported by this architecture.

---

## 4. Backup Architecture

### 4.1 Three-Tier Hybrid Model

The backup system uses three complementary backup types, each serving a distinct purpose:

```
Q1 Full Backup (Jan 1, 00:00)
│   Complete export of all collections
│   anchor_ts = T₀
│
├── Incremental  Jan 2, 01:00   Changes since T₀        anchor_ts = T₁
├── Incremental  Jan 3, 01:00   Changes since T₁        anchor_ts = T₂
├── Incremental  Jan 4, 01:00   Changes since T₂        anchor_ts = T₃
├── Incremental  Jan 5, 01:00   Changes since T₃        anchor_ts = T₄
├── Incremental  Jan 6, 01:00   Changes since T₄        anchor_ts = T₅
├── Incremental  Jan 7, 01:00   Changes since T₅        anchor_ts = T₆
│
├── Checkpoint   Jan 8, 02:00   Merge of Jan 2–7        anchor_ts = T₆ (inherited)
│
├── Incremental  Jan 9, 01:00   Changes since T₆        anchor_ts = T₇
│   ... (pattern repeats for 13 weeks)
│
Q2 Full Backup (Apr 1, 00:00)   New chain begins
```

**Full backup:** A complete cursor scan of every collection, written to BSON format, compressed, and hashed. Large collections are split into sequentially numbered chunk files at document boundaries. Produces one or more chunk blobs per collection.

**Daily incremental:** A query of all documents with `_ts` greater than the previous backup's anchor timestamp (with a 5-minute conservative overlap buffer). Produces one or more BSON chunk blobs per collection that had changes. Collections with no changes produce no blobs.

**Weekly checkpoint:** A merge-and-deduplicate operation over the six preceding daily incremental blobs. Produces one or more BSON chunk blobs per collection touched during the week. No new database query is performed — the checkpoint is derived entirely from existing incremental blobs.

### 4.2 Why This Model

**Why not pure differential (all changes since the full)?**
A pure differential grows linearly with time. By day 89 of a quarter, the differential contains 89 days of changes for every document ever modified — including documents modified 88 days ago that have not changed since. Storage cost is proportional to time, not change volume.

**Why not pure incremental (changes since previous incremental only)?**
Pure incremental keeps each file small but creates a long restore chain. To restore to day 87, you need the full backup plus 86 incremental files applied in strict order. A single missing or corrupt file breaks the entire chain from that point forward.

**Why the hybrid?**
Weekly checkpoints bound the restore chain to a maximum of 19 files (1 full + 12 checkpoints + 6 daily incrementals) regardless of the restore target date. Storage scales with actual change volume. Restore complexity is predictable.

### 4.3 The Anchor Timestamp Chain

Every backup record carries two timestamps that form a linked chain:

- `queryFromTs` — the lower bound of the `_ts` query used to generate this backup
- `anchorTs` — the server timestamp captured at the end of this backup's execution window, which becomes `queryFromTs` for the next incremental

This chain is the core correctness guarantee of the system. Every document modified between any two consecutive anchor timestamps will appear in exactly one incremental backup. The 5-minute overlap buffer ensures that documents written near the boundary are captured by both the preceding and following backup — duplicates handled safely by upsert on restore.

### 4.4 Differential Change Detection via `_ts`

Rather than using a change stream (which requires a persistent cursor and is vulnerable to oplog window expiry), the incremental backup uses a direct query against the Cosmos DB system field `_ts`:

```
For mutable collections:
  find({ _ts: { $gt: (queryFromTs - 300) } })
  // 300 seconds = 5-minute conservative overlap buffer

For immutable collections (insert-only):
  find({ _ts: { $gt: (queryFromTs - 300) } })
  // _ts on first insert equals creation time — same query works correctly
```

`_ts` is set by the Cosmos DB engine on every write. It is not controlled by application code and cannot be set incorrectly by a bug or a bulk import. This makes it a reliable and tamper-resistant change detection field.

Soft deletes are captured correctly because a soft delete is a document update: the engine updates `_ts`, the document appears in the next incremental with `isDeleted: true`. No special handling is required.

---

## 5. Backup Schedule and Retention Policy

### 5.1 Schedule

| Backup type | Trigger | Time window | Frequency |
|---|---|---|---|
| Full | First day of each calendar quarter (Jan 1, Apr 1, Jul 1, Oct 1) | 00:00–completion (typically 2–4 hours) | Quarterly (4×/year) |
| Incremental | Every day except the full backup day and checkpoint day | 01:00–completion (typically 15–60 min) | Daily |
| Checkpoint | Every 8th day of each week within a quarter | 02:00–completion (typically 30 min) | Weekly |
| Verification | Immediately after each backup completes | Automated | Per backup |
| Restore dry-run | Day after each quarterly full backup | Off-hours | Quarterly |

> The checkpoint is scheduled at 02:00 (one hour after incremental start) to allow the preceding daily incremental to complete. The scheduler additionally checks that all six source incrementals are in `status: Completed` before beginning the merge. If any source incremental is still running or failed, the checkpoint is deferred with an alert.

### 5.2 Retention Policy

| Backup type | Hot tier retention | Archive tier | Total retention |
|---|---|---|---|
| Full backups | 1 year (4 quarterly fulls) | Moved to Archive after 1 year | 6 years minimum |
| Daily incrementals | 1 year | Moved to Archive after 1 year | 6 years minimum |
| Weekly checkpoints | 1 year | Moved to Archive after 1 year | 6 years minimum |
| Backup manifest files | Retained in Blob Storage alongside chunks | Moved to Archive after 1 year | 6 years minimum |

Retention is enforced by Azure Blob Storage lifecycle management policies configured at the storage account level. These policies run automatically and cannot be overridden by application code.

Blobs are protected by WORM (Write Once Read Many) immutability policies at the container level. Once written, a backup blob cannot be modified or deleted before its retention period expires — including by storage account administrators. This provides tamper-evidence required for HIPAA audit defensibility.

---

## 6. How Backups Are Generated

### 6.1 Full Backup Procedure

Executed as an Azure Durable Function (fan-out/fan-in orchestration):

1. **Capture anchor** — record the current server timestamp as `anchorTs`. This happens before any collection scan begins to ensure no document written during the export falls into a gap.
2. **Discover collections** — enumerate all collections in the database.
3. **Fan out** — export each collection in parallel as a separate activity:
   - Open a sorted cursor (`_id` ascending) over all documents
   - Stream documents to a gzip-compressed BSON blob in Azure Blob Storage
   - Compute SHA-256 hash of the uncompressed content in a single streaming pass
   - Record document count, hash, blob path, and export timestamp
4. **Write manifest** — on successful completion of all collection exports, write `manifest.json` to the backup folder as the final step. The manifest is the completion certificate: its presence means the backup is valid; its absence means the backup did not complete. The manifest is never written for a failed backup.
5. **Verify** — run Level 1 (hash) and Level 2 (count) consistency verification automatically.
6. **Trigger dry-run** — schedule a restore dry-run for the following off-hours window.

**Streaming requirement:** All read and write operations are fully streaming. No collection export buffers documents in memory. Memory consumption per active collection export is O(1) with respect to collection size — bounded by the serializer and compression buffer, not the number of documents. The SHA-256 hash is computed in a single streaming pass concurrent with the write; the blob is never read back for hashing.

**Chunked export:** Each collection is exported as one or more sequentially numbered chunk files. A new chunk is started after each complete document write when the accumulated uncompressed byte count meets or exceeds the configured threshold (default: 10 GB). The threshold is checked after a complete document is written — never mid-document — guaranteeing that every document is fully present in exactly one chunk. If a single document exceeds the threshold, it is written to its own chunk without splitting. Chunk files are named `{db}.{collection}.part-{NNN}.bson.gz` where `NNN` is zero-padded to three digits.

**Large blob constraints:** Each chunk is uploaded as an Azure block blob using a configurable block size (default: 100 MB per block). This reduces the number of block API calls for large chunks and avoids the 5 GB single-upload limit. Network timeout is configured explicitly to accommodate slow or throttled links. A chunk blob is only visible in storage after all blocks are committed — a failed mid-upload leaves no partial blob, ensuring hash verification is never applied to incomplete data.

**Format:** BSON (Binary JSON), gzip-compressed. BSON preserves all MongoDB native types faithfully (ObjectId, Date, Decimal128, Binary). This is the authoritative format for all backup types and is directly consumable by `mongorestore` (full backup chunks) and the restore replay service (incremental and checkpoint chunks).

### 6.2 Daily Incremental Backup Procedure

Executed as an Azure Durable Function (fan-out/fan-in orchestration), using the same parallel structure as the full backup:

1. **Load previous anchor** — read `anchorTs` from the most recently completed backup manifest in blob storage (either the quarterly full or the previous daily incremental).
2. **Verify chain integrity** — confirm the previous backup has a valid manifest. If the most recent backup has no manifest (indicating failure), block this run and alert the administrator. Do not run an incremental on a broken chain.
3. **Discover collections** — enumerate all collections in the database.
4. **Fan out** — query and export each collection in parallel as a separate activity:
   - Query `_ts > (previousAnchorTs - 300)` against that collection. The 5-minute buffer ensures no document is missed due to clock drift or boundary conditions.
   - Stream the changed documents for that collection into one or more sequentially numbered gzip-compressed BSON chunk blobs in Azure Blob Storage, using the same chunked export logic as the full backup.
   - Compute SHA-256 hash of the content in a single streaming pass.
   - Record document count, hash, blob path, and export timestamp for that collection.
5. **Record anchor** — capture the server timestamp at the end of the fan-out as this backup's `anchorTs`.
6. **Write manifest** — write `manifest.json` to the incremental folder as the final step. Run Level 1 and Level 3 verification per collection. If verification fails, do not write the manifest — the absence of the manifest marks the backup as failed.

**Format:** BSON (Binary JSON), gzip-compressed. Identical format to full backups. Same chunking rules apply — chunk boundaries fall on document boundaries, never mid-document. Use `bsondump` to inspect individual chunk contents.

**Note:** If a collection has zero changes since the previous anchor, no blob is written for that collection and no entry is added to `collections[]` for that run. This keeps storage lean — absent collections in an incremental record are correctly interpreted as "no changes" during restore, not as an error.

### 6.3 Weekly Checkpoint Procedure

Executed as an Azure Durable Function (fan-out/fan-in orchestration), after verifying all six source incrementals are complete:

1. **Verify prerequisites** — check that all six source incremental folders have a valid `manifest.json`. If any source incremental manifest is missing or reports failure, defer by one hour and recheck. If still not resolved after two hours, alert the administrator and do not proceed (see Section 10.2).
2. **Discover collections** — union of all collection names that appear across the six source incremental manifests. Collections with no changes during the week are skipped.
3. **Fan out** — merge each collection independently in parallel as a separate activity:
   - Read only the chunks for that collection from each of the six daily incremental folders (some days may have no chunks for a given collection — that is correct and expected).
   - Stream the chunks in chronological order (Day 2 first, Day 7 last). Maintain an in-memory map of `{ _id → latest document seen }`. When the same `_id` appears in multiple files, the later file always wins (higher `_ts` = more recent state).
   - Write the deduplicated result for that collection to one or more sequentially numbered gzip-compressed BSON chunk blobs, using the same chunking rules as full and incremental backups.
   - Compute SHA-256 hash and record document count, hash, and blob path.
4. **Write manifest** — write `manifest.json` to the checkpoint folder as the final step, recording the six source incremental folder paths in `sourceIncrementalFolders`. Run Level 1 verification per collection. If verification fails, do not write the manifest.

**Format:** BSON (Binary JSON), gzip-compressed. Identical format to full and incremental backups. Same document-boundary chunking rules apply.

**Important:** The checkpoint does not replace the daily incremental blobs. Both are retained. Daily incrementals are required for point-in-time restore to any specific day within the week. The checkpoint is an optimization for week-boundary restores only.

---

## 7. Storage Structure and Security

### 7.1 Blob Storage Layout

All three backup types use the same structural convention: **one or more chunk files per collection plus one manifest file per backup operation**. The manifest is the single source of truth for all backup metadata — no external database is required. Blob Storage is the complete, self-contained backup system.

```
[storage-account]/
└── backups/                              ← single container, WORM-enabled
    ├── full/
    │   └── {year}/{quarter}/{backupId}/
    │       ├── {db}.{collection-a}.part-001.bson.gz   ← chunk 1 of N, BSON format
    │       ├── {db}.{collection-a}.part-002.bson.gz   ← chunk 2 of N (large collections)
    │       ├── {db}.{collection-b}.part-001.bson.gz
    │       ├── {db}.{collection}.part-{NNN}.bson.gz
    │       └── manifest.json                          ← completion certificate; absent = failed
    │
    ├── incremental/
    │   └── {year}/{quarter}/{fullBackupId}/
    │       └── {yyyy-MM-ddTHHmmZ}/                   ← ISO 8601 UTC (e.g. 2025-01-03T0100Z)
    │           ├── {db}.{collection-a}.part-001.bson.gz
    │           ├── {db}.{collection-b}.part-001.bson.gz
    │           ├── {db}.{collection}.part-{NNN}.bson.gz
    │           └── manifest.json
    │           (collections with no changes on this day have no chunk files;
    │            manifest is still written recording zero-change collections)
    │
    └── checkpoint/
        └── {year}/{quarter}/{fullBackupId}/
            └── week-{nn}T{HHmmZ}/                    ← e.g. week-02T0200Z
                ├── {db}.{collection-a}.part-001.bson.gz
                ├── {db}.{collection-b}.part-001.bson.gz
                ├── {db}.{collection}.part-{NNN}.bson.gz
                └── manifest.json
```

**Folder naming convention:**
- Full backup folders use a UUID `{backupId}` — unique and collision-free by construction.
- Incremental folders use ISO 8601 UTC timestamp truncated to the minute (`yyyy-MM-ddTHHmmZ`). Lexicographic sort order equals chronological order — no parsing needed to sequence restores.
- Checkpoint folders combine week number and scheduled start time (`week-{nn}T{HHmmZ}`).
- All timestamps are UTC. The explicit `Z` suffix eliminates timezone ambiguity.

**Manifest as completion certificate:** A folder containing chunk files but no `manifest.json` represents a failed or incomplete backup. The manifest is written as the absolute last step — only after all chunks are uploaded and verified. Its presence is an unconditional guarantee that the backup completed successfully. A periodic cleanup process scans for folders older than 24 hours that have no manifest and removes their orphaned chunk files (see Section 10.5).

**Backup history browsing:** The folder structure alone is sufficient to list available backups — no blob content needs to be read. Listing `incremental/{year}/{quarter}/{fullBackupId}/` returns all incremental timestamps for a given quarter. The admin selects a target timestamp; the library then reads only the manifests required to resolve the restore chain for that point in time.

Chunks must always be applied in ascending `part-NNN` order during restore.

### 7.2 Security Controls

| Control | Implementation |
|---|---|
| Authentication | Managed identity only. No shared access keys, no SAS tokens, no connection strings in application code or configuration. |
| Authorization | Azure RBAC. Backup writer role: `Storage Blob Data Contributor` scoped to the backup container. Restore operator role: `Storage Blob Data Reader` plus time-limited elevation via Azure PIM. |
| Encryption at rest | Azure Storage Service Encryption (AES-256) enabled by default. Customer-managed keys (CMK) via Azure Key Vault are available and should be evaluated by the compliance team. |
| Encryption in transit | TLS 1.2 minimum enforced on the storage account. All Cosmos DB connections use TLS. |
| Immutability | WORM time-based retention policy on the backup container. Blobs cannot be modified or deleted before their retention period expires. Policy is locked — it cannot be shortened after activation. |
| Network access | Storage account network rules restrict access to the Azure Virtual Network hosting the backup services. Public blob access is disabled. |
| Audit logging | Azure Storage diagnostic logs capture all read and write operations on backup blobs, forwarded to Azure Monitor / Log Analytics. |

### 7.3 Storage Account Configuration (Key Settings)

```
allowBlobPublicAccess:   false
allowSharedKeyAccess:    false          ← managed identity only
minimumTlsVersion:       TLS1_2
redundancy:              GRS            ← geo-redundant, required for HIPAA DR
immutableStorageWithVersioning: enabled
```

---

## 8. Data Consistency Verification

Verification runs automatically after every backup and on a scheduled weekly basis. Four verification levels exist, each catching a different class of failure.

### Level 1 — Structural Integrity
*Runs after every backup (full, incremental, checkpoint)*

Verifies the backup artifact has not been corrupted in transit or storage.

- Download the backup blob
- Compute SHA-256 of the content
- Compare to the hash stored in `manifest.json` at write time
- **PASS:** hashes match
- **FAIL:** blob is corrupted or tampered → rename or remove the manifest to invalidate the backup, alert administrator immediately, do not use for restore

### Level 2 — Count Consistency
*Runs after every full backup*

Verifies the full export captured the same number of documents as the live database at the time of export.

- Read `documentCount` per collection from `manifest.json`
- Query the live database reconstructing the "as of anchor timestamp" count using soft delete fields:
  ```
  count where (isDeleted does not exist) OR (deletedAt > anchorTimestamp)
  ```
- **PASS:** counts match within tolerance (±0.01% for large collections, exact for collections under 10,000 documents)
- **FAIL:** count delta exceeds tolerance → investigate before marking backup valid

The small tolerance exists because documents written during the export window (after anchor capture but before their collection's cursor reaches them) may appear in both the full backup and the first incremental. This is by design and handled correctly by upsert on restore.

### Level 3 — Incremental Completeness
*Runs after every daily incremental*

Verifies the incremental captured all documents changed since the previous anchor.

- Count distinct document IDs in the incremental blob
- Query the live database for documents modified after the previous anchor:
  ```
  count where _ts > queryFromTs
  ```
- **PASS:** incremental document count ≥ live modified count (incremental may be slightly higher due to the 5-minute buffer)
- **FAIL:** incremental count < live count → gap detected, escalate immediately

### Level 4 — Restore Dry-Run
*Runs quarterly, the day after each full backup*

The only definitive proof that a backup is restorable. A backup that has never been restored is an untested assumption.

1. Provision a new, empty, ephemeral Cosmos DB for MongoDB account
2. Restore the most recent full backup to this account (full procedure, Section 9)
3. Compare per-collection document counts: restored vs manifest
4. Spot-check 100 randomly selected document IDs: restored document must match production document field-by-field
5. Record results in the dry-run log (append to a `dry-run-results.json` file in the full backup folder)
6. **PASS:** all counts match, all spot-checks pass → backup confirmed restorable
7. **FAIL:** any mismatch → alert administrator, investigate before next production restore
8. Decommission the ephemeral account within 48 hours of dry-run completion

> **HIPAA note:** The ephemeral restore account contains a complete copy of production PHI during the dry-run window. It must reside in the same Azure subscription and region (covered by the existing BAA), access must be restricted to named individuals performing the dry-run, and decommissioning within 48 hours is mandatory.

---

## 9. Restore Procedure

### 9.1 Restore Chain by Scenario

The restore chain is always deterministic given a target date. Because all three backup types use one file per collection, the table below describes the number of **backup operations** (each comprising one file per collection) required for any restore scenario within a quarter. Any individual collection or subset of collections can be restored by applying only the relevant collection files from each step in the chain.

| Target | Backup operations required | Maximum chain depth |
|---|---|---|
| Full backup date (quarter start) | Full[Q] | 1 |
| Specific day within week 1 (days 2–7) | Full[Q] + Inc[Day2] + … + Inc[DayN] | 7 |
| End of week 1 (day 7) | Full[Q] + CKP[Week1] | 2 |
| Specific day within week N | Full[Q] + CKP[Week1] + … + CKP[Week(N-1)] + Inc[FirstDayWeekN] + … + Inc[DayN] | 19 |
| End of quarter (week 13 day 7) | Full[Q] + CKP[Week1] + … + CKP[Week13] | 14 |

**Maximum chain depth: 19 backup operations** (1 full + 12 checkpoints + 6 daily incrementals). This is the worst case regardless of how far into the quarter the restore target falls. Within each operation, collections can be restored in parallel.

### 9.2 Pre-Restore Checklist

Before any restore operation begins, the following steps are mandatory:

```
□ Identify the restore target date and time
□ Identify and document the reason for restore
□ Obtain sign-off from two named authorized individuals
□ Record the restore request in the audit log BEFORE starting
  (who requested, who approved, target date, reason, correlation ID)
□ Verify backup blob hashes (Level 1 check) for all manifests and chunks in the chain
□ Confirm all folders in the restore chain have a manifest.json (no failed backups in chain)
□ Provision an empty restore Cosmos DB account
  (NEVER restore directly into the production account)
□ Confirm the restore account is in the same Azure subscription and region
□ Restrict access to the restore account to named restore operators only
```

### 9.3 Step A — Full Backup Restore

For each collection in the full backup manifest:

1. **Create the collection** in the restore account with the original throughput settings recorded in the `BackupRecord`
2. **Download all chunk blobs for the collection**, in ascending `part-NNN` order:
   ```
   backups/full/{year}/{quarter}/{backupId}/{db}.{collection}.part-001.bson.gz
   backups/full/{year}/{quarter}/{backupId}/{db}.{collection}.part-002.bson.gz
   ... (repeat for all chunks recorded in BackupRecord.collections[n].chunks[])
   ```
3. **Verify hash per chunk** — compute SHA-256 of each downloaded chunk and compare to the hash recorded in `BackupRecord.collections[n].chunks[i].sha256Hash`. Stop if any chunk hash mismatches.
4. **Decompress each chunk:** `gunzip {db}.{collection}.part-{NNN}.bson.gz`
5. **Restore each chunk in order:**
   ```
   mongorestore \
     --uri "{restoreAccountConnectionString}" \
     --db {databaseName} \
     --collection {collectionName} \
     --file {db}.{collection}.part-{NNN}.bson \
     --numInsertionWorkersPerCollection 4 \
     --noIndexRestore
   ```
6. **Restore indexes** from the index definitions stored in the `BackupRecord`
7. **Verify document count:** `db.{collection}.countDocuments()` must equal `BackupRecord.collections[n].documentCount`. Stop if mismatch.

### 9.4 Step B — Incremental and Checkpoint Replay

For each backup operation in the restore chain after the full backup, applied in strict chronological order (oldest first):

1. **Identify the collections to restore** — read `BackupRecord.collections[]` for this operation. Collections absent from the array had no changes during this period and require no action.
2. **For each collection present** (parallelisable within a single backup operation):
   - For each chunk in `BackupRecord.collections[n].chunks[]`, in ascending `part-NNN` order:
     - Download the chunk blob (e.g., `incremental/2025/Q1/{fullId}/2025-01-03/{db}.{collection}.part-001.bson.gz`)
     - Verify SHA-256 hash against `BackupRecord.collections[n].chunks[i].sha256Hash`. Stop if mismatch.
     - Decompress and stream each BSON document from the chunk
     - Upsert the full document into the restore account using `replaceOne` with `upsert: true`, keyed on `_id`
   - Log document count applied and any errors
3. **Complete the operation** before moving to the next backup operation in the chain. Chronological order between operations must be preserved; parallelism within a single operation is safe.

**Partial collection restore:** Because each collection is a separate file, a single collection or any subset can be restored by applying only the relevant collection files from each chain step — the other collection files are simply skipped. No special tooling is required.

The upsert strategy is correct by construction:
- A document present in both the full backup and a subsequent incremental will be upserted twice. The second upsert (from the incremental) writes the more recent state. The result is the correct final version.
- A document soft-deleted after the full backup will appear in an incremental with `isDeleted: true`. Upsert correctly writes this final state.
- A document created after the full backup appears only in an incremental. Upsert inserts it.
- The 5-minute overlap buffer means some documents near the anchor boundary appear in both the full and the first incremental. Upsert handles these as harmless idempotent overwrites.

### 9.5 Post-Restore Verification and Promotion

```
□ Compare restored collection document counts vs production snapshot counts
□ Perform spot-checks on a sample of known records
□ Document all verification results in the audit log
□ Obtain sign-off from authorized individuals before any promotion step
□ If promoting to production: execute DNS/connection string swap
  or perform targeted data merge (depending on restore scenario)
□ Decommission restore account within 48 hours of promotion sign-off
□ Record decommission in audit log
```

---

## 10. Failure Handling and Recovery

### 10.1 Incremental Backup Failure

If a daily incremental backup fails:

**Retry policy:** Automatic retry up to 2 times with exponential backoff (5 minutes between attempts).

**If all retries fail:**
1. Do not write the manifest for this backup — absence of the manifest is the failure signal
2. Send an immediate notification to the system administrator including:
   - Which backup folder failed and at what step
   - The error details (sanitized — no PHI in notifications)
   - The last folder with a valid manifest (last known good backup)
   - An estimated data loss window if the failure is not resolved
3. Block subsequent daily incrementals from running until the failure is resolved. The chain integrity check at the start of each incremental (Section 6.2 step 2) enforces this automatically — it checks for a valid manifest in the previous folder before proceeding.

**Recovery options (in order of preference):**

The most recently completed backup is always identified as the newest folder containing a `manifest.json`.

*Option 1 — Retry manually.* If the failure was transient (network timeout, throttling), the administrator can trigger a manual retry. The incremental reads the anchor timestamp from the last valid manifest and captures everything since that point.

*Option 2 — Gap report and acceptance.* If the failure cannot be retried (e.g., the data window has passed and documents have been updated since), the system generates a gap report:
   - Documents modified between `lastSuccessfulAnchorTs` and `currentTs`
   - Estimated count of affected documents per collection
   - Recommended action: proceed with gap accepted (document the accepted risk) or trigger an emergency full backup to reset the chain

*Option 3 — Emergency full backup.* An emergency full backup can be triggered at any time. It resets the anchor to the current point in time and starts a new incremental chain. Historical data between the failed incremental and the emergency full is not captured in the incremental chain but remains in the preceding backups up to the last successful incremental.

### 10.2 Checkpoint Prerequisite Failure

If the weekly checkpoint scheduler detects that one or more of the six source incrementals are not in `status: Completed`:

1. Defer the checkpoint by one hour and recheck
2. If still not resolved after 2 hours, send an alert to the administrator
3. Generate a partial checkpoint report identifying which source incremental folders are missing a manifest
4. Do not generate a checkpoint from incomplete source folders — a partial checkpoint would corrupt the restore chain for week-boundary restores
5. When all source incremental folders have valid manifests (even if delayed), trigger the checkpoint manually

### 10.3 Full Backup Failure

A full backup failure is the highest severity event in this system. Without a valid full backup, the incremental chain for that quarter cannot be used for restore.

**Automatic actions:**
1. Do not write the manifest for this backup — no manifest means no valid full backup for this quarter
2. Alert administrator immediately
3. Block incremental backups for the new quarter until the full backup folder has a valid manifest (incrementals without a valid full anchor are unusable)

**Recovery:**
- Retry the full backup (it is idempotent — if a collection blob already exists in storage, that collection is skipped)
- If the full backup cannot complete within 24 hours, escalate to a P1 incident

### 10.4 Failure Recovery Report Contents

When an administrator is notified of a backup failure, the system generates a structured report containing:

| Field | Content |
|---|---|
| Failure type | Full / Incremental / Checkpoint |
| Failed backup folder | Full blob storage path of the failed backup folder |
| Failure timestamp | UTC |
| Last successful backup | Folder path, type, and manifest timestamp |
| Data loss window | Duration between last successful backup and failure |
| Affected collections | List (no document content) |
| Estimated affected document count | Per collection, estimated from `_ts` query |
| Recommended action | Option 1, 2, or 3 as described above |
| Chain integrity status | Whether subsequent backups can safely proceed |

### 10.5 Orphaned Chunk Cleanup

A periodic maintenance process runs daily and scans the backup container for folders that contain chunk files but no `manifest.json`. These are the remnants of failed backup attempts — partial uploads that did not complete successfully.

**Cleanup rules:**
- A folder is eligible for cleanup only if it is older than 24 hours. This prevents the cleanup process from racing with an active backup operation.
- Only chunk files (`*.bson.gz`) are removed. No other blobs are touched.
- Every cleanup action is logged: folder path, number of chunks removed, and the timestamp of the cleanup — no PHI in log entries.
- If a folder is cleaned up, an alert is sent to the administrator identifying the failed backup attempt and the time window it covered, so the data loss window can be assessed.

This process is self-healing: orphaned chunks do not accumulate indefinitely, storage costs remain predictable, and failed backup attempts are surfaced promptly rather than silently occupying storage.

---

## 11. Backup Metadata and Audit Trail

### 11.1 Metadata Storage

All backup metadata is stored in **blob-resident `manifest.json` files** written alongside the backup chunks in Azure Blob Storage. There is no separate database for backup metadata. This design eliminates the circular dependency that would prevent restore if the source database were lost.

The manifest file for each backup operation:
- Is written as the **last step** of a successful backup seal — its presence is an unconditional completion guarantee
- Is covered by the same WORM immutability policy as all chunk blobs — it cannot be modified after writing
- Is covered by the same lifecycle management policy — retained for 6 years minimum
- Contains no PHI — only collection names, document counts, hashes, timestamps, and structural metadata
- Is small (typically 2–10 KB) regardless of database or collection size

### 11.2 Manifest File Structure

Every backup operation produces exactly one `manifest.json` in its backup folder. The structure is uniform across all three backup types.

```json
{
  "schemaVersion":          1,
  "backupType":             "Full | Incremental | Checkpoint",
  "backupFolder":           "full/2025/Q1/a3f8c2d1-...",
  "parentFolder":           "full/2025/Q1/a3f8c2d1-...",
  "sourceIncrementalFolders": [],

  "startedAtUtc":           "2025-01-01T00:00:00Z",
  "completedAtUtc":         "2025-01-01T02:17:43Z",
  "queryFromTs":            null,
  "anchorTs":               1735689601,

  "databaseName":           "{database}",
  "quarterLabel":           "2025-Q1",
  "weekNumber":             null,
  "format":                 "Bson",

  "collections": [
    {
      "collectionName":     "{collection}",
      "totalDocuments":     4823901,
      "chunkCount":         3,
      "exportedAtUtc":      "2025-01-01T01:04:22Z",
      "chunks": [
        {
          "chunkIndex":     1,
          "blobPath":       "full/2025/Q1/{backupId}/{db}.{collection}.part-001.bson.gz",
          "documentCount":  1800000,
          "sha256Hash":     "e3b0c44298fc...",
          "sizeBytes":      3200000000
        }
      ]
    }
  ],

  "triggeredBy":            "scheduler",
  "correlationId":          "f1a2b3c4-..."
}
```

**Key fields:**
- `queryFromTs` — null for Full backups (full scan); Unix timestamp for Incremental and Checkpoint (lower bound of `_ts` query, including the 5-minute buffer)
- `anchorTs` — Unix timestamp captured at end of backup execution; becomes `queryFromTs` for the next incremental
- `parentFolder` — blob path of the preceding backup in the chain; null for Full backups
- `sourceIncrementalFolders` — list of the six source incremental folder paths; populated for Checkpoint only

### 11.3 Backup History and Chain Resolution

**Listing available backups** requires only a blob folder listing — no manifest content is read:

```
List prefixes: incremental/2025/Q1/{fullBackupId}/
→ Returns: 2025-01-02T0100Z/, 2025-01-03T0100Z/, ...
Lexicographic sort = chronological order (ISO 8601 property)
```

**Resolving a restore chain** for a target timestamp requires reading only the manifests in the chain — at most 19 small JSON files for the worst-case restore scenario.

**Identifying failed backups** requires no manifest reading — a folder present in the listing without a `manifest.json` is a failed backup.

### 11.4 Audit Trail Requirements

Every operation that reads, writes, or acts on backup data produces a durable record. The following events are audited:

| Event | Who | What | When | Stored in |
|---|---|---|---|---|
| Backup completed | System | Manifest written to blob folder | UTC timestamp | manifest.json (completedAtUtc) |
| Backup failed | System | No manifest written; error summary logged (no PHI) | UTC timestamp | Application log |
| Orphaned chunks cleaned | System | Folder path, chunk count removed | UTC timestamp | Application log |
| Verification run | System | Level, result, detail | UTC timestamp | manifest.json (verificationHistory) |
| Restore requested | Named principal | Target folder, reason, approver | UTC timestamp | restore-request.json in restore folder |
| Restore completed | Named principal | Folders used, counts verified | UTC timestamp | restore-request.json |
| Ephemeral account decommissioned | Named principal | Account name, timestamp | UTC timestamp | restore-request.json |
| Blob accessed | Any principal | Blob path, operation | UTC timestamp | Azure Storage diagnostic logs |

---

## 12. HIPAA Compliance Analysis

### 12.1 PHI Boundary Tracing

PHI is present in backup blobs (full, incremental, and checkpoint files). It is **not** present in:
- Backup metadata documents (these contain collection names, document counts, hashes, and blob paths — no document content)
- Log messages (PHI is explicitly excluded from all structured log fields)
- Error messages and alerts (sanitized before transmission)
- Blob storage paths (paths contain collection names, dates, and UUIDs — no PHI or record identifiers)

### 12.2 Access Control Analysis

| Access path | Control mechanism | Least privilege enforced? |
|---|---|---|
| Backup service writes blobs and manifests | Managed identity + `Storage Blob Data Contributor` scoped to backup container | Yes |
| Backup service reads source database | Managed identity + read-only Cosmos DB RBAC role | Yes |
| Restore operator reads blobs and manifests | Managed identity + `Storage Blob Data Reader` + Azure PIM time-limited elevation | Yes |
| Restore operator writes to restore account | Managed identity + write role scoped to restore account only | Yes |
| Administrator lists and reads backup history | Managed identity + `Storage Blob Data Reader` scoped to backup container | Yes |

No shared access keys. No SAS tokens with long expiry. No connection strings in source code or configuration files. All secrets in Azure Key Vault, accessed via managed identity.

### 12.3 Tamper-Evidence

| Control | Mechanism | Guarantees |
|---|---|---|
| Blob immutability | WORM time-based retention on Blob Storage container | Backup blobs cannot be modified or deleted before retention expiry, even by storage administrators |
| Hash verification | SHA-256 computed at write time, stored in manifest.json | Any post-write modification of a chunk blob is detectable |
| Manifest integrity | manifest.json written last and covered by WORM policy | Manifest cannot be modified or backdated after writing |
| Access logging | Azure Storage diagnostic logs to Log Analytics | Every blob read and write is recorded with principal identity and timestamp |

### 12.4 Data Minimization

- Backup blobs contain only the data necessary to restore the database — no additional copies or derivatives are created
- Log messages contain correlation IDs, counts, and blob paths only — no document content, no PHI
- Error messages are sanitized before being written to logs or sent in notifications
- The ephemeral restore account is decommissioned within 48 hours — PHI is not retained beyond the restore window

### 12.5 Geographic and Redundancy Controls

| Requirement | Implementation |
|---|---|
| Data residency | Backup storage account in the same Azure region as production Cosmos DB account |
| Geographic redundancy | GRS (Geo-Redundant Storage) — backup data replicated to a paired Azure region |
| BAA coverage | Both regions are covered by the existing Microsoft Azure BAA |

---

## 13. Known Gaps and Accepted Risks

These items are known limitations of the design. They are documented here for transparency and require explicit acknowledgment from the compliance department.

### Gap 1 — RPO is 24 Hours

The maximum data loss window between nightly incremental backups is 24 hours. A disaster occurring at 00:59 (one minute before the next incremental runs) would result in loss of up to 24 hours of data changes.

**Mitigation available:** The architecture supports hourly incremental backups without design changes. If 24-hour RPO is not acceptable, increasing backup frequency to hourly reduces the worst-case RPO to 1 hour.

**Action required:** Compliance team must formally document and sign off on the 24-hour RPO, or request a change to hourly incrementals.

### Gap 2 — Restore Requires a Second Cosmos DB Account

Restore is always performed to a new, empty Cosmos DB account — never directly into production. Promoting the restored data to production requires a connection string swap or targeted data merge operation, both of which require human intervention and carry execution risk.

**Mitigation:** The restore runbook (Section 9) defines the promotion procedure explicitly. All promotion steps require sign-off from two named individuals.

### Gap 3 — Ephemeral Restore Account Contains Full PHI Copy

During restore dry-runs and actual restores, a complete copy of production PHI exists in the ephemeral restore account for the duration of the operation. This account is within the same Azure subscription and BAA coverage, but it represents an additional PHI exposure surface.

**Mitigation:** Access to the ephemeral account is restricted by RBAC to named restore operators. The account is decommissioned within 48 hours. Both restrictions are procedurally enforced and audited.

### Gap 4 — Customer-Managed Encryption Keys Not Yet Configured

The current design relies on Microsoft-managed encryption keys for Cosmos DB and Azure Blob Storage (AES-256 at rest). Customer-managed keys (CMK) via Azure Key Vault are available and provide additional control over the encryption key lifecycle.

**Action required:** The adopting organisation's compliance team should determine whether CMK is required under their HIPAA compliance posture. If required, this must be added to the implementation scope before go-live.

### Gap 5 — RTO Is Not Formally Defined

The restore procedure has been designed and documented, but a formal Recovery Time Objective (how long the system may be unavailable during a restore) has not been established.

**Action required:** Compliance team must define and document the RTO. The restore chain design (maximum 19 files) is sufficient for any reasonable RTO given the expected data volumes, but this must be validated with a timed restore dry-run before go-live.

---

## 14. Open Items Requiring Compliance Sign-off

The following items require an explicit decision from the compliance department before implementation begins.

| # | Item | Decision required | Default if no response |
|---|---|---|---|
| C1 | **RPO acceptance** — Is a 24-hour Recovery Point Objective acceptable under the adopting organisation's HIPAA disaster recovery plan? | Accept 24-hour RPO, or request hourly incrementals | Implementation blocked |
| C2 | **RTO definition** — What is the maximum acceptable time to restore the database to a working state? | Formal RTO target in hours | Implementation blocked |
| C3 | **Customer-managed keys** — Does the compliance posture require CMK for encryption at rest on Cosmos DB and Blob Storage? | Yes / No | No (Microsoft-managed keys) |
| C4 | **Dry-run PHI handling** — Is the 48-hour ephemeral restore account window acceptable for quarterly dry-runs? | Accept / Reduce window | 48 hours accepted |
| C5 | **Backup storage location** — Confirm the Azure region for backup storage and verify it is covered by the existing BAA | Confirm region | Same region as production |
| C6 | **HIPAA 6-year retention confirmation** — Confirm that 6-year backup retention is the adopting organisation's interpretation of §164.530(j) and applicable state law | Confirm / Adjust duration | 6 years |
| C7 | **Restore authorization** — Confirm that two-person sign-off is the required authorization model for restore operations, and identify the named roles authorized to approve | Confirm roles | Two named senior individuals |

---

*End of document.*

*This document is a compliance template produced by the MongoCustody project. Adopting organisations must substitute their own system name, database names, Azure resource identifiers, and named personnel before submitting for compliance review. Any changes to the underlying assumptions (Section 2.3) or to the Azure infrastructure configuration require a revision to this document and a fresh compliance review.*
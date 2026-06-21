# Audit Report — Design Document

## Purpose

Enable production teams and compliance auditors to obtain a professionally
formatted Markdown report covering all backup and restore activity for any
given date range, or a detailed report for a single restore operation.
Designed specifically for HIPAA audit requests — no third-party libraries,
no separate reporting database.

---

## Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Output format | Markdown (`.md`) | No dependencies; renders in any editor; converts to PDF/DOCX via standard tools |
| Default output | Always write a file | stdout of a multi-page structured document is not practical |
| stdout | Brief business summary only | Operator confirmation — not the full report |
| Two-person sign-off | Recorded as-is, never enforced | System records who requested and who approved; policy enforcement is organisational |
| Chain narrative | Story-style with step table | Auditors care about the logical path to the restore point, not raw folder paths |
| Backup scope in range report | Full backups only | Incrementals are voluminous and technically redundant for audit purposes; they appear only within restore chain narratives |

---

## Storage: Restore Record Indexing

### Problem

Restore records are currently written to flat paths:

```
restores/{restoreId}/restore-request.json
```

There is no date-rooted index, so listing all restore operations within a
date range requires either a full container scan (expensive) or knowing the
restore IDs upfront.

### Solution: Monthly pointer index

On every `SaveRestoreRecordAsync` call, write an additional lightweight
pointer blob alongside the main record:

```
_restore_index/{year:D4}/{month:D2}/{restoreId}/pointer.json
```

Content (minimal — only what is needed for date filtering without reading
the full record):

```json
{
  "restoreId": "b7c1d4e2-...",
  "requestedAtUtc": "2025-02-14T09:15:00+00:00"
}
```

This uses the existing `ListFoldersAsync` + `ReadTextBlobAsync` pattern
already in place for backup records — no changes to `IStorageConnector`
are required.

### Listing algorithm (`ListRestoreRecordsAsync`)

1. Compute all `(year, month)` pairs in `[from, to]`.
2. For each pair, call `ListFoldersAsync("_restore_index/{year}/{MM}/")`.
3. For each folder returned, read `pointer.json` → deserialize restoreId + date.
4. Filter: `requestedAtUtc >= from && requestedAtUtc <= to`.
5. For each match, read the full `RestoreRecord` from `restores/{restoreId}/restore-request.json`.
6. Return sorted oldest-first.

The pointer blob is written once and never updated — it contains only the
creation timestamp, which never changes. Final status lives in the full record.

### New methods added to `IMetadataRepository`

```csharp
// List all restore records in a date range, oldest-first.
Task<IReadOnlyList<RestoreRecord>> ListRestoreRecordsAsync(
    DateTimeOffset from,
    DateTimeOffset to,
    CancellationToken ct = default);

// Fetch a single restore record by ID.
Task<RestoreRecord?> GetRestoreRecordAsync(
    string restoreId,
    CancellationToken ct = default);

// Read a BackupRecord manifest by folder path (used for chain resolution).
Task<BackupRecord?> GetBackupRecordByFolderAsync(
    string folderPath,
    CancellationToken ct = default);
```

---

## Architecture

```
MongoCustody.Core
  Models/
    AuditReportRequest     — From/To/DatabaseName or RestoreId
    AuditReport            — structured result (summary + backups + restores)
    AuditSummary           — aggregate counts and document totals
    RestoreAuditEntry      — one per restore operation (record + chain steps)
    RestoreChainStep       — one per backup applied during restore

  Engines/
    AuditReportEngine      — queries metadata, assembles AuditReport

  Abstractions/
    IMongoCustodyService   — GenerateAuditReportAsync(request, ct)

MongoCustody.Cli
  MarkdownReportRenderer   — AuditReport → Markdown string
  Commands/
    ReportCommand          — two modes: range and single-restore
```

`MongoCustody.Core` owns data assembly (library consumers get programmatic
access to `AuditReport`). The CLI owns rendering — the same separation used
by backup and restore.

---

## CLI Command

```bash
# Range report — all databases
mongocustody report \
  --from 2025-01-01T00:00:00Z \
  --to   2025-03-31T23:59:59Z \
  [--output-file Q1-2025-audit.md]

# Range report — single database
mongocustody report \
  --from 2025-01-01T00:00:00Z \
  --to   2025-03-31T23:59:59Z \
  --database your-database \
  [--output-file Q1-2025-your-database-audit.md]

# Single restore report — for a specific incident
mongocustody report \
  --restore-id b7c1d4e2-... \
  [--output-file restore-INC-2025-0042.md]
```

`--output-file` is optional; defaults to an auto-generated name:
- Range: `mongocustody-audit-{from:yyyy-MM-dd}_{to:yyyy-MM-dd}.md`
- Single restore: `mongocustody-restore-{restoreId[..8]}.md`

**stdout** always prints a brief business summary (period, counts, filename).
The full report is always written to a file.

---

## Markdown Report Structure

### Range report

```markdown
# MongoCustody Audit Report

**Period:** 2025-01-01 – 2025-03-31
**Database:** all databases
**Generated:** 2025-06-20 14:32 UTC
**Generated by:** HOSTNAME

---

## Executive Summary

| Metric | Value |
|---|---|
| Full backups completed | 1 |
| Full backups failed | 0 |
| Restore operations completed | 2 |
| Restore operations failed | 0 |
| Total collections backed up | 8 |
| Total documents backed up | 1,420,183 |

---

## Full Backup Activity

### 2025-01-01 01:00 UTC — your-database [COMPLETED]

| Field | Value |
|---|---|
| Backup ID | a3f8c2d1-... |
| ...

| Collection | Documents | Chunks |
|---|---|---|
| users | 42,000 | 1 |
...

---

## Restore Activity

### 2025-02-14 09:15 UTC — your-database → your-database-restore [COMPLETED]

| Field | Value |
|---|---|
| Restore ID | b7c1d4e2-... |
| Requested restore point | 2025-02-13 23:59 UTC |
| Requested by | admin@example.com |
| Approved by | compliance@example.com |
| Reason | Data recovery — incident INC-2025-0042 |
| ...

#### Restore Chain Applied

To reach the requested restore point of **2025-02-13 23:59 UTC**, the
following backups were applied in sequence:

| Step | Type | Backup date | Documents written |
|---|---|---|---|
| 1 | Full backup | 2025-01-01 01:00 UTC | 1,420,183 inserted |
| 2 | Incremental | 2025-02-10 01:00 UTC | 8,431 upserted |
...

The full backup from **2025-01-01** provided the baseline (1,420,183
documents). 4 subsequent incrementals were replayed to reach the requested
point, updating a total of 17,534 documents.

---

## Notes

- All backup manifests contain SHA-256 per-collection hash values.
- Restore operations record the requesting and approving principal before
  any data is written to the target.
- This report was generated from immutable Blob Storage records.
```

### Single-restore report

Same structure — no "Full Backup Activity" section. Title:
`# MongoCustody Restore Audit Report — {restoreId}`

---

## Implementation Sequence

| Step | What | Breaking change? |
|---|---|---|
| 1 | `StoragePathHelper` — add restore index path helpers | No |
| 2 | `IMetadataRepository` — add 3 new methods | No (additive) |
| 3 | `BlobMetadataRepository` — implement + write index on save | No |
| 4 | Core models (`AuditReportRequest`, `AuditReport`, …) | No (new) |
| 5 | `AuditReportEngine` | No (new) |
| 6 | `IMongoCustodyService` + `MongoCustodyService` | No (additive) |
| 7 | `MarkdownReportRenderer` (CLI) | No (new) |
| 8 | `ReportCommand` + `Program.cs` | No (additive) |

No existing tests are affected. New unit tests for `AuditReportEngine`
and `MarkdownReportRenderer` can be added independently.

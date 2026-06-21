# MongoCustody

A HIPAA-compliant backup and recovery library for MongoDB, built on .NET 10.

MongoCustody implements a hybrid incremental backup strategy — quarterly full backups, daily incrementals, and weekly checkpoints — with tamper-evident storage, a full audit trail, and a bounded restore chain regardless of how far into a retention period you are. It works against any MongoDB-compatible database, including Azure Cosmos DB for MongoDB.

---

## Table of Contents

- [Why MongoCustody](#why-mongocustody)
- [Backup Strategy](#backup-strategy)
- [Project Structure](#project-structure)
- [Getting Started](#getting-started)
- [Configuration](#configuration)
- [Usage](#usage)
  - [As a Library](#as-a-library)
  - [As a CLI Tool](#as-a-cli-tool)
- [Restore Procedure](#restore-procedure)
- [Validation](#validation)
- [HIPAA Compliance](#hipaa-compliance)
- [Storage Layout](#storage-layout)
- [Development](#development)
- [Testing](#testing)
- [Implementation Phases](#implementation-phases)

---

## Why MongoCustody

MongoDB's native backup tooling and cloud provider backup features typically offer short retention windows (30 days or less) with no user custody over the backup artifacts. For regulated environments — HIPAA, SOC 2, PCI-DSS — you need:

- **Your own custody** of backup artifacts, with proof of integrity
- **Long-term retention** (HIPAA requires up to 6 years)
- **Tamper-evident storage** to satisfy audit requirements
- **A tested, documented restore procedure** — not just a hope that the blobs are there
- **A bounded restore complexity** that does not grow linearly with time

MongoCustody is designed to satisfy all of these requirements out of the box, without locking you into a proprietary backup format or a third-party SaaS platform.

---

## Backup Strategy

MongoCustody uses a three-tier hybrid model:

```
Q1 Full Backup (Jan 1)
│   Complete export of all collections   anchor_ts = T₀
│
├── Incremental  Jan 2   _ts > T₀   anchor_ts = T₁
├── Incremental  Jan 3   _ts > T₁   anchor_ts = T₂
├── Incremental  Jan 4   _ts > T₂   anchor_ts = T₃
├── Incremental  Jan 5   _ts > T₃   anchor_ts = T₄
├── Incremental  Jan 6   _ts > T₄   anchor_ts = T₅
├── Incremental  Jan 7   _ts > T₅   anchor_ts = T₆
│
├── Checkpoint   Jan 8   merge of Jan 2–7 (deduplicated, per collection)
│
├── Incremental  Jan 9   _ts > T₆   anchor_ts = T₇
│   ... (13 weeks per quarter)
│
Q2 Full Backup (Apr 1)   new chain begins
```

| Backup type | Frequency | Purpose |
|---|---|---|
| **Full** | Quarterly (Jan 1, Apr 1, Jul 1, Oct 1) | Canonical baseline; starts a new incremental chain |
| **Incremental** | Daily (off-hours) | Captures all documents changed since the previous backup using the server-managed `_ts` field |
| **Checkpoint** | Weekly | Deduplicates 6 preceding incrementals into a single file per collection; bounds restore complexity |

### Key design properties

**Chunked export, one or more files per collection, for all backup types.** Full, incremental, and checkpoint backups split large collections into sequentially numbered chunk files at document boundaries (default: 10 GB uncompressed per chunk). No document is ever split across chunks. Any individual collection or subset of collections can be restored independently. The restore procedure is identical regardless of backup type or number of chunks.

**Server-managed change detection.** Incrementals use MongoDB's `_ts` field — set by the database engine on every write, not by application code — as the change predicate. This is immune to application bugs, bulk imports, and clock skew between application servers.

**Self-contained storage.** All backup metadata lives in `manifest.json` files written alongside the backup chunks in Blob Storage. No separate metadata database is required. If the source database is completely lost, the backup system remains fully operational from Blob Storage alone — no circular dependency.

**Soft-delete aware.** MongoCustody assumes all collections implement soft delete (a boolean deleted flag + a server-managed timestamp). Deletions are therefore visible as document updates and are captured correctly by incremental backups. Hard-delete operations are out of scope. The field names default to `isDeleted` and `_ts` and are [configurable per database](#per-database-field-name-overrides).

**Bounded restore complexity.** The maximum restore chain depth is 19 operations (1 full + 12 checkpoints + 6 daily incrementals), regardless of the restore target date. This is constant and predictable.

**5-minute overlap buffer.** Incremental queries use `_ts > (previousAnchorTs - 300 seconds)` to ensure no document near the boundary is missed. Duplicate documents resulting from the overlap are handled correctly by upsert during restore.

**Fully streaming I/O.** All backup and restore operations are fully streaming. No collection is loaded into memory in full at any point. Memory consumption per active collection operation is O(1) with respect to collection size.

---

## Project Structure

```
MongoCustody/
│
├── src/
│   ├── MongoCustody.Core/             # Business logic — no Azure SDK dependencies
│   │   ├── Abstractions/              # All interfaces
│   │   ├── Models/                    # BackupRecord, BackupPolicy, request/result types
│   │   ├── Engines/                   # BackupEngine, RestoreEngine,
│   │   │                              #   ValidationEngine, CheckpointEngine
│   │   └── Extensions/                # IServiceCollection wiring
│   │
   ├── MongoCustody.Connectors/       # Azure SDK implementations
│   │   ├── MongoDbConnector           # IBackupSourceConnector
│   │   ├── BlobStorageConnector       # IStorageConnector
│   │   ├── BlobMetadataRepository     # IMetadataRepository — manifests live in Blob Storage
│   │   └── Secrets/
│   │       ├── AzureKeyVaultSecretProvider
│   │       └── EnvironmentSecretProvider
│   │
│   └── MongoCustody.Cli/              # dotnet tool — admin CLI
│       └── Commands/
│           ├── BackupCommand
│           ├── RestoreCommand
│           ├── BackupsCommand
│           ├── ValidateCommand
│           ├── ReportCommand
│           └── HealthCommand
│
└── tests/
    ├── MongoCustody.Core.Tests/       # Unit tests — no infrastructure required
    └── MongoCustody.Connectors.Tests/ # Integration tests — Azurite + MongoDB (docker-compose)
```

`MongoCustody.Core` has zero Azure SDK dependencies. All backup logic — the export algorithm, restore chain resolution, validation rules, and checkpoint merge — lives here and is testable with plain mocks, no emulator required.

`MongoCustody.Connectors` is where `MongoDB.Driver`, `Azure.Storage.Blobs`, and `Azure.Identity` live. Keeping them separate means the core logic tests run fast with no infrastructure.

---

## Getting Started

### Prerequisites

- .NET 10 SDK
- A MongoDB-compatible database (MongoDB 4.0+ or Azure Cosmos DB for MongoDB API)
- Azure Blob Storage account (for backup artifact and manifest storage)

### Install the CLI tool

```bash
dotnet tool install --global MongoCustody.Cli
```

### Add the library to your project

```bash
dotnet add package MongoCustody.Core
dotnet add package MongoCustody.Connectors
```

---

## Configuration

All configuration maps to `MongoCustodyConfiguration`. Provide it via a `mongocustody.json` file or programmatically.

**No URIs or connection strings belong in the config file.** Every environment-specific value — connection strings, account endpoints, and the Key Vault URI itself — is resolved at runtime from environment variables (or from Key Vault, once the vault URI is known). The config file only holds key names that tell MongoCustody *which* environment variable to read.

### Configuration file (`mongocustody.json`)

```json
{
  "storageContainerName": "backups",
  "databases": [
    {
      "databaseName": "your-database",
      "excludedCollections": ["_sessions", "_changelog"],
      "timestampAttribute": "_ts",
      "deletedAttribute": "isDeleted"
    }
  ],
  "policy": {
    "incrementalBufferSeconds": 300,
    "backupFormat": "Bson",
    "compressionLevel": "Fastest",
    "maxParallelCollections": 4,
    "blobBlockSizeMb": 100,
    "chunkSizeGb": 10
  },
  "secrets": {
    "provider": "EnvironmentVariables",
    "keyVaultUriKey": "MONGOCUSTODY_KEYVAULT_URI",
    "mongoConnectionStringKey": "MONGOCUSTODY_MONGO_CONNECTION_STRING",
    "mongoAccountEndpointKey": "MONGOCUSTODY_MONGO_ENDPOINT",
    "storageConnectionStringKey": "MONGOCUSTODY_STORAGE_CONNECTION_STRING",
    "storageAccountEndpointKey": "MONGOCUSTODY_STORAGE_ENDPOINT"
  }
}
```

All `secrets.*Key` values are overridable. The defaults shown above are used when the field is omitted.

### Secret providers

| Provider | When to use |
|---|---|
| `EnvironmentVariables` | Local development, CI pipelines, and any host where secrets are injected as environment variables. |
| `AzureKeyVault` | Azure-hosted workloads (Azure Functions, Container Apps, App Service). Uses `DefaultAzureCredential` — works with system-assigned managed identity on Linux and Windows. |

### Environment variables

All environment-specific values are supplied via environment variables. The variable names are configurable via the `secrets.*Key` fields above; these are the defaults:

| Environment variable | Used for | Required when |
|---|---|---|
| `MONGOCUSTODY_MONGO_CONNECTION_STRING` | MongoDB connection string | Always (or use endpoint below) |
| `MONGOCUSTODY_MONGO_ENDPOINT` | MongoDB account endpoint URL | Managed identity — when no connection string is set |
| `MONGOCUSTODY_STORAGE_CONNECTION_STRING` | Azure Blob Storage connection string | Always (or use endpoint below) |
| `MONGOCUSTODY_STORAGE_ENDPOINT` | Azure Blob Storage account endpoint URL | Managed identity — when no connection string is set |
| `MONGOCUSTODY_KEYVAULT_URI` | Azure Key Vault URI | Only when `provider` is `AzureKeyVault` |

MongoCustody tries the connection string first; if it is absent it falls back to the endpoint URL (managed identity path). At least one of each pair must be set.

### AzureKeyVault provider

When using Key Vault, the Key Vault URI itself comes from the environment variable named by `keyVaultUriKey` (default `MONGOCUSTODY_KEYVAULT_URI`). The remaining secret keys become Key Vault secret names and must follow Key Vault naming rules (`[a-zA-Z0-9-]` only — no underscores). Use hyphen-separated names for Key Vault:

```json
"secrets": {
  "provider": "AzureKeyVault",
  "keyVaultUriKey": "MONGOCUSTODY_KEYVAULT_URI",
  "mongoConnectionStringKey": "mongocustody-mongo-connection-string",
  "storageConnectionStringKey": "mongocustody-storage-connection-string"
}
```

Then set a single environment variable pointing to your vault — all other secrets are fetched from Key Vault at runtime:

```bash
export MONGOCUSTODY_KEYVAULT_URI="https://your-keyvault.vault.azure.net/"
```

### Per-database field name overrides

If your collections use different field names for the timestamp or soft-delete flag, override them per database:

```json
"databases": [
  {
    "databaseName": "legacy-db",
    "timestampAttribute": "updatedTs",
    "deletedAttribute": "delete_flag"
  }
]
```

| Field | Default | Purpose |
|---|---|---|
| `timestampAttribute` | `_ts` | Unix-second timestamp field used for incremental change detection |
| `deletedAttribute` | `isDeleted` | Boolean soft-delete flag used during validation |

---

## Usage

### As a Library

#### Register with dependency injection

```csharp
// Program.cs or Startup.cs
var config = builder.Configuration
    .GetSection("MongoCustody")
    .Get<MongoCustodyConfiguration>()!;

builder.Services.AddMongoCustody(config);
```

#### Run a full backup

```csharp
public class BackupJob(IMongoCustodyService backupService)
{
    public async Task RunAsync(CancellationToken ct)
    {
        // Verify all connectors are reachable before starting
        var health = await backupService.CheckHealthAsync(ct);
        if (!health.IsHealthy)
            throw new InvalidOperationException(
                $"Health check failed: {health.Summary}");

        var result = await backupService.RunFullBackupAsync(
            new BackupRequest
            {
                Databases = ["your-database"],
                TriggeredBy = "scheduler"
            },
            // Optional: receive per-collection progress reports
            progress: new Progress<OperationProgress>(p =>
                Console.WriteLine($"  {p.Completed}/{p.Total} ({p.Percentage:F0}%) — {p.CurrentItem}")),
            ct);

        if (result.Status != BackupStatus.Completed)
            throw new InvalidOperationException(
                $"Full backup failed: {result.FailureReason}");
    }
}
```

#### Run an incremental backup

```csharp
var result = await backupService.RunIncrementalBackupAsync(
    new BackupRequest
    {
        Databases = ["your-database"],
        TriggeredBy = "scheduler"
    }, ct);
```

#### Restore to a point in time

```csharp
var result = await backupService.RestoreAsync(
    new RestoreRequest
    {
        DatabaseName = "source-database",          // backup source database name
        TargetDate = new DateTimeOffset(2025, 4, 15, 0, 0, 0, TimeSpan.Zero),
        TargetConnectionString = "mongodb://restore-instance...",
        TargetDatabaseName = "restore-target-db",  // must exist and be empty
        // Optional: restore only specific collections
        Collections = ["collection-a", "collection-b"],
        RequestedBy = "admin@example.com",
        ApprovedBy = "compliance@example.com",
        Reason = "Data recovery — incident INC-2025-0042"
    },
    // Optional: receive per-collection-step progress reports across the full chain
    progress: new Progress<OperationProgress>(p =>
        Console.WriteLine($"  {p.Completed}/{p.Total} ({p.Percentage:F0}%) — {p.CurrentItem}")),
    ct);
```

#### Validate a backup

```csharp
var result = await backupService.ValidateBackupAsync(
    new ValidationRequest
    {
        BackupId = "a3f8c2d1-...",
        Levels = [
            ValidationLevel.StructuralIntegrity,
            ValidationLevel.CountConsistency
        ]
    }, ct);

foreach (var collection in result.CollectionResults)
    Console.WriteLine($"{collection.CollectionName}: {(collection.Passed ? "PASS" : "FAIL")}");
```

### As a CLI Tool

All commands accept `--config <path>` (default: `mongocustody.json`) and `--output text|json`.
No URIs or connection strings are stored in the config file — supply them via environment variables as described in the [Configuration](#configuration) section above.

#### Health check

```bash
mongocustody health --config mongocustody.json
```

#### Full backup

```bash
mongocustody backup full \
  --config mongocustody.json \
  --database your-database \
  [--triggered-by scheduler] \
  [--output json]
```

In text mode, a live progress bar is rendered to stderr while the backup runs:

```
⠹ Backing up 'your-database'  [████████░░░░░░░░░░░░]  4/12  33%  — users
```

The bar disappears when the operation completes and the result summary is printed.

#### Restore

```bash
# Restore all collections to a point in time
# --target-database must exist in the target instance and be empty (no collections)
mongocustody restore \
  --config mongocustody.json \
  --database source-database \
  --target-date 2025-04-15T00:00:00Z \
  --target-connection-string "mongodb://restore-instance..." \
  --target-database restore-target-db \
  --requested-by admin@example.com \
  --approved-by compliance@example.com \
  --reason "INC-2025-0042"

# Restore a subset of collections into a differently-named target database
mongocustody restore \
  --config mongocustody.json \
  --database source-database \
  --target-date 2025-04-15T00:00:00Z \
  --target-connection-string "mongodb://restore-instance..." \
  --target-database restore-target-db \
  --collections users projects
```

The restore command verifies the target database exists and is empty before writing any data,
then prints the resolved backup chain to stderr so you can confirm what will be applied. In
text mode a live progress bar tracks collection steps across the entire restore chain:

```
⠼ Restoring 'source-database' → 'restore-target-db'  [██████░░░░░░░░░░░░░░]  3/15  20%  — projects
```

> **Target database provisioning is out of scope.** MongoCustody will not create the target
> database. Provision it (with any required throughput or Cosmos DB settings) before running
> restore. The database must be empty — no collections — when the restore starts.

#### List full backups

```bash
# List all backups across all databases in a date range (summary view)
mongocustody backups list \
  --config mongocustody.json \
  --from 2025-01-01T00:00:00Z \
  --to   2025-03-31T23:59:59Z

# Filter to a single database
mongocustody backups list \
  --config mongocustody.json \
  --database your-database \
  --from 2025-01-01T00:00:00Z \
  --to   2025-03-31T23:59:59Z

# Include per-collection breakdown (document count and chunk count per collection)
mongocustody backups list \
  --database your-database \
  --from 2025-01-01T00:00:00Z \
  --to   2025-03-31T23:59:59Z \
  --detail

# Machine-readable output for scripting (--detail works here too)
mongocustody backups list \
  --from 2025-01-01T00:00:00Z \
  --to   2025-03-31T23:59:59Z \
  --output json
```

Results are ordered oldest-first. The default summary shows backup ID, database, completion timestamp, quarter label, storage folder, and aggregate totals (collection count, document count, chunk count). Add `--detail` (`-v`) to expand each backup with its full per-collection breakdown.

#### Validate a backup

```bash
# L1 — structural integrity (default, runs fast, no live DB required)
mongocustody validate \
  --backup-id a3f8c2d1 \
  --level structural

# L1 + L2 — structural integrity and count consistency
mongocustody validate \
  --backup-id a3f8c2d1 \
  --level structural count

# All levels except dry-run
mongocustody validate \
  --backup-id a3f8c2d1 \
  --level structural count incremental

# L4 — full restore dry-run (requires an ephemeral target account)
mongocustody validate \
  --backup-id a3f8c2d1 \
  --level dry-run \
  --dry-run-connection-string "mongodb://ephemeral-account..."

# All levels in one pass
mongocustody validate \
  --backup-id a3f8c2d1 \
  --level all \
  --dry-run-connection-string "mongodb://ephemeral-account..."

# Machine-readable output
mongocustody validate --backup-id a3f8c2d1 --level structural --output json
```

| Level flag | Alias | What it checks | Live DB needed |
|---|---|---|---|
| `structural` | `l1` | SHA-256 hash of each chunk matches manifest | No |
| `count` | `l2` | Backup document count matches live DB count as of anchor | Yes |
| `incremental` | `l3` | Incremental count ≥ changed documents in live DB since anchor | Yes |
| `dry-run` | `l4` | Full restore to ephemeral target; verifies counts and spot-checks 100 documents | Yes (ephemeral) |

Multiple levels can be passed as space-separated values (`--level structural count`) or comma-separated (`--level structural,count`). `--level all` runs all four levels and requires `--dry-run-connection-string`.

> **Planned commands** (Phases 6–9): `backup incremental`, `backup checkpoint`, `chain` (restore chain preview).

---

#### Generate an audit report

```bash
# Range report — all databases
mongocustody report generate \
  --from 2025-01-01T00:00:00Z \
  --to   2025-03-31T23:59:59Z

# Range report — single database
mongocustody report generate \
  --from 2025-01-01T00:00:00Z \
  --to   2025-03-31T23:59:59Z \
  --database your-database \
  --output-file Q1-2025-your-database-audit.md

# Single-restore report (for a specific incident or auditor request)
mongocustody report generate \
  --restore-id b7c1d4e2-1234-5678-9abc-def012345678 \
  --generated-by "Jane Smith, Compliance Officer"
```

The `--output-file` path is optional. When omitted, a filename is automatically
generated:
- Range mode: `mongocustody-audit-{from:yyyy-MM-dd}_{to:yyyy-MM-dd}.md`
- Single-restore mode: `mongocustody-restore-{first-8-chars-of-id}.md`

A brief summary is always printed to stdout; the full Markdown report is written
to the file. Example stdout output:

```
Audit Report — 2025-01-01 to 2025-03-31
  Full backups     : 1 completed, 0 failed
  Restore ops      : 2 completed, 0 failed
  Documents backed : 1,420,183

Full report written to: /home/ops/mongocustody-audit-2025-01-01_2025-03-31.md
```

The Markdown file contains an executive summary table, a full backup activity
section (range mode only), and a restore activity section — including the
restore chain narrative for each restore:

```
To reach the requested restore point of 2025-02-13 23:59 UTC, the following
backups were applied in sequence:

| Step | Type        | Backup date         | Documents written |
|------|-------------|---------------------|-------------------|
| 1    | Full backup | 2025-01-01 01:00 UTC| 1,420,183 inserted|
| 2    | Incremental | 2025-02-10 01:00 UTC| 8,431 upserted    |
| 3    | Incremental | 2025-02-11 01:00 UTC| 2,017 upserted    |
```

---

## Restore Procedure

Restore is always two steps: apply the full backup, then replay subsequent operations in order.

### Restore chain by scenario

| Target | Operations required | Max chain depth |
|---|---|---|
| Quarter start (full backup date) | Full[Q] | 1 |
| Specific day in week 1 (days 2–7) | Full[Q] + Inc[Day2..DayN] | 7 |
| End of week 1 | Full[Q] + CKP[Week1] | 2 |
| Specific day in week N | Full[Q] + CKP[Week1..N-1] + Inc[DayN] | 19 |
| End of quarter | Full[Q] + CKP[Week1..13] | 14 |

**Maximum chain depth: 19 operations.** Within each operation, collections are processed in parallel.

### Upsert correctness

All restore operations use `replaceOne` with `upsert: true` keyed on `_id`. This means:

- A document in both the full backup and a subsequent incremental is upserted twice — the incremental (applied second) writes the more recent state. The result is correct.
- A document soft-deleted after the full backup appears in an incremental with `isDeleted: true`. Upsert writes this final state correctly.
- The 5-minute buffer may produce harmless duplicate upserts near anchor boundaries. These are idempotent.

### Restore target requirements

Restore writes into a **named database within an existing MongoDB instance**. The target database:

1. **Must exist** — MongoCustody will not create or provision the database. Provision it (including any required throughput, tier, or Cosmos DB configuration) before running restore.
2. **Must be empty** — no collections. MongoCustody checks this before writing any data and aborts if any collections are found.
3. **Must not be the production source** — always use a separate instance or at minimum a separate database name.

A `RestoreRecord` (who requested, who approved, target database, reason) is written to Blob Storage before any data is written to the target. Verify counts and spot-check documents before promoting the restored database to production.

---

## Validation

MongoCustody provides four levels of validation, all of which read data without writing anything.

| Level | What it checks | When it runs |
|---|---|---|
| **Structural Integrity** | SHA-256 hash of each collection blob matches the hash stored in the `BackupRecord` at write time | After every backup automatically |
| **Count Consistency** | Document count in the backup matches a soft-delete-aware count query against the live database as of the anchor timestamp | After every full backup automatically |
| **Incremental Completeness** | Document count in the incremental is ≥ the count of documents modified in the live database since the previous anchor | After every incremental automatically |
| **Restore Dry-Run** | Full restore to an ephemeral account; verifies counts and spot-checks 100 random documents without promoting to production | Quarterly, or on demand |

Run validation at any time via the library or CLI:

```bash
# Validate structural integrity and count consistency
mongocustody validate --backup-id a3f8c2d1 --level structural,count

# Full restore dry-run (requires an ephemeral target account)
mongocustody validate \
  --backup-id a3f8c2d1 \
  --level dry-run \
  --dry-run-connection-string "mongodb://ephemeral-account..."
```

---

## HIPAA Compliance

MongoCustody is designed to satisfy the following HIPAA Security Rule provisions for systems that handle ePHI:

| Rule | Requirement | How addressed |
|---|---|---|
| §164.308(a)(7)(ii)(A) | Data backup plan | Quarterly full + daily incremental + weekly checkpoint |
| §164.308(a)(7)(ii)(B) | Disaster recovery plan | Documented restore procedure; restore chain always resolvable |
| §164.308(a)(7)(ii)(D) | Testing and revision | Quarterly restore dry-run with documented results |
| §164.312(a)(1) | Access control | Managed identity; no shared keys; RBAC-scoped roles |
| §164.312(b) | Audit controls | `BackupRecord` and `RestoreRecord` per operation with principal, timestamp, and correlation ID |
| §164.312(c)(1) | Integrity | SHA-256 per collection blob; WORM immutability on storage |
| §164.312(e)(1) | Transmission security | TLS enforced for all MongoDB and Blob Storage connections |
| §164.530(j) | Documentation retention | Configurable retention; 6-year lifecycle policy supported |

### PHI never appears in

- Log messages (structured logging with explicit allowlists; no document content)
- Error messages or exception details
- Backup metadata documents (collection names, counts, hashes, and blob paths only)
- Blob storage paths (dates, UUIDs, and collection names only — no PHI or record identifiers)

### Authentication requirements

MongoCustody is designed for managed identity access. Shared access keys and SAS tokens with long expiry are explicitly not supported for production use. The `EnvironmentSecretProvider` is provided for development and CI only.

### Compliance documentation

A full compliance document template covering backup architecture, HIPAA mapping, known gaps, and open items requiring compliance sign-off is maintained separately as `COMPLIANCE.md`. Organisations using MongoCustody in a regulated environment should review and adapt that document — substituting their own system name, database names, and Azure resource identifiers — before submitting for compliance review.

### No metadata database required

All backup metadata lives in `manifest.json` files in Blob Storage. There is no separate database dependency for backup or restore operations. If the source database is completely lost, restore proceeds directly from Blob Storage without any external service.

---

## Storage Layout

```
[backup-container]/                       ← WORM-enabled Blob Storage container
│
├── full/
│   └── {year}/{quarter}/{backupId}/
│       ├── {db}.{collection}.part-001.bson.gz   ← chunk 1 of N, BSON format
│       ├── {db}.{collection}.part-002.bson.gz   ← chunk 2 of N (large collections)
│       ├── ...
│       └── manifest.json                        ← completion certificate; absent = failed
│
├── incremental/
│   └── {year}/{quarter}/{fullBackupId}/
│       └── {yyyy-MM-ddTHHmmZ}/                  ← ISO 8601 UTC e.g. 2025-01-03T0100Z
│           ├── {db}.{collection}.part-001.bson.gz
│           ├── ...
│           └── manifest.json
│
└── checkpoint/
    └── {year}/{quarter}/{fullBackupId}/
        └── week-{nn}T{HHmmZ}/                   ← e.g. week-02T0200Z
            ├── {db}.{collection}.part-001.bson.gz
            ├── ...
            └── manifest.json
```

All backup artifacts — chunks and manifests — are stored under a single container with WORM time-based retention. A folder containing chunks but no `manifest.json` represents a failed backup; a periodic cleanup process removes orphaned chunks older than 24 hours. Chunks must always be applied in ascending `part-NNN` order during restore. Lifecycle management policies tier blobs from Hot to Archive storage automatically based on age:

| Age | Storage tier |
|---|---|
| 0–12 months | Hot |
| 12+ months | Archive |
| 6 years + buffer | Deleted |

---

## Development

### Clone and build

```bash
git clone https://github.com/your-org/mongocustody.git
cd mongocustody
dotnet build
```

### Run unit tests (no infrastructure required)

```bash
dotnet test tests/MongoCustody.Core.Tests
```

### Run integration tests

Integration tests require Azurite (Azure Blob Storage emulator) and MongoDB. The easiest way is Docker Compose:

```bash
# Start Azurite and MongoDB
docker compose -f tests/docker-compose.yml up -d

# PowerShell
$env:MONGOCUSTODY_INTEGRATION_TESTS = "1"
dotnet test tests/MongoCustody.Connectors.Tests --filter "Category=Integration"

# bash / zsh
MONGOCUSTODY_INTEGRATION_TESTS=1 dotnet test tests/MongoCustody.Connectors.Tests --filter "Category=Integration"

# Tear down when done
docker compose -f tests/docker-compose.yml down
```

Without the environment variable, integration tests are automatically marked **Inconclusive** (not failed), so CI pipelines that have no emulators stay green.

If you prefer to manage the processes yourself:

```bash
# Azurite — available as an npm package or standalone binary
azurite --blobHost 127.0.0.1 --blobPort 10000 --loose --silent --location ./tmp/azurite

# MongoDB — any 4.0+ instance on localhost:27017 is sufficient
mongod --dbpath ./tmp/mongo
```

---

## Testing

| Project | Type | Infrastructure |
|---|---|---|
| `MongoCustody.Core.Tests` | Unit — mocked connectors | None |
| `MongoCustody.Connectors.Tests` | Integration — real I/O | Azurite (Blob Storage) + MongoDB |

Test framework: **MSTest v3** with source generators.

Core logic tests (backup algorithm, restore chain resolution, validation rules, checkpoint merge deduplication) use mocked `IBackupSourceConnector`, `IStorageConnector`, and `IMetadataRepository`. They run in milliseconds with no external dependencies.

Connector integration tests verify health checks, read/write round-trips, and end-to-end backup → validate → restore flows against real emulated infrastructure. They are gated by the `MONGOCUSTODY_INTEGRATION_TESTS` environment variable and tagged `Category=Integration`, so they never run in standard CI unless explicitly opted in.

`BlobMetadataRepository` stores all backup and restore records as `manifest.json` files inside the same Blob Storage container used for backup artifacts. There is no separate metadata database. Lookup is done through a lightweight `_index/{backupId}` pointer blob written alongside each manifest.

---

## Implementation Phases

The library is being built in the following sequence. Each phase is independently releasable.

| Phase | Scope | Status |
|---|---|---|
| 1 | Models, abstractions, and configuration | ✅ Complete |
| 2 | Connectors and health checks | ✅ Complete |
| 3 | Full backup engine | ✅ Complete |
| 4 | Validation engine (L1 structural integrity, L2 count consistency, L3 incremental completeness) | ✅ Complete |
| 5 | Full backup restore engine (restore chain replay; L4 restore dry-run validation) | ✅ Complete |
| 6 | Incremental backup engine | Planned |
| 7 | Incremental restore and validation | Planned |
| 8 | Checkpoint engine | Planned |
| 9 | DI wiring and public API | Planned |
| 10 | CLI tool (`health`, `backup full`, `restore`, `backups list`, `validate`, `report`); live progress bar for backup and restore; HIPAA audit report generation | ✅ Complete |

---

## Requirements

- .NET 10 SDK or later
- MongoDB 4.0+ or Azure Cosmos DB for MongoDB API (RU-based)
- Azure Blob Storage (for backup artifact and metadata storage)
- `DefaultAzureCredential`-compatible identity for production deployments (managed identity, workload identity, or `az login` for local development)

---

## License

MIT License

Copyright (c) 2026 MongoCustody Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

---

## Contributing

[To be determined]
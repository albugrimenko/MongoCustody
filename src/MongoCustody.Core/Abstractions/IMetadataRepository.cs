using MongoCustody.Core.Models;

namespace MongoCustody.Core.Abstractions;

/// <summary>
/// Abstracts read and write access to the backup metadata store.
/// Metadata is stored as blob-resident <c>manifest.json</c> files;
/// implementations may additionally maintain a queryable index
/// (e.g., a Cosmos DB collection) for fast chain resolution.
/// </summary>
public interface IMetadataRepository
{
    /// <summary>
    /// Persists a completed <see cref="BackupRecord"/>.
    /// Called by the engine after a successful backup seal.
    /// </summary>
    /// <param name="record">The record to save.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveBackupRecordAsync(BackupRecord record, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a <see cref="BackupRecord"/> by its unique identifier.
    /// Returns <c>null</c> if not found.
    /// </summary>
    Task<BackupRecord?> GetBackupRecordAsync(
        string backupId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the most recently completed backup of the specified type
    /// for the given database, or <c>null</c> if none exists.
    /// Used by the incremental engine to resolve the previous
    /// <c>anchorTs</c>.
    /// </summary>
    Task<BackupRecord?> GetLatestBackupAsync(
        string databaseName,
        BackupType backupType,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent full backup for the given database that
    /// falls within the specified quarter label (e.g., <c>2025-Q1</c>).
    /// Returns <c>null</c> if no full backup exists for that quarter.
    /// </summary>
    Task<BackupRecord?> GetFullBackupForQuarterAsync(
        string databaseName,
        string quarterLabel,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves the ordered restore chain for the given database and
    /// target timestamp. Returns the list of backup folder paths to apply,
    /// from oldest (full backup) to newest (final incremental or checkpoint),
    /// with a maximum depth of 19.
    /// </summary>
    /// <param name="databaseName">Source database name.</param>
    /// <param name="targetDate">The point in time to restore to.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<BackupRecord>> ResolveRestoreChainAsync(
        string databaseName,
        DateTimeOffset targetDate,
        CancellationToken ct = default);

    /// <summary>
    /// Persists a <see cref="RestoreRecord"/> before any data is written
    /// to the restore target. This is the pre-restore audit entry.
    /// </summary>
    Task SaveRestoreRecordAsync(RestoreRecord record, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing <see cref="RestoreRecord"/> with the final
    /// status and completion timestamp after the restore finishes.
    /// </summary>
    Task UpdateRestoreRecordAsync(RestoreRecord record, CancellationToken ct = default);

    /// <summary>
    /// Returns all full <see cref="BackupRecord"/>s whose
    /// <see cref="BackupRecord.CompletedAtUtc"/> falls within
    /// [<paramref name="from"/>, <paramref name="to"/>] inclusive.
    /// When <paramref name="databaseName"/> is <c>null</c>, records for all
    /// databases are returned. Results are ordered oldest-first.
    /// </summary>
    Task<IReadOnlyList<BackupRecord>> ListFullBackupsAsync(
        string? databaseName,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all completed backups of the specified <paramref name="types"/>
    /// whose <see cref="BackupRecord.CompletedAtUtc"/> falls within
    /// [<paramref name="from"/>, <paramref name="to"/>] inclusive.
    /// When <paramref name="databaseName"/> is <c>null</c>, records for all
    /// databases are returned. Results are ordered oldest-first.
    /// Pass <c>null</c> for <paramref name="types"/> to return all types.
    /// </summary>
    Task<IReadOnlyList<BackupRecord>> ListAllBackupsAsync(
        string? databaseName,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyCollection<BackupType>? types = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all <see cref="RestoreRecord"/>s whose
    /// <see cref="RestoreRecord.RequestedAtUtc"/> falls within
    /// [<paramref name="from"/>, <paramref name="to"/>] inclusive.
    /// Results are ordered oldest-first.
    /// </summary>
    Task<IReadOnlyList<RestoreRecord>> ListRestoreRecordsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves a single <see cref="RestoreRecord"/> by its restore ID.
    /// Returns <c>null</c> if not found.
    /// </summary>
    Task<RestoreRecord?> GetRestoreRecordAsync(
        string restoreId,
        CancellationToken ct = default);

    /// <summary>
    /// Counts incremental backups for <paramref name="databaseName"/> whose
    /// <c>completedAtUtc</c> is after <paramref name="chainRootCompletedAt"/>,
    /// searching the secondary index for the given quarter. Uses folder-name
    /// timestamps — no manifest or entry reads.
    /// </summary>
    Task<int> CountIncrementalsSinceAsync(
        string databaseName,
        int year,
        int quarter,
        DateTimeOffset chainRootCompletedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Returns <c>true</c> if the secondary index contains at least one entry.
    /// Used by CLI commands to detect missing index and suggest a rebuild.
    /// </summary>
    Task<bool> IsSecondaryIndexPopulatedAsync(CancellationToken ct = default);

    /// <summary>
    /// Reads a <see cref="BackupRecord"/> manifest directly by folder path.
    /// Used during audit report generation to annotate restore chain steps
    /// with backup type, date, and document counts.
    /// Returns <c>null</c> if the manifest is absent (failed or incomplete backup).
    /// </summary>
    Task<BackupRecord?> GetBackupRecordByFolderAsync(
        string folderPath,
        CancellationToken ct = default);

    /// <summary>
    /// Validates the secondary index for consistency: checks for dangling index
    /// entries (no manifest) and unindexed manifests (no index entry). Non-destructive.
    /// </summary>
    Task<Models.IndexValidationResult> ValidateIndexAsync(CancellationToken ct = default);

    /// <summary>
    /// Rebuilds the secondary index from manifests. Adds missing entries and removes
    /// orphaned entries. When <paramref name="dryRun"/> is <c>true</c>, reports what
    /// would change without writing anything.
    /// </summary>
    Task<Models.IndexRebuildResult> RebuildIndexAsync(
        bool dryRun = false,
        CancellationToken ct = default);

    /// <summary>
    /// Verifies the connector can reach the metadata store.
    /// Returns <c>true</c> and an optional latency if reachable.
    /// </summary>
    Task<(bool IsHealthy, long LatencyMs, string? ErrorMessage)> CheckHealthAsync(
        CancellationToken ct = default);
}

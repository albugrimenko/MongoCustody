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
    /// Verifies the connector can reach the metadata store.
    /// Returns <c>true</c> and an optional latency if reachable.
    /// </summary>
    Task<(bool IsHealthy, long LatencyMs, string? ErrorMessage)> CheckHealthAsync(
        CancellationToken ct = default);
}

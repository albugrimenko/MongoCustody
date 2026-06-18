using MongoCustody.Core.Models;

namespace MongoCustody.Core.Abstractions;

/// <summary>
/// The single public façade for all MongoCustody operations.
/// Register via <c>IServiceCollection.AddMongoCustody(config)</c>.
/// </summary>
public interface IMongoCustodyService
{
    /// <summary>
    /// Verifies all configured connectors (MongoDB source, Blob Storage,
    /// metadata repository, secret provider) are reachable and operational.
    /// Should be called before starting any backup or restore operation.
    /// </summary>
    Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs a full backup — a complete export of all collections in the
    /// requested databases. Starts a new incremental chain.
    /// </summary>
    Task<BackupResult> RunFullBackupAsync(
        BackupRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Runs a daily incremental backup — exports all documents changed
    /// since the previous backup's <c>anchorTs</c>, using the server-managed
    /// <c>_ts</c> field with the configured overlap buffer.
    /// </summary>
    Task<BackupResult> RunIncrementalBackupAsync(
        BackupRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Generates a weekly checkpoint — a deduplicated merge of the six
    /// preceding daily incrementals. Bounds the restore chain depth.
    /// Requires all six source incrementals to be in
    /// <see cref="BackupStatus.Completed"/> status before proceeding.
    /// </summary>
    Task<BackupResult> RunCheckpointAsync(
        BackupRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Restores a database to a point in time by applying the resolved
    /// restore chain (full + checkpoints + incrementals) against the
    /// provided target account.
    /// A <see cref="RestoreRecord"/> is written before any data is written.
    /// </summary>
    Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Validates a backup at one or more levels without writing any data.
    /// </summary>
    Task<ValidationResult> ValidateBackupAsync(
        ValidationRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all completed full backups for the given database whose
    /// <c>CompletedAtUtc</c> falls within [<paramref name="from"/>,
    /// <paramref name="to"/>] inclusive, ordered oldest-first.
    /// </summary>
    Task<IReadOnlyList<BackupRecord>> ListFullBackupsAsync(
        string databaseName,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves and returns the ordered restore chain for a target date
    /// without performing any data operations (dry-run / preview).
    /// </summary>
    /// <param name="databaseName">Database to restore.</param>
    /// <param name="targetDate">Target point in time.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Ordered list of <see cref="BackupRecord"/> entries that would be
    /// applied, from oldest to newest. Maximum 19 entries.
    /// </returns>
    Task<IReadOnlyList<BackupRecord>> ResolveRestoreChainAsync(
        string databaseName,
        DateTimeOffset targetDate,
        CancellationToken ct = default);
}

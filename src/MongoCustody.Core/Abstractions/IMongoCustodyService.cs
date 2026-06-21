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
    /// <param name="progress">
    /// Optional progress sink. Receives one <see cref="Models.OperationProgress"/>
    /// report after each collection completes, enabling callers to render a
    /// live progress indicator. Pass <c>null</c> to suppress reporting.
    /// </param>
    Task<BackupResult> RunFullBackupAsync(
        BackupRequest request,
        IProgress<Models.OperationProgress>? progress = null,
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
    /// <param name="progress">
    /// Optional progress sink. Receives one <see cref="Models.OperationProgress"/>
    /// report after each collection step completes. The total reflects all
    /// collection operations across every step in the restore chain.
    /// Pass <c>null</c> to suppress reporting.
    /// </param>
    Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        IProgress<Models.OperationProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Validates a backup at one or more levels without writing any data.
    /// </summary>
    Task<ValidationResult> ValidateBackupAsync(
        ValidationRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all completed full backups whose <c>CompletedAtUtc</c> falls
    /// within [<paramref name="from"/>, <paramref name="to"/>] inclusive,
    /// ordered oldest-first. When <paramref name="databaseName"/> is
    /// <c>null</c>, backups for all databases are returned.
    /// </summary>
    Task<IReadOnlyList<BackupRecord>> ListFullBackupsAsync(
        string? databaseName,
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

    /// <summary>
    /// Assembles an audit report for the specified date range or a single
    /// restore operation. The returned <see cref="Models.AuditReport"/> is a
    /// structured object — the caller is responsible for rendering and
    /// persisting it (e.g., as a Markdown file).
    /// </summary>
    Task<Models.AuditReport> GenerateAuditReportAsync(
        Models.AuditReportRequest request,
        CancellationToken ct = default);
}

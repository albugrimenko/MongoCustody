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
        IProgress<Models.OperationProgress>? progress = null,
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
    /// Returns all completed backups of the specified <paramref name="types"/>
    /// whose <see cref="Models.BackupRecord.CompletedAtUtc"/> falls within
    /// [<paramref name="from"/>, <paramref name="to"/>] inclusive, ordered
    /// oldest-first. When <paramref name="databaseName"/> is <c>null</c>,
    /// backups for all databases are returned. Pass <c>null</c> for
    /// <paramref name="types"/> to return all backup types.
    /// </summary>
    Task<IReadOnlyList<Models.BackupRecord>> ListAllBackupsAsync(
        string? databaseName,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyCollection<Models.BackupType>? types = null,
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
    /// Resolves the restore chain and returns a preview with the best
    /// available date and a gap warning when it exceeds 1 day.
    /// Read-only — no data is written.
    /// </summary>
    Task<Models.RestoreChainPreview> PreviewRestoreChainAsync(
        string databaseName,
        DateTimeOffset targetDate,
        CancellationToken ct = default);

    /// <summary>
    /// Runs a full restore dry-run cycle: restore → verify counts → clean up.
    /// The <see cref="Models.RestoreRecord"/> written to storage has
    /// <c>IsDryRun = true</c> and is included in date-range audit reports.
    /// <para>
    /// When <see cref="Models.RestoreRequest.TargetConnectionString"/> is blank,
    /// the service automatically resolves it from
    /// <see cref="Models.MongoCustodyConfiguration.DryRun"/> using the configured
    /// <see cref="ISecretProvider"/>. <see cref="Models.RestoreRequest.TargetDatabaseName"/>
    /// is similarly filled from <see cref="Models.DryRunConfiguration.DatabaseName"/>
    /// when blank. This lets callers omit both fields and rely entirely on the
    /// configuration supplied at construction time.
    /// </para>
    /// </summary>
    Task<RestoreResult> RunDryRunAsync(
        RestoreRequest request,
        IProgress<Models.OperationProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves the restore chain for <paramref name="databaseName"/> as of
    /// <paramref name="targetDate"/> and runs per-step validation at the
    /// requested <paramref name="levels"/>. Returns one result per chain step,
    /// ordered oldest-first (full backup first).
    /// </summary>
    Task<Models.ChainValidationResult> ValidateRestoreChainAsync(
        string databaseName,
        DateTimeOffset targetDate,
        ValidationLevel levels,
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

    /// <summary>
    /// Validates the secondary metadata index for consistency.
    /// Non-destructive — safe to run on a live system.
    /// </summary>
    Task<Models.IndexValidationResult> ValidateIndexAsync(CancellationToken ct = default);

    /// <summary>
    /// Rebuilds the secondary metadata index from manifests.
    /// When <paramref name="dryRun"/> is <c>true</c>, reports what would change
    /// without writing anything.
    /// </summary>
    Task<Models.IndexRebuildResult> RebuildIndexAsync(
        bool dryRun = false,
        CancellationToken ct = default);

    /// <summary>
    /// Returns <c>true</c> if the secondary index contains at least one entry.
    /// </summary>
    Task<bool> IsSecondaryIndexPopulatedAsync(CancellationToken ct = default);
}

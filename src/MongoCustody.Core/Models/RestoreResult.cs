namespace MongoCustody.Core.Models;

/// <summary>
/// The result of a restore operation.
/// </summary>
public sealed class RestoreResult
{
    /// <summary>Terminal status of the restore operation.</summary>
    public BackupStatus Status { get; set; }

    /// <summary>UTC time the restore operation started.</summary>
    public DateTimeOffset StartedAtUtc { get; set; }

    /// <summary>UTC time the restore operation completed or failed.</summary>
    public DateTimeOffset CompletedAtUtc { get; set; }

    /// <summary>The target point-in-time that was restored to.</summary>
    public DateTimeOffset TargetDate { get; set; }

    /// <summary>
    /// Ordered list of backup folder paths applied during the restore,
    /// from oldest (full backup) to newest (final incremental or checkpoint).
    /// Maximum length: 19 entries.
    /// </summary>
    public List<string> RestoreChain { get; set; } = [];

    /// <summary>Per-collection restore summary.</summary>
    public List<CollectionRestoreResult> CollectionResults { get; set; } = [];

    /// <summary>
    /// Human-readable failure description when
    /// <see cref="Status"/> is <see cref="BackupStatus.Failed"/>.
    /// Never contains PHI.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Correlation ID linking this result to the originating
    /// <see cref="RestoreRequest"/>.
    /// </summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// Unique identifier of the <see cref="RestoreRecord"/> written to
    /// storage. Used by dry-run cleanup to patch the audit record after
    /// the restore engine returns.
    /// </summary>
    public string RestoreId { get; set; } = string.Empty;
}

/// <summary>
/// Per-collection summary within a <see cref="RestoreResult"/>.
/// </summary>
public sealed class CollectionRestoreResult
{
    /// <summary>Name of the collection.</summary>
    public string CollectionName { get; set; } = string.Empty;

    /// <summary>Total documents upserted into the restore target.</summary>
    public long DocumentsUpserted { get; set; }

    /// <summary>Terminal status for this collection.</summary>
    public BackupStatus Status { get; set; }

    /// <summary>Failure detail if this collection failed to restore.</summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Non-fatal warning about this collection's restore — e.g. the backup
    /// recorded a shard key that could not be applied because the restore
    /// target is a standard MongoDB instance rather than Cosmos DB for
    /// MongoDB. <see cref="Status"/> remains <see cref="BackupStatus.Completed"/>
    /// when this is set.
    /// </summary>
    public string? Warning { get; set; }
}

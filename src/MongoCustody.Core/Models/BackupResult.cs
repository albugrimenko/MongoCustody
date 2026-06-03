namespace MongoCustody.Core.Models;

/// <summary>
/// The result of a completed backup operation.
/// </summary>
public sealed class BackupResult
{
    /// <summary>The unique identifier assigned to this backup run.</summary>
    public string BackupId { get; set; } = string.Empty;

    /// <summary>The type of backup that was performed.</summary>
    public BackupType BackupType { get; set; }

    /// <summary>Terminal status of the backup operation.</summary>
    public BackupStatus Status { get; set; }

    /// <summary>UTC time the backup operation started.</summary>
    public DateTimeOffset StartedAtUtc { get; set; }

    /// <summary>UTC time the backup operation completed or failed.</summary>
    public DateTimeOffset CompletedAtUtc { get; set; }

    /// <summary>
    /// Human-readable failure description when
    /// <see cref="Status"/> is <see cref="BackupStatus.Failed"/>.
    /// Never contains PHI.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Per-database results for this backup run.
    /// </summary>
    public List<DatabaseBackupResult> DatabaseResults { get; set; } = [];

    /// <summary>
    /// Correlation ID linking this result to the originating
    /// <see cref="BackupRequest"/>.
    /// </summary>
    public string CorrelationId { get; set; } = string.Empty;
}

/// <summary>
/// Per-database summary within a <see cref="BackupResult"/>.
/// </summary>
public sealed class DatabaseBackupResult
{
    /// <summary>Name of the database.</summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>Terminal status for this database within the backup run.</summary>
    public BackupStatus Status { get; set; }

    /// <summary>Total documents exported across all collections.</summary>
    public long TotalDocuments { get; set; }

    /// <summary>Total compressed bytes written to blob storage.</summary>
    public long TotalCompressedBytes { get; set; }

    /// <summary>Number of collections exported.</summary>
    public int CollectionCount { get; set; }

    /// <summary>
    /// Human-readable failure description when
    /// <see cref="Status"/> is <see cref="BackupStatus.Failed"/>.
    /// </summary>
    public string? FailureReason { get; set; }
}

using System.Text.Json.Serialization;

namespace MongoCustody.Core.Models;

/// <summary>
/// Fully assembled audit report returned by <c>GenerateAuditReportAsync</c>.
/// The CLI renders this to a Markdown file; library consumers can serialize
/// it directly.
/// </summary>
public sealed class AuditReport
{
    /// <summary>Start of the period covered by this report.</summary>
    [JsonPropertyName("from")]
    public DateTimeOffset From { get; init; }

    /// <summary>End of the period covered by this report.</summary>
    [JsonPropertyName("to")]
    public DateTimeOffset To { get; init; }

    /// <summary>
    /// Database filter applied to the report, or <c>null</c> when all
    /// databases are included.
    /// </summary>
    [JsonPropertyName("databaseFilter")]
    public string? DatabaseFilter { get; init; }

    /// <summary>
    /// <c>true</c> when the report covers a single restore operation
    /// rather than a date range.
    /// </summary>
    [JsonPropertyName("isSingleRestoreMode")]
    public bool IsSingleRestoreMode { get; init; }

    /// <summary>UTC timestamp when the report was generated.</summary>
    [JsonPropertyName("generatedAtUtc")]
    public DateTimeOffset GeneratedAtUtc { get; init; }

    /// <summary>Identity that requested the report.</summary>
    [JsonPropertyName("generatedBy")]
    public string GeneratedBy { get; init; } = string.Empty;

    /// <summary>Aggregate counts and document totals.</summary>
    [JsonPropertyName("summary")]
    public AuditSummary Summary { get; init; } = new();

    /// <summary>
    /// Full backup records in chronological order. Empty in single-restore mode.
    /// </summary>
    [JsonPropertyName("backups")]
    public IReadOnlyList<BackupRecord> Backups { get; init; } = [];

    /// <summary>Restore operations in chronological order.</summary>
    [JsonPropertyName("restores")]
    public IReadOnlyList<RestoreAuditEntry> Restores { get; init; } = [];
}

/// <summary>Aggregate metrics across the report period.</summary>
public sealed class AuditSummary
{
    [JsonPropertyName("backupsCompleted")]
    public int BackupsCompleted { get; init; }

    [JsonPropertyName("backupsFailed")]
    public int BackupsFailed { get; init; }

    [JsonPropertyName("restoresCompleted")]
    public int RestoresCompleted { get; init; }

    [JsonPropertyName("restoresFailed")]
    public int RestoresFailed { get; init; }

    [JsonPropertyName("totalCollectionsBacked")]
    public int TotalCollectionsBacked { get; init; }

    [JsonPropertyName("totalDocumentsBacked")]
    public long TotalDocumentsBacked { get; init; }
}

/// <summary>
/// A single restore operation enriched with the backup chain that was
/// applied to reach the requested point in time.
/// </summary>
public sealed class RestoreAuditEntry
{
    /// <summary>Raw restore record as written to storage.</summary>
    [JsonPropertyName("record")]
    public RestoreRecord Record { get; init; } = null!;

    /// <summary>
    /// Ordered list of backups applied during this restore, oldest first.
    /// Empty if the restore failed before the chain was resolved.
    /// </summary>
    [JsonPropertyName("chainSteps")]
    public IReadOnlyList<RestoreChainStep> ChainSteps { get; init; } = [];
}

/// <summary>
/// One backup applied during a restore operation.
/// </summary>
public sealed class RestoreChainStep
{
    /// <summary>1-based position in the restore sequence.</summary>
    [JsonPropertyName("stepNumber")]
    public int StepNumber { get; init; }

    [JsonPropertyName("backupType")]
    public BackupType BackupType { get; init; }

    /// <summary>UTC timestamp of the backup.</summary>
    [JsonPropertyName("backupDate")]
    public DateTimeOffset BackupDate { get; init; }

    /// <summary>Storage folder path (relative to container root).</summary>
    [JsonPropertyName("backupFolder")]
    public string BackupFolder { get; init; } = string.Empty;

    /// <summary>
    /// Documents written in this step. For the full backup this is the
    /// baseline insert count; for incrementals and checkpoints it is the
    /// upsert count.
    /// </summary>
    [JsonPropertyName("documentsWritten")]
    public long DocumentsWritten { get; init; }

    /// <summary>
    /// <c>true</c> for the first (Full) step, <c>false</c> for all others.
    /// Determines whether "inserted" or "upserted" appears in the narrative.
    /// </summary>
    [JsonPropertyName("isBaselineInsert")]
    public bool IsBaselineInsert { get; init; }

    /// <summary>
    /// <c>null</c> when the backup manifest could not be read from storage
    /// (e.g., failed backup or corrupt manifest).
    /// </summary>
    [JsonPropertyName("backupId")]
    public string? BackupId { get; init; }
}

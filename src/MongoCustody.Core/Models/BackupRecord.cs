using System.Text.Json.Serialization;

namespace MongoCustody.Core.Models;

/// <summary>
/// The completion certificate for a backup operation, serialised as
/// <c>manifest.json</c> in the backup folder in Blob Storage.
/// <para>
/// The manifest is written as the absolute last step of a successful
/// backup. Its presence in a folder is an unconditional guarantee that
/// the backup completed successfully. A folder that contains chunk blobs
/// but no <c>manifest.json</c> represents a failed or incomplete backup.
/// </para>
/// <para>
/// The manifest contains no PHI — only collection names, document counts,
/// hashes, timestamps, and structural metadata.
/// </para>
/// </summary>
public sealed class BackupRecord
{
    /// <summary>
    /// Schema version for forward-compatibility detection.
    /// Current value: 1.
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Unique identifier for this backup operation.</summary>
    [JsonPropertyName("backupId")]
    public string BackupId { get; set; } = string.Empty;

    /// <summary>The type of backup this manifest describes.</summary>
    [JsonPropertyName("backupType")]
    public BackupType BackupType { get; set; }

    /// <summary>
    /// Blob folder path of this backup, relative to the container root.
    /// Example: <c>full/2025/Q1/a3f8c2d1-...</c>
    /// </summary>
    [JsonPropertyName("backupFolder")]
    public string BackupFolder { get; set; } = string.Empty;

    /// <summary>
    /// Blob folder path of the preceding backup in the incremental chain.
    /// <c>null</c> for full backups (they start a new chain).
    /// </summary>
    [JsonPropertyName("parentFolder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentFolder { get; set; }

    /// <summary>
    /// Ordered list of source incremental folder paths merged to produce
    /// this checkpoint. Populated only for
    /// <see cref="BackupType.Checkpoint"/>; empty for all other types.
    /// </summary>
    [JsonPropertyName("sourceIncrementalFolders")]
    public List<string> SourceIncrementalFolders { get; set; } = [];

    /// <summary>UTC time the backup operation started.</summary>
    [JsonPropertyName("startedAtUtc")]
    public DateTimeOffset StartedAtUtc { get; set; }

    /// <summary>UTC time the backup operation completed and the manifest was written.</summary>
    [JsonPropertyName("completedAtUtc")]
    public DateTimeOffset CompletedAtUtc { get; set; }

    /// <summary>
    /// Lower bound of the <c>_ts</c> query used to identify changed
    /// documents, including the 5-minute conservative overlap buffer.
    /// <c>null</c> for full backups (which perform a full collection scan).
    /// Unix timestamp (seconds since epoch).
    /// </summary>
    [JsonPropertyName("queryFromTs")]
    public long? QueryFromTs { get; set; }

    /// <summary>
    /// Server timestamp captured at the end of this backup's execution
    /// window. Becomes the <c>queryFromTs</c> baseline (before the overlap
    /// buffer is applied) for the next incremental backup.
    /// Unix timestamp (seconds since epoch).
    /// </summary>
    [JsonPropertyName("anchorTs")]
    public long AnchorTs { get; set; }

    /// <summary>Name of the MongoDB database backed up.</summary>
    [JsonPropertyName("databaseName")]
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// Calendar quarter label for grouping in storage.
    /// Example: <c>2025-Q1</c>
    /// </summary>
    [JsonPropertyName("quarterLabel")]
    public string QuarterLabel { get; set; } = string.Empty;

    /// <summary>
    /// 1-based week number within the quarter (1–13).
    /// Populated for <see cref="BackupType.Checkpoint"/> only;
    /// <c>null</c> for all other types.
    /// </summary>
    [JsonPropertyName("weekNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WeekNumber { get; set; }

    /// <summary>Serialisation format of the chunk blobs.</summary>
    [JsonPropertyName("format")]
    public BackupFormat Format { get; set; }

    /// <summary>
    /// Metadata for each collection included in this backup operation.
    /// Collections with no changes during an incremental run are absent
    /// from this list — the absence is correctly interpreted as
    /// "no changes" during restore, not as an error.
    /// </summary>
    [JsonPropertyName("collections")]
    public List<CollectionBackupRecord> Collections { get; set; } = [];

    /// <summary>
    /// Identity or label of the process or principal that triggered
    /// this backup (e.g., <c>"scheduler"</c>).
    /// </summary>
    [JsonPropertyName("triggeredBy")]
    public string TriggeredBy { get; set; } = string.Empty;

    /// <summary>
    /// Correlation ID linking this manifest to the originating request
    /// and to any associated log entries.
    /// </summary>
    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;
}

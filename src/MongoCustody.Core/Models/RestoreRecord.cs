using System.Text.Json.Serialization;

namespace MongoCustody.Core.Models;

/// <summary>
/// Pre-restore audit record written to
/// <c>restore-request.json</c> in the restore folder in Blob Storage
/// <b>before any data is written</b> to the target account.
/// <para>
/// This record satisfies HIPAA §164.312(b) (audit controls) and
/// §164.308(a)(7)(ii)(B) (disaster recovery documentation).
/// It is written regardless of whether the restore ultimately succeeds
/// or fails.
/// </para>
/// </summary>
public sealed class RestoreRecord
{
    /// <summary>Unique identifier for this restore operation.</summary>
    [JsonPropertyName("restoreId")]
    public string RestoreId { get; set; } = string.Empty;

    /// <summary>UTC time the restore request was received and recorded.</summary>
    [JsonPropertyName("requestedAtUtc")]
    public DateTimeOffset RequestedAtUtc { get; set; }

    /// <summary>UTC time the restore operation completed or failed.</summary>
    [JsonPropertyName("completedAtUtc")]
    public DateTimeOffset? CompletedAtUtc { get; set; }

    /// <summary>Terminal status of the restore operation.</summary>
    [JsonPropertyName("status")]
    public BackupStatus Status { get; set; }

    /// <summary>Name of the source database restored.</summary>
    [JsonPropertyName("databaseName")]
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// Name of the target database the data was restored into.
    /// May differ from <see cref="DatabaseName"/> when restoring to a
    /// differently-named database within an existing instance.
    /// </summary>
    [JsonPropertyName("targetDatabaseName")]
    public string TargetDatabaseName { get; set; } = string.Empty;

    /// <summary>The point in time that was restored to.</summary>
    [JsonPropertyName("targetDate")]
    public DateTimeOffset TargetDate { get; set; }

    /// <summary>
    /// Ordered list of backup folder paths applied during the restore,
    /// from oldest (full backup) to newest (final incremental or checkpoint).
    /// </summary>
    [JsonPropertyName("restoreChain")]
    public List<string> RestoreChain { get; set; } = [];

    /// <summary>
    /// Optional subset of collections that were restored.
    /// Empty list means all collections were restored.
    /// </summary>
    [JsonPropertyName("collections")]
    public List<string> Collections { get; set; } = [];

    /// <summary>
    /// Identity of the principal who requested the restore.
    /// </summary>
    [JsonPropertyName("requestedBy")]
    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>
    /// Identity of the principal who approved the restore.
    /// Two-person sign-off is required per the HIPAA access control policy.
    /// </summary>
    [JsonPropertyName("approvedBy")]
    public string ApprovedBy { get; set; } = string.Empty;

    /// <summary>Human-readable reason for the restore.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Failure description when <see cref="Status"/> is
    /// <see cref="BackupStatus.Failed"/>. Never contains PHI.
    /// </summary>
    [JsonPropertyName("failureReason")]
    public string? FailureReason { get; set; }

    /// <summary>
    /// Correlation ID linking this record to the originating
    /// <see cref="RestoreRequest"/> and associated log entries.
    /// </summary>
    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;
}

namespace MongoCustody.Core.Models;

/// <summary>
/// Parameters for a point-in-time restore operation.
/// </summary>
public sealed class RestoreRequest
{
    /// <summary>
    /// Name of the source database to restore.
    /// </summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// The point in time to restore to. The engine will resolve the
    /// restore chain (full + checkpoints + incrementals) that covers
    /// this timestamp.
    /// </summary>
    public DateTimeOffset TargetDate { get; set; }

    /// <summary>
    /// MongoDB connection string for the target MongoDB instance.
    /// The instance must be reachable and the database named by
    /// <see cref="TargetDatabaseName"/> must exist and be empty.
    /// NEVER use the production source connection string here.
    /// </summary>
    public string TargetConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Name of the database in the target instance to restore into.
    /// The database must already exist (be provisioned) and must contain
    /// no collections. MongoCustody will not create or provision the database
    /// — this is intentional, as databases sometimes require special
    /// provisioning (throughput settings, Cosmos DB options, etc.).
    /// </summary>
    public string TargetDatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// Optional subset of collection names to restore.
    /// When empty, all collections in the backup chain are restored.
    /// </summary>
    public List<string> Collections { get; set; } = [];

    /// <summary>
    /// Identity of the principal requesting the restore.
    /// Recorded in <c>restore-request.json</c> before any data is written.
    /// </summary>
    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>
    /// Identity of the principal who approved the restore.
    /// Two-person sign-off is required per the HIPAA access control policy.
    /// Recorded in <c>restore-request.json</c> before any data is written.
    /// </summary>
    public string ApprovedBy { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable reason for the restore (e.g., incident reference).
    /// Recorded in <c>restore-request.json</c> for audit purposes.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Optional correlation ID to link this restore to an external
    /// workflow or incident tracker. A new GUID is generated
    /// automatically if not provided.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// When <c>true</c>, the restore is treated as a dry-run validation cycle.
    /// The <see cref="RestoreRecord"/> is flagged with <c>IsDryRun = true</c>
    /// and the target database is cleaned up after the restore completes.
    /// </summary>
    public bool IsDryRun { get; set; }
}

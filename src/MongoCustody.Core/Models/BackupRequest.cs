namespace MongoCustody.Core.Models;

/// <summary>
/// Parameters for a backup operation (full, incremental, or checkpoint).
/// </summary>
public sealed class BackupRequest
{
    /// <summary>
    /// Names of the databases to include in this backup run.
    /// Must be a subset of the databases listed in
    /// <see cref="MongoCustodyConfiguration.Databases"/>.
    /// If empty, all configured databases are included.
    /// </summary>
    public List<string> Databases { get; set; } = [];

    /// <summary>
    /// Human-readable identifier of the principal or process that
    /// triggered this backup (e.g., <c>"scheduler"</c>,
    /// <c>"admin@example.com"</c>).
    /// Recorded in the <c>manifest.json</c> for audit purposes.
    /// </summary>
    public string TriggeredBy { get; set; } = string.Empty;

    /// <summary>
    /// Optional correlation ID to link this backup operation to an
    /// external workflow, incident, or job scheduler run.
    /// A new GUID is generated automatically if not provided.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Optional free-text label describing why this backup was created.
    /// Recorded in the manifest for auditing and filtering purposes.
    /// Example: <c>"pre-upgrade snapshot"</c>, <c>"before schema migration v3"</c>.
    /// </summary>
    public string? Label { get; set; }
}

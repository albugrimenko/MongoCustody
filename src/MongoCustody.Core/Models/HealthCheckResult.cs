namespace MongoCustody.Core.Models;

/// <summary>
/// Result of a health check that verifies all connectors are reachable
/// before a backup or restore operation is started.
/// </summary>
public sealed class HealthCheckResult
{
    /// <summary>
    /// <c>true</c> if all connector checks passed and the system is
    /// ready to run backup or restore operations.
    /// </summary>
    public bool IsHealthy => ComponentResults.Count > 0
        && ComponentResults.All(r => r.IsHealthy);

    /// <summary>
    /// Single-line human-readable summary, suitable for logging.
    /// </summary>
    public string Summary => IsHealthy
        ? "All components healthy."
        : $"Unhealthy: {string.Join(", ", ComponentResults.Where(r => !r.IsHealthy).Select(r => r.ComponentName))}";

    /// <summary>UTC time the health check was performed.</summary>
    public DateTimeOffset CheckedAtUtc { get; set; }

    /// <summary>Per-component health check results.</summary>
    public List<ComponentHealthResult> ComponentResults { get; set; } = [];
}

/// <summary>
/// Health check result for a single infrastructure component
/// (e.g., MongoDB source, Blob Storage, Key Vault).
/// </summary>
public sealed class ComponentHealthResult
{
    /// <summary>Human-readable component name (e.g., <c>"BlobStorage"</c>).</summary>
    public string ComponentName { get; set; } = string.Empty;

    /// <summary><c>true</c> if this component is reachable and operational.</summary>
    public bool IsHealthy { get; set; }

    /// <summary>
    /// Latency of the connectivity probe in milliseconds.
    /// <c>null</c> if the probe did not complete.
    /// </summary>
    public long? LatencyMs { get; set; }

    /// <summary>
    /// Human-readable error description when
    /// <see cref="IsHealthy"/> is <c>false</c>.
    /// Never contains PHI or secrets.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

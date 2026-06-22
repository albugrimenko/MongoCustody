namespace MongoCustody.Core.Models;

/// <summary>
/// Root configuration type for MongoCustody.
/// Bind this from the <c>MongoCustody</c> section of your
/// <c>appsettings.json</c> or from a standalone <c>mongocustody.json</c>
/// file.
/// </summary>
public sealed class MongoCustodyConfiguration
{
    /// <summary>
    /// Name of the blob container in which all backup artifacts are stored.
    /// The container must have WORM time-based retention enabled.
    /// Default: <c>backups</c>.
    /// </summary>
    public string StorageContainerName { get; set; } = "backups";

    /// <summary>
    /// One or more databases to include in backup operations.
    /// </summary>
    public List<DatabaseConfiguration> Databases { get; set; } = [];

    /// <summary>
    /// Operational policy settings (chunk size, compression, parallelism,
    /// buffer window, etc.).
    /// </summary>
    public BackupPolicy Policy { get; set; } = new();

    /// <summary>
    /// Secret provider configuration for resolving connection strings
    /// and storage keys at runtime.
    /// </summary>
    public SecretsConfiguration Secrets { get; set; } = new();

    /// <summary>
    /// Optional configuration for automated dry-run validation.
    /// When present, <c>validate dry-run</c> uses these defaults and no
    /// connection string is required on the command line.
    /// </summary>
    public DryRunConfiguration? DryRun { get; set; }
}

/// <summary>
/// Shared target database for automated restore dry-run validation.
/// A single empty database is reused sequentially across all source databases.
/// </summary>
public sealed class DryRunConfiguration
{
    /// <summary>MongoDB connection string for the dry-run target instance.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Name of the pre-provisioned empty database to restore into.
    /// Must exist; must be empty at the start of each run.
    /// </summary>
    public string DatabaseName { get; set; } = string.Empty;
}

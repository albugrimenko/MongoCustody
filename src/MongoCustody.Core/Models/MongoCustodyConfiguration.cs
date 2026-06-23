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

    /// <summary>
    /// Optional configuration for audit report generation.
    /// </summary>
    public ReportConfiguration? Report { get; set; }
}

/// <summary>
/// Configuration for audit report generation.
/// </summary>
public sealed class ReportConfiguration
{
    /// <summary>
    /// Directory where audit report files are written when <c>--output-file</c>
    /// is not specified on the command line. Must be an absolute path.
    /// </summary>
    public string DefaultOutputDirectory { get; set; } = string.Empty;
}

/// <summary>
/// Shared target database for automated restore dry-run validation.
/// A single empty database is reused sequentially across all source databases.
/// </summary>
public sealed class DryRunConfiguration
{
    /// <summary>
    /// Key used to look up the dry-run target MongoDB connection string.
    /// For EnvironmentVariables this is the env-var name; for AzureKeyVault
    /// it is the secret name.
    /// Default: <c>MONGOCUSTODY_DRY_RUN_CONNECTION_STRING</c>.
    /// </summary>
    public string ConnectionStringKey { get; set; } =
        "MONGOCUSTODY_DRY_RUN_CONNECTION_STRING";

    /// <summary>
    /// Name of the pre-provisioned empty database to restore into.
    /// Must exist; must be empty at the start of each run.
    /// </summary>
    public string DatabaseName { get; set; } = string.Empty;
}

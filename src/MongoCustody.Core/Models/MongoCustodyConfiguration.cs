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
    /// Attribute name of the database collections that contains the timestamp.
    /// This property must always be present and defined as long (Unix epoch time in milliseconds).
    /// It is used to determine which documents to include in each backup chunk based on the backup policy's buffer window.
    /// </summary>
    public const string AttributeName_ts = "ts";

    /// <summary>
    /// Attribute name of the database collections that indicates whether a document is deleted.
    /// This property must always be present and defined as boolean.
    /// </summary>
    public const string AttributeName_Deleted = "IsDeleted";


    /// <summary>
    /// Connection endpoint for the MongoDB account
    /// that hosts the source data to be backed up.
    /// Example: <c>https://your-account.mongo.cosmos.azure.com:10255</c>
    /// </summary>
    public string MongoDBAccountEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Endpoint URL of the Azure Blob Storage account used to store
    /// backup artifacts (chunks and manifests).
    /// Example: <c>https://yourbackupstorage.blob.core.windows.net</c>
    /// </summary>
    public string StorageAccountEndpoint { get; set; } = string.Empty;

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
}

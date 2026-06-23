namespace MongoCustody.Core.Models;

/// <summary>
/// Per-database backup scope configuration.
/// </summary>
public sealed class DatabaseConfiguration
{
    /// <summary>
    /// The name of the MongoDB database to back up.
    /// </summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// Collection names to exclude from all backup operations.
    /// Useful for ephemeral or session collections that do not require
    /// point-in-time recovery.
    /// </summary>
    public List<string> ExcludedCollections { get; set; } = [];

    /// <summary>
    /// Name of the document field that holds the last-modified Unix timestamp
    /// (seconds since epoch). MongoCustody uses this field to identify changed
    /// documents during incremental backups.
    /// Override when your collections use a different field name (e.g. <c>"updatedTs"</c>).
    /// Default: <c>"_ts"</c> (the Cosmos DB system-managed timestamp field).
    /// </summary>
    public string TimestampAttribute { get; set; } = "_ts";

}

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
}

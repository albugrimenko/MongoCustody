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

    /// <summary>
    /// Name of the shard (partition) key field used by every collection in
    /// this database, or <c>null</c> when none of this database's
    /// collections are sharded.
    ///
    /// <para>
    /// Azure Cosmos DB for MongoDB (RU-based API) does not expose an
    /// existing collection's shard key through any Mongo wire-protocol
    /// command — it's visible only in the Azure Portal / ARM management
    /// plane — so it cannot be auto-discovered at backup time. This is
    /// declared once per database instead, on the assumption (common for
    /// Cosmos DB for MongoDB accounts) that every sharded collection in a
    /// given database shares the same shard-key field name (e.g. <c>"_pk"</c>).
    /// </para>
    ///
    /// <para>
    /// When set, every new collection created during restore to a Cosmos DB
    /// target in this database gets this field applied as its shard key at
    /// creation time (required — Cosmos rejects adding a shard key after
    /// creation). On a standard MongoDB restore target, sharding a real
    /// cluster is out of scope for this tool; a non-fatal restore warning is
    /// recorded instead.
    /// </para>
    /// </summary>
    public string? ShardKeyField { get; set; }
}

namespace MongoCustody.Core.Abstractions;

/// <summary>
/// Abstracts write access to the target MongoDB instance used during a
/// restore operation. The target database must be pre-provisioned, empty,
/// and must never be the production source database.
///
/// <para>
/// The connector is created per-request from a connection string supplied
/// in <see cref="Models.RestoreRequest.TargetConnectionString"/> via
/// <see cref="IRestoreTargetConnectorFactory"/> and is disposed after the
/// restore operation completes.
/// </para>
/// </summary>
public interface IRestoreTargetConnector : IAsyncDisposable
{
    /// <summary>
    /// Pre-flight check that confirms the target database is ready to receive
    /// a restore. Throws <see cref="InvalidOperationException"/> when:
    /// <list type="bullet">
    ///   <item>The database does not exist (has not been provisioned).</item>
    ///   <item>The database already contains one or more collections.</item>
    /// </list>
    /// Must be called before any data is written to the target.
    /// </summary>
    Task EnsureEmptyDatabaseAsync(string databaseName, CancellationToken ct = default);
    /// <summary>
    /// Bulk-inserts raw BSON documents into a collection in the restore
    /// target. Used for the full-backup baseline step, where the target
    /// collection is guaranteed to be empty. Returns the number of documents
    /// inserted.
    /// </summary>
    /// <param name="databaseName">Destination database name.</param>
    /// <param name="collectionName">Destination collection name.</param>
    /// <param name="bsonDocuments">
    /// Stream of raw BSON document bytes, each a complete BSON-serialised
    /// document as produced by <see cref="IBackupSourceConnector"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<long> InsertManyAsync(
        string databaseName,
        string collectionName,
        IAsyncEnumerable<ReadOnlyMemory<byte>> bsonDocuments,
        CancellationToken ct = default);

    /// <summary>
    /// Upserts raw BSON documents into a collection in the restore target
    /// using <c>replaceOne(_id, doc, upsert:true)</c>. Used for incremental
    /// and checkpoint replay steps, where documents may already exist from
    /// earlier chain steps. Returns the number of documents written
    /// (inserted + replaced).
    /// </summary>
    /// <param name="databaseName">Destination database name.</param>
    /// <param name="collectionName">Destination collection name.</param>
    /// <param name="bsonDocuments">
    /// Stream of raw BSON document bytes, each a complete BSON-serialised
    /// document as produced by <see cref="IBackupSourceConnector"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<long> UpsertManyAsync(
        string databaseName,
        string collectionName,
        IAsyncEnumerable<ReadOnlyMemory<byte>> bsonDocuments,
        CancellationToken ct = default);

    /// <summary>
    /// Returns <c>true</c> if <paramref name="collectionName"/> already
    /// exists in <paramref name="databaseName"/> on the restore target.
    /// Used during incremental and checkpoint restore steps to detect newly
    /// added collections that must receive unique indexes before data load.
    /// </summary>
    Task<bool> CollectionExistsAsync(
        string databaseName,
        string collectionName,
        CancellationToken ct = default);

    /// <summary>
    /// Creates the supplied indexes on <paramref name="collectionName"/>.
    /// Runs the MongoDB <c>createIndexes</c> command with the full
    /// specification stored in each <see cref="Models.IndexDefinition.Spec"/>.
    /// Silently skips <c>indexes</c> that are already present by name.
    ///
    /// <para>
    /// Used for non-unique indexes only, created after all data for the
    /// restore chain has loaded. Unique indexes (and shard keys) on a
    /// brand-new collection must instead go through
    /// <see cref="CreateCollectionAsync"/> — see that method for why.
    /// </para>
    /// </summary>
    /// <param name="databaseName">Target database name.</param>
    /// <param name="collectionName">Target collection name.</param>
    /// <param name="indexes">Index definitions to create.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CreateIndexesAsync(
        string databaseName,
        string collectionName,
        IReadOnlyList<Models.IndexDefinition> indexes,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a brand-new collection with its shard key and/or unique
    /// indexes applied atomically at creation time, before any data is
    /// written.
    ///
    /// <para>
    /// This exists because Azure Cosmos DB for MongoDB (RU-based API)
    /// accounts with continuous backup enabled reject a plain
    /// <c>createIndexes</c> call for a unique index unconditionally — even
    /// on a brand-new, empty collection. The shard key has the same
    /// "must be set at creation, never after" constraint on both Cosmos DB
    /// and sharded MongoDB clusters. On a Cosmos target this is implemented
    /// via the <c>customAction: "CreateCollection"</c> extension command;
    /// on a standard MongoDB target, <paramref name="uniqueIndexes"/> is
    /// created with the ordinary <c>createIndexes</c> command (no such
    /// restriction exists there) and <paramref name="shardKeyField"/> is not
    /// applied — provisioning a real sharded cluster topology is out of
    /// scope for this tool.
    /// </para>
    ///
    /// <para>
    /// Call exactly once per collection, only when the collection does not
    /// already exist in the target (see <see cref="CollectionExistsAsync"/>),
    /// before any documents are written to it. A no-op when
    /// <paramref name="shardKeyField"/> is <c>null</c> and
    /// <paramref name="uniqueIndexes"/> is empty — the collection is left to
    /// be created implicitly by the first write, exactly as before this
    /// method existed.
    /// </para>
    /// </summary>
    /// <param name="databaseName">Target database name.</param>
    /// <param name="collectionName">Target collection name (must not yet exist).</param>
    /// <param name="shardKeyField">
    /// The name of the shard-key field shared by every collection in this
    /// database, or <c>null</c> when unsharded. See
    /// <see cref="Models.DatabaseConfiguration.ShardKeyField"/> — Cosmos DB
    /// for MongoDB does not expose an existing collection's shard key
    /// through any Mongo command, so this is operator-declared per
    /// database rather than discovered per collection at backup time.
    /// </param>
    /// <param name="uniqueIndexes">
    /// Unique index definitions to create as part of collection creation.
    /// Must contain only indexes where <see cref="Models.IndexDefinition.IsUnique"/>
    /// is <c>true</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A non-fatal warning message when <paramref name="shardKeyField"/>
    /// could not be applied (standard MongoDB target with a shard key
    /// configured), otherwise <c>null</c>.
    /// </returns>
    Task<string?> CreateCollectionAsync(
        string databaseName,
        string collectionName,
        string? shardKeyField,
        IReadOnlyList<Models.IndexDefinition> uniqueIndexes,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the total document count for a collection in the restore
    /// target. Used for post-restore count verification in Level 4
    /// (Restore Dry-Run) validation.
    /// </summary>
    Task<long> CountDocumentsAsync(
        string databaseName,
        string collectionName,
        CancellationToken ct = default);

    /// <summary>
    /// Drops <paramref name="collectionName"/> from <paramref name="databaseName"/>
    /// if it exists. Safe to call when the collection is absent — the operation
    /// completes without error.
    /// </summary>
    Task DropCollectionAsync(
        string databaseName,
        string collectionName,
        CancellationToken ct = default);

    /// <summary>
    /// Drops all collections in <paramref name="databaseName"/>, returning
    /// it to an empty state. Used at the end of a dry-run to remove restored
    /// ePHI from the temporary target database.
    /// </summary>
    Task DropAllCollectionsAsync(string databaseName, CancellationToken ct = default);

    /// <summary>
    /// Verifies that the connector can reach the restore target.
    /// Returns <c>true</c> if reachable, together with an optional error
    /// message on failure.
    /// </summary>
    Task<(bool IsHealthy, string? ErrorMessage)> CheckHealthAsync(
        CancellationToken ct = default);
}

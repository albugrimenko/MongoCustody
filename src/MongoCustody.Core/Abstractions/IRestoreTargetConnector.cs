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
    /// Returns the total document count for a collection in the restore
    /// target. Used for post-restore count verification in Level 4
    /// (Restore Dry-Run) validation.
    /// </summary>
    Task<long> CountDocumentsAsync(
        string databaseName,
        string collectionName,
        CancellationToken ct = default);

    /// <summary>
    /// Verifies that the connector can reach the restore target.
    /// Returns <c>true</c> if reachable, together with an optional error
    /// message on failure.
    /// </summary>
    Task<(bool IsHealthy, string? ErrorMessage)> CheckHealthAsync(
        CancellationToken ct = default);
}

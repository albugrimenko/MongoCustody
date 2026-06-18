namespace MongoCustody.Core.Abstractions;

/// <summary>
/// Abstracts write access to the target MongoDB account used during a restore
/// operation. The target is always a separate, empty account — never the
/// production source.
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

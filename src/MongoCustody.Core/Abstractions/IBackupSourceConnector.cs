namespace MongoCustody.Core.Abstractions;

/// <summary>
/// Represents a single MongoDB collection as a stream of serialised
/// BSON documents, with pagination for chunked export.
/// </summary>
public sealed class CollectionPage
{
    /// <summary>Name of the collection this page belongs to.</summary>
    public string CollectionName { get; set; } = string.Empty;

    /// <summary>
    /// Raw BSON bytes of each document in this page, in cursor order.
    /// </summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Documents { get; set; }
        = Array.Empty<ReadOnlyMemory<byte>>();

    /// <summary>
    /// <c>true</c> if there are more documents available after this page.
    /// </summary>
    public bool HasMore { get; set; }
}

/// <summary>
/// Abstracts read-only access to a MongoDB-compatible source database
/// for backup purposes.
/// </summary>
public interface IBackupSourceConnector
{
    /// <summary>
    /// Returns the names of all collections in the specified database,
    /// excluding any collections listed in
    /// <see cref="Models.DatabaseConfiguration.ExcludedCollections"/>.
    /// </summary>
    /// <param name="databaseName">Target database name.</param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<string> ListCollectionsAsync(
        string databaseName,
        CancellationToken ct = default);

    /// <summary>
    /// Streams all documents in a collection as raw BSON bytes,
    /// in ascending <c>_id</c> order (full backup).
    /// Memory consumption is O(1) with respect to collection size.
    /// </summary>
    /// <param name="databaseName">Source database name.</param>
    /// <param name="collectionName">Source collection name.</param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<ReadOnlyMemory<byte>> StreamAllDocumentsAsync(
        string databaseName,
        string collectionName,
        CancellationToken ct = default);

    /// <summary>
    /// Streams documents modified since <paramref name="fromTimestamp"/>
    /// (incremental backup).
    /// Uses the server-managed <c>_ts</c> field:
    /// <c>_ts &gt; (fromTimestamp - bufferSeconds)</c>.
    /// Memory consumption is O(1) with respect to result set size.
    /// </summary>
    /// <param name="databaseName">Source database name.</param>
    /// <param name="collectionName">Source collection name.</param>
    /// <param name="fromTimestamp">
    /// Unix timestamp of the previous backup's <c>anchorTs</c>.
    /// The connector applies the configured overlap buffer internally.
    /// </param>
    /// <param name="bufferSeconds">
    /// Conservative overlap buffer subtracted from
    /// <paramref name="fromTimestamp"/> before the query.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<ReadOnlyMemory<byte>> StreamChangedDocumentsAsync(
        string databaseName,
        string collectionName,
        long fromTimestamp,
        int bufferSeconds,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the current server timestamp (Unix seconds) from the
    /// MongoDB engine. Used to capture <c>anchorTs</c> at the start of
    /// a full backup and at the end of an incremental backup.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<long> GetServerTimestampAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a count of documents matching a soft-delete-aware predicate
    /// as of a given anchor timestamp.
    /// Used by Level 2 (Count Consistency) and Level 3 (Incremental
    /// Completeness) validation.
    /// </summary>
    /// <param name="databaseName">Source database name.</param>
    /// <param name="collectionName">Source collection name.</param>
    /// <param name="anchorTs">
    /// Unix timestamp of the backup's <c>anchorTs</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<long> CountDocumentsAsOfAsync(
        string databaseName,
        string collectionName,
        long anchorTs,
        CancellationToken ct = default);

    /// <summary>
    /// Verifies the connector can reach the MongoDB instance.
    /// Returns <c>true</c> and an optional latency if reachable.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<(bool IsHealthy, long LatencyMs, string? ErrorMessage)> CheckHealthAsync(
        CancellationToken ct = default);
}

using MongoCustody.Core.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace MongoCustody.Connectors;

/// <summary>
/// <see cref="IRestoreTargetConnector"/> implementation backed by
/// <c>MongoDB.Driver</c> 3.x.
///
/// <para>
/// Created per restore request by <see cref="MongoDbRestoreTargetConnectorFactory"/>
/// using the connection string supplied in
/// <see cref="Core.Models.RestoreRequest.TargetConnectionString"/>.
/// Disposed (and the underlying connection pool released) when the engine
/// finishes.
/// </para>
///
/// <para>
/// <see cref="InsertManyAsync"/> batches documents in groups of 100 using
/// <c>insertMany</c> — correct when the target collection is guaranteed to be
/// empty (full backup baseline).
/// </para>
///
/// <para>
/// <see cref="UpsertManyAsync"/> uses <c>bulkWrite</c> with
/// <c>replaceOne(filter:{"_id":&lt;id&gt;}, upsert:true)</c> batched in groups of 100.
/// This is the correct strategy for incremental and checkpoint replay because:
/// <list type="bullet">
///   <item>A document in both the full baseline and a later incremental is written
///         twice; the incremental (applied second) wins — correct final state.</item>
///   <item>A document soft-deleted after the baseline appears in the incremental
///         with <c>isDeleted:true</c>; upsert writes the final deleted state.</item>
///   <item>A new document (post-baseline) appears only in the incremental;
///         upsert inserts it.</item>
/// </list>
/// </para>
/// </summary>
public sealed class MongoDbRestoreTargetConnector : IRestoreTargetConnector
{
    private readonly IMongoClient _client;
    private const int BatchSize = 100;

    public MongoDbRestoreTargetConnector(string connectionString)
    {
        _client = new MongoClient(connectionString);
    }

    /// <inheritdoc />
    public async Task<long> InsertManyAsync(
        string databaseName,
        string collectionName,
        IAsyncEnumerable<ReadOnlyMemory<byte>> bsonDocuments,
        CancellationToken ct = default)
    {
        var collection = GetCollection(databaseName, collectionName);
        var batch      = new List<BsonDocument>(BatchSize);
        long inserted  = 0;

        await foreach (var docBytes in bsonDocuments.WithCancellation(ct).ConfigureAwait(false))
        {
            batch.Add(DeserializeDocument(docBytes));

            if (batch.Count >= BatchSize)
            {
                await collection.InsertManyAsync(batch, cancellationToken: ct)
                    .ConfigureAwait(false);
                inserted += batch.Count;
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await collection.InsertManyAsync(batch, cancellationToken: ct)
                .ConfigureAwait(false);
            inserted += batch.Count;
        }

        return inserted;
    }

    /// <inheritdoc />
    public async Task<long> UpsertManyAsync(
        string databaseName,
        string collectionName,
        IAsyncEnumerable<ReadOnlyMemory<byte>> bsonDocuments,
        CancellationToken ct = default)
    {
        var collection = GetCollection(databaseName, collectionName);
        var writes     = new List<WriteModel<BsonDocument>>(BatchSize);
        long written   = 0;

        await foreach (var docBytes in bsonDocuments.WithCancellation(ct).ConfigureAwait(false))
        {
            var doc    = DeserializeDocument(docBytes);
            var filter = Builders<BsonDocument>.Filter.Eq("_id", doc["_id"]);
            writes.Add(new ReplaceOneModel<BsonDocument>(filter, doc) { IsUpsert = true });

            if (writes.Count >= BatchSize)
            {
                written += await FlushUpsertBatchAsync(collection, writes, ct)
                    .ConfigureAwait(false);
            }
        }

        if (writes.Count > 0)
            written += await FlushUpsertBatchAsync(collection, writes, ct)
                .ConfigureAwait(false);

        return written;
    }

    /// <inheritdoc />
    public async Task<long> CountDocumentsAsync(
        string databaseName,
        string collectionName,
        CancellationToken ct = default)
    {
        var collection = GetCollection(databaseName, collectionName);
        return await collection
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<(bool IsHealthy, string? ErrorMessage)> CheckHealthAsync(
        CancellationToken ct = default)
    {
        try
        {
            await _client
                .GetDatabase("admin")
                .RunCommandAsync<BsonDocument>(
                    new BsonDocument("ping", 1),
                    cancellationToken: ct)
                .ConfigureAwait(false);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Restore target health check failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_client is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (_client is IDisposable disposable)
            disposable.Dispose();
    }

    // ------------------------------------------------------------------ //
    //  Private helpers
    // ------------------------------------------------------------------ //

    private IMongoCollection<BsonDocument> GetCollection(
        string databaseName, string collectionName)
        => _client.GetDatabase(databaseName).GetCollection<BsonDocument>(collectionName);

    private static BsonDocument DeserializeDocument(ReadOnlyMemory<byte> bytes)
    {
        // BsonSerializer.Deserialize requires a byte[] — avoid copying when
        // the memory is already backed by an array (it always is from BsonChunkReader).
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(
                bytes, out var segment))
        {
            using var ms = new MemoryStream(segment.Array!, segment.Offset, segment.Count);
            return BsonSerializer.Deserialize<BsonDocument>(ms);
        }

        // Fallback: copy to a temporary array (should not happen in practice).
        using var fallback = new MemoryStream(bytes.ToArray());
        return BsonSerializer.Deserialize<BsonDocument>(fallback);
    }

    private static async Task<long> FlushUpsertBatchAsync(
        IMongoCollection<BsonDocument>  collection,
        List<WriteModel<BsonDocument>>  writes,
        CancellationToken               ct)
    {
        var bulkResult = await collection
            .BulkWriteAsync(writes, cancellationToken: ct)
            .ConfigureAwait(false);

        writes.Clear();
        return bulkResult.Upserts.Count + bulkResult.MatchedCount;
    }
}

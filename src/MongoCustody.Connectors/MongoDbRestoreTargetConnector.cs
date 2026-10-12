using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;
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
    private readonly bool _isCosmos;
    private const int BatchSize = 100;

    public MongoDbRestoreTargetConnector(string connectionString)
    {
        _client = new MongoClient(connectionString);
        _isCosmos = CosmosDetector.IsCosmos(connectionString);
    }

    /// <inheritdoc />
    public async Task EnsureEmptyDatabaseAsync(
        string databaseName,
        CancellationToken ct = default)
    {
        // Verify the database exists (appears in the server's database list).
        using var dbCursor = await _client
            .ListDatabaseNamesAsync(cancellationToken: ct)
            .ConfigureAwait(false);

        var dbNames = await dbCursor.ToListAsync(ct).ConfigureAwait(false);
        if (!dbNames.Contains(databaseName, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"Target database '{databaseName}' does not exist on the target instance. " +
                $"Provision the database before running a restore.");

        // Verify the database has no collections.
        var db = _client.GetDatabase(databaseName);
        using var colCursor = await db
            .ListCollectionNamesAsync(cancellationToken: ct)
            .ConfigureAwait(false);

        var collections = await colCursor.ToListAsync(ct).ConfigureAwait(false);
        if (collections.Count > 0)
        {
            var sample = string.Join(", ", collections.Take(5));
            var more   = collections.Count > 5 ? $" … and {collections.Count - 5} more" : string.Empty;
            throw new InvalidOperationException(
                $"Target database '{databaseName}' is not empty — " +
                $"found {collections.Count} collection(s): {sample}{more}. " +
                $"Drop all collections before running a restore.");
        }
    }

    /// <inheritdoc />
    public async Task<bool> CollectionExistsAsync(
        string databaseName,
        string collectionName,
        CancellationToken ct = default)
    {
        var db = _client.GetDatabase(databaseName);
        using var cursor = await db
            .ListCollectionNamesAsync(cancellationToken: ct)
            .ConfigureAwait(false);
        var names = await cursor.ToListAsync(ct).ConfigureAwait(false);
        return names.Contains(collectionName, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task CreateIndexesAsync(
        string databaseName,
        string collectionName,
        IReadOnlyList<IndexDefinition> indexes,
        CancellationToken ct = default)
    {
        if (indexes.Count == 0) return;

        var indexDocs = new BsonArray();
        foreach (var idx in indexes)
        {
            // Parse the stored relaxed extended JSON spec back to BsonDocument.
            var specDoc = BsonDocument.Parse(idx.Spec.GetRawText());

            // Strip server-managed fields that must not be passed to createIndexes.
            specDoc.Remove("v");   // wire protocol version — server sets this
            specDoc.Remove("ns");  // namespace — removed in MongoDB 4.4+

            indexDocs.Add(specDoc);
        }

        // Use the raw createIndexes command so the spec round-trips with exact
        // fidelity — no manual option mapping required.
        var command = new BsonDocument
        {
            { "createIndexes", collectionName },
            { "indexes", indexDocs }
        };

        await _client
            .GetDatabase(databaseName)
            .RunCommandAsync<BsonDocument>(command, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> CreateCollectionAsync(
        string databaseName,
        string collectionName,
        string? shardKeyField,
        IReadOnlyList<IndexDefinition> uniqueIndexes,
        CancellationToken ct = default)
    {
        if (_isCosmos)
        {
            await CreateCosmosCollectionAsync(
                    databaseName, collectionName, shardKeyField, uniqueIndexes, ct)
                .ConfigureAwait(false);
            return null;
        }

        // Standard MongoDB: no restriction against createIndexes with
        // unique:true on a new/empty collection — use the ordinary path.
        if (uniqueIndexes.Count > 0)
            await CreateIndexesAsync(databaseName, collectionName, uniqueIndexes, ct)
                .ConfigureAwait(false);

        // Sharding a real MongoDB cluster requires a mongos/config-server
        // topology this tool does not provision — surface as a non-fatal
        // warning instead of silently dropping the shard key.
        return !string.IsNullOrWhiteSpace(shardKeyField)
            ? $"Database has ShardKeyField '{shardKeyField}' configured, but the restore " +
              $"target is a standard MongoDB instance — sharding a target cluster is out " +
              $"of scope for this tool. Collection '{collectionName}' was restored unsharded."
            : null;
    }

    /// <summary>
    /// Creates a collection on a Cosmos DB for MongoDB (RU-based API) target
    /// via the <c>customAction: "CreateCollection"</c> extension command,
    /// applying the shard key and any unique indexes atomically at creation
    /// time. Required because Cosmos continuous-backup accounts reject a
    /// plain <c>createIndexes</c> call for a unique index unconditionally,
    /// even on an empty collection — and the shard key cannot be set after
    /// collection creation at all.
    /// </summary>
    private async Task CreateCosmosCollectionAsync(
        string databaseName,
        string collectionName,
        string? shardKeyField,
        IReadOnlyList<IndexDefinition> uniqueIndexes,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(shardKeyField) && uniqueIndexes.Count == 0)
            return; // No special requirements — implicit creation on first write.

        var command = new BsonDocument
        {
            { "customAction", "CreateCollection" },
            { "collection", collectionName }
        };

        if (!string.IsNullOrWhiteSpace(shardKeyField))
            command["shardKey"] = shardKeyField;

        if (uniqueIndexes.Count > 0)
        {
            var indexDocs = new BsonArray();
            foreach (var idx in uniqueIndexes)
            {
                var specDoc = BsonDocument.Parse(idx.Spec.GetRawText());
                specDoc.Remove("v");
                specDoc.Remove("ns");
                indexDocs.Add(specDoc);
            }

            command["indexes"] = indexDocs;
        }

        await _client
            .GetDatabase(databaseName)
            .RunCommandAsync<BsonDocument>(command, cancellationToken: ct)
            .ConfigureAwait(false);
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
    public Task DropCollectionAsync(
        string databaseName,
        string collectionName,
        CancellationToken ct = default)
        => _client.GetDatabase(databaseName).DropCollectionAsync(collectionName, ct);

    /// <inheritdoc />
    public async Task DropAllCollectionsAsync(
        string databaseName,
        CancellationToken ct = default)
    {
        var db = _client.GetDatabase(databaseName);
        using var cursor = await db
            .ListCollectionNamesAsync(cancellationToken: ct)
            .ConfigureAwait(false);
        var names = await cursor.ToListAsync(ct).ConfigureAwait(false);

        var drops = names.Select(name =>
            db.DropCollectionAsync(name, ct));
        await Task.WhenAll(drops).ConfigureAwait(false);
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

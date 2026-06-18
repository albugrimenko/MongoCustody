using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace MongoCustody.Connectors;

/// <summary>
/// <see cref="IBackupSourceConnector"/> implementation backed by
/// <c>MongoDB.Driver</c> 3.x. Works against MongoDB 4.0+ and
/// Azure Cosmos DB for MongoDB (RU-based API).
///
/// The connection string is resolved lazily from the secret provider
/// using the key <c>mongocustody-mongo-connection-string</c>.
/// If the secret is absent, <see cref="MongoCustodyConfiguration.MongoDBAccountEndpoint"/>
/// is used as the connection string (useful for Cosmos DB managed identity
/// endpoints that embed no credentials).
/// </summary>
public sealed class MongoDbConnector : IBackupSourceConnector
{
    private readonly MongoCustodyConfiguration _config;
    private readonly ISecretProvider _secretProvider;

    // Lazily initialised on first use — avoids blocking DI construction
    private IMongoClient? _client;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public MongoDbConnector(
        MongoCustodyConfiguration configuration,
        ISecretProvider secretProvider)
    {
        _config = configuration;
        _secretProvider = secretProvider;
    }

    // ------------------------------------------------------------------ //
    //  IBackupSourceConnector
    // ------------------------------------------------------------------ //

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListCollectionsAsync(
        string databaseName,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var db = await GetDatabaseAsync(databaseName, ct).ConfigureAwait(false);

        using var cursor = await db
            .ListCollectionNamesAsync(cancellationToken: ct)
            .ConfigureAwait(false);

        var excluded = GetExcludedCollections(databaseName);

        while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
        {
            foreach (var name in cursor.Current)
            {
                if (!excluded.Contains(name))
                    yield return name;
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamAllDocumentsAsync(
        string databaseName,
        string collectionName,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var collection = await GetCollectionAsync(databaseName, collectionName, ct)
            .ConfigureAwait(false);

        // Full scan in ascending _id order, no filter
        var sort = Builders<BsonDocument>.Sort.Ascending("_id");
        var options = new FindOptions<BsonDocument>
        {
            Sort = sort,
            BatchSize = 100,
            NoCursorTimeout = false
        };

        using var cursor = await collection
            .FindAsync(FilterDefinition<BsonDocument>.Empty, options, ct)
            .ConfigureAwait(false);

        while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
        {
            foreach (var doc in cursor.Current)
                yield return SerializeDocument(doc);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamChangedDocumentsAsync(
        string databaseName,
        string collectionName,
        long fromTimestamp,
        int bufferSeconds,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var collection = await GetCollectionAsync(databaseName, collectionName, ct)
            .ConfigureAwait(false);

        long queryFromTs = fromTimestamp - bufferSeconds;
        var filter = Builders<BsonDocument>.Filter.Gt("_ts", queryFromTs);
        var sort = Builders<BsonDocument>.Sort.Ascending("_ts").Ascending("_id");
        var options = new FindOptions<BsonDocument>
        {
            Sort = sort,
            BatchSize = 100,
            NoCursorTimeout = false
        };

        using var cursor = await collection
            .FindAsync(filter, options, ct)
            .ConfigureAwait(false);

        while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
        {
            foreach (var doc in cursor.Current)
                yield return SerializeDocument(doc);
        }
    }

    /// <inheritdoc />
    public async Task<long> GetServerTimestampAsync(CancellationToken ct = default)
    {
        // Cosmos DB for MongoDB does not expose a server clock read without
        // a write. We ping to confirm connectivity then return the client
        // UTC clock, which is accurate to within the network round-trip.
        var client = await GetClientAsync(ct).ConfigureAwait(false);
        await client
            .GetDatabase("admin")
            .RunCommandAsync<BsonDocument>(
                new BsonDocument("ping", 1),
                cancellationToken: ct)
            .ConfigureAwait(false);

        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <inheritdoc />
    public async Task<long> CountDocumentsAsOfAsync(
        string databaseName,
        string collectionName,
        long anchorTs,
        CancellationToken ct = default)
    {
        var collection = await GetCollectionAsync(databaseName, collectionName, ct)
            .ConfigureAwait(false);

        // Counts live documents modified after anchorTs — used for Level 3
        // (Incremental Completeness) validation: how many documents changed
        // since the last anchor and still exist (i.e. were not soft-deleted).
        //
        //   _ts > anchorTs          — modified after the anchor
        //   isDeleted absent/false  — document is still live
        var notDeleted = Builders<BsonDocument>.Filter.Or(
            Builders<BsonDocument>.Filter.Not(
                Builders<BsonDocument>.Filter.Exists("isDeleted")),
            Builders<BsonDocument>.Filter.Eq("isDeleted", false));

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Gt("_ts", anchorTs),
            notDeleted);

        return await collection
            .CountDocumentsAsync(filter, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> CountDocumentsExistingAtAsync(
        string databaseName,
        string collectionName,
        long anchorTs,
        CancellationToken ct = default)
    {
        var collection = await GetCollectionAsync(databaseName, collectionName, ct)
            .ConfigureAwait(false);

        // Counts documents that existed at anchorTs — used for Level 2
        // (Count Consistency) validation: documents never soft-deleted, OR
        // documents soft-deleted AFTER the backup's anchor timestamp (they
        // were still present in the collection when the snapshot was taken).
        //
        //   (isDeleted absent OR isDeleted=false)
        //   OR (isDeleted=true AND _ts > anchorTs)
        var notDeleted = Builders<BsonDocument>.Filter.Or(
            Builders<BsonDocument>.Filter.Not(
                Builders<BsonDocument>.Filter.Exists("isDeleted")),
            Builders<BsonDocument>.Filter.Eq("isDeleted", false));

        var deletedAfterAnchor = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("isDeleted", true),
            Builders<BsonDocument>.Filter.Gt("_ts", anchorTs));

        var filter = Builders<BsonDocument>.Filter.Or(notDeleted, deletedAfterAnchor);

        return await collection
            .CountDocumentsAsync(filter, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<(bool IsHealthy, long LatencyMs, string? ErrorMessage)>
        CheckHealthAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var client = await GetClientAsync(ct).ConfigureAwait(false);
            await client
                .GetDatabase("admin")
                .RunCommandAsync<BsonDocument>(
                    new BsonDocument("ping", 1),
                    cancellationToken: ct)
                .ConfigureAwait(false);
            sw.Stop();
            return (true, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds,
                $"MongoDB health check failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ //
    //  Private helpers
    // ------------------------------------------------------------------ //

    private async Task<IMongoClient> GetClientAsync(CancellationToken ct)
    {
        if (_client is not null) return _client;

        bool acquired = false;
        try
        {
            await _initLock.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            if (_client is not null) return _client;

            var connectionString = await _secretProvider
                .GetSecretAsync("mongocustody-mongo-connection-string", ct)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(connectionString))
                connectionString = _config.MongoDBAccountEndpoint;

            _client = new MongoClient(connectionString);
            return _client;
        }
        finally
        {
            if (acquired) _initLock.Release();
        }
    }

    private async Task<IMongoDatabase> GetDatabaseAsync(
        string databaseName,
        CancellationToken ct)
    {
        var client = await GetClientAsync(ct).ConfigureAwait(false);
        return client.GetDatabase(databaseName);
    }

    private async Task<IMongoCollection<BsonDocument>> GetCollectionAsync(
        string databaseName,
        string collectionName,
        CancellationToken ct)
    {
        var db = await GetDatabaseAsync(databaseName, ct).ConfigureAwait(false);
        return db.GetCollection<BsonDocument>(collectionName);
    }

    private HashSet<string> GetExcludedCollections(string databaseName)
    {
        var dbConfig = _config.Databases
            .FirstOrDefault(d => d.DatabaseName == databaseName);
        return dbConfig?.ExcludedCollections is { Count: > 0 }
            ? new HashSet<string>(dbConfig.ExcludedCollections,
                StringComparer.OrdinalIgnoreCase)
            : [];
    }

    /// <summary>
    /// Serialises a <see cref="BsonDocument"/> to raw BSON bytes.
    /// </summary>
    private static ReadOnlyMemory<byte> SerializeDocument(BsonDocument doc)
    {
        using var ms = new MemoryStream();
        using var writer = new BsonBinaryWriter(ms);
        BsonSerializer.Serialize(writer, doc);
        return ms.ToArray();
    }
}

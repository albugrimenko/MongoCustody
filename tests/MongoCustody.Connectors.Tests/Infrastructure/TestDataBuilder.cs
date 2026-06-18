using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoCustody.Connectors.Tests.Infrastructure;

/// <summary>
/// Fluent builder for generic BSON test documents.
///
/// <para>
/// Uses three generic collection types — <c>users</c>, <c>projects</c>, and
/// <c>documents</c> — so tests remain domain-neutral while still having
/// realistic, queryable data shapes.
/// </para>
///
/// <para>
/// Every document carries:
/// <list type="bullet">
///   <item><c>_id</c> — unique <see cref="ObjectId"/>.</item>
///   <item><c>name</c> — human-readable label derived from the type and index.</item>
///   <item><c>status</c> — alternates between "active" and "archived".</item>
///   <item><c>createdAt</c> — UTC datetime offset back from <paramref name="anchorTs"/>.</item>
///   <item><c>_ts</c> and <c>_updatedAt</c> — Unix-second timestamps used by
///         the incremental connector queries.</item>
/// </list>
/// </para>
/// </summary>
public sealed class TestDataBuilder
{
    private readonly string _type;
    private readonly long   _anchorTs;
    private readonly int    _count;
    private int             _deletedEvery = 0;

    private TestDataBuilder(string type, int count, long anchorTs)
    {
        _type     = type;
        _count    = count;
        _anchorTs = anchorTs;
    }

    // ------------------------------------------------------------------ //
    //  Factory methods
    // ------------------------------------------------------------------ //

    /// <summary>Generates <paramref name="count"/> user documents.</summary>
    public static TestDataBuilder Users(int count, long? anchorTs = null)
        => new("user", count, anchorTs ?? UtcNow());

    /// <summary>Generates <paramref name="count"/> project documents.</summary>
    public static TestDataBuilder Projects(int count, long? anchorTs = null)
        => new("project", count, anchorTs ?? UtcNow());

    /// <summary>Generates <paramref name="count"/> generic document records.</summary>
    public static TestDataBuilder Documents(int count, long? anchorTs = null)
        => new("document", count, anchorTs ?? UtcNow());

    // ------------------------------------------------------------------ //
    //  Modifiers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Marks every <paramref name="nth"/> document as deleted
    /// (adds <c>isDeleted: true</c>).
    /// </summary>
    public TestDataBuilder WithSomeDeleted(int nth)
    {
        _deletedEvery = nth;
        return this;
    }

    // ------------------------------------------------------------------ //
    //  Build
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Materialises and returns the generated document list.
    /// Each document's <c>_ts</c> and <c>_updatedAt</c> decrease by one
    /// second per document so the newest document has the highest timestamp.
    /// </summary>
    public IReadOnlyList<BsonDocument> Build()
    {
        var docs = new List<BsonDocument>(_count);
        for (int i = 1; i <= _count; i++)
        {
            long ts = _anchorTs - i;   // newest = smallest i = largest ts
            bool deleted = _deletedEvery > 0 && i % _deletedEvery == 0;

            var doc = new BsonDocument
            {
                ["_id"]        = ObjectId.GenerateNewId(),
                ["name"]       = $"{_type}-{i:D4}",
                ["slug"]       = $"{_type}-{i:D6}",
                ["status"]     = deleted ? "deleted" : (i % 3 == 0 ? "archived" : "active"),
                ["isDeleted"]  = new BsonBoolean(deleted),
                ["createdAt"]  = new BsonDateTime(
                    DateTimeOffset.FromUnixTimeSeconds(ts - 86400).UtcDateTime),
                ["_updatedAt"] = new BsonInt64(ts),
                ["_ts"]        = new BsonInt64(ts),
            };

            docs.Add(doc);
        }
        return docs.AsReadOnly();
    }

    // ------------------------------------------------------------------ //
    //  MongoDB helpers — seed and clean up via a live IMongoDatabase
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Inserts the generated documents into <paramref name="collection"/>
    /// and returns the inserted count.
    /// </summary>
    public async Task<int> SeedAsync(
        IMongoCollection<BsonDocument> collection,
        CancellationToken ct = default)
    {
        var docs = Build();
        await collection.InsertManyAsync(docs, cancellationToken: ct)
            .ConfigureAwait(false);
        return docs.Count;
    }

    /// <summary>
    /// Convenience overload: resolves the collection from an
    /// <see cref="IMongoDatabase"/> and seeds it.
    /// </summary>
    public Task<int> SeedAsync(
        IMongoDatabase database,
        string collectionName,
        CancellationToken ct = default)
        => SeedAsync(database.GetCollection<BsonDocument>(collectionName), ct);

    // ------------------------------------------------------------------ //
    //  Private helpers
    // ------------------------------------------------------------------ //

    private static long UtcNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

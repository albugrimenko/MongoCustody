using MongoCustody.Connectors.Tests.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoCustody.Connectors.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="MongoDbConnector"/> against the
/// MongoDB container started by <see cref="ContainerFixture"/>.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class MongoDbConnectorIntegrationTests : IntegrationTestBase
{
    private MongoDbConnector _connector = null!;
    private IMongoDatabase   _database  = null!;

    // Unique database name per test run so parallel executions don't clash.
    private static readonly string DbName =
        $"mc_test_{Guid.NewGuid():N}";

    [TestInitialize]
    public void SetUp()
    {
        RequireContainers();
        Environment.SetEnvironmentVariable(
            "MONGOCUSTODY_MONGO_CONNECTION_STRING", MongoConnectionString);

        var config = new MongoCustodyConfiguration
        {
            Databases =
            [
                new DatabaseConfiguration
                {
                    DatabaseName        = DbName,
                    ExcludedCollections = ["_excluded"]
                }
            ]
        };

        _connector = new MongoDbConnector(config, new EnvironmentSecretProvider());

        var client = new MongoClient(MongoConnectionString);
        _database  = client.GetDatabase(DbName);
    }

    [TestCleanup]
    public async Task TearDown()
    {
        Environment.SetEnvironmentVariable("MONGOCUSTODY_MONGO_CONNECTION_STRING", null);
        try
        {
            var client = new MongoClient(MongoConnectionString);
            await client.DropDatabaseAsync(DbName);
        }
        catch (InvalidOperationException) { /* containers were never started */ }
    }

    [TestMethod]
    public async Task CheckHealthAsync_ReturnsHealthy()
    {
        var (isHealthy, latencyMs, error) = await _connector.CheckHealthAsync();
        Assert.IsTrue(isHealthy, $"Health check failed: {error}");
        Assert.IsTrue(latencyMs >= 0);
    }

    [TestMethod]
    public async Task GetServerTimestampAsync_ReturnsReasonableUnixTimestamp()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ts     = await _connector.GetServerTimestampAsync();
        var after  = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.IsTrue(ts >= before,     "Timestamp should be >= before probe.");
        Assert.IsTrue(ts <= after + 1,  "Timestamp should be <= after probe (+ 1s tolerance).");
    }

    [TestMethod]
    public async Task ListCollectionsAsync_ExcludesConfiguredCollections()
    {
        // Create both an included and an excluded collection.
        await _database.GetCollection<BsonDocument>("projects")
            .InsertOneAsync(new BsonDocument("x", 1));
        await _database.GetCollection<BsonDocument>("_excluded")
            .InsertOneAsync(new BsonDocument("x", 1));

        var collections = new List<string>();
        await foreach (var name in _connector.ListCollectionsAsync(DbName))
            collections.Add(name);

        Assert.IsTrue(collections.Contains("projects"),
            "Collection 'projects' should be listed.");
        Assert.IsFalse(collections.Contains("_excluded"),
            "Collection '_excluded' must be omitted.");
    }

    [TestMethod]
    public async Task StreamAllDocumentsAsync_ReturnsAllSeededDocuments()
    {
        var seeded = await TestDataBuilder.Projects(50)
            .SeedAsync(_database, "projects");

        var count = 0;
        await foreach (var bytes in _connector.StreamAllDocumentsAsync(DbName, "projects"))
        {
            Assert.IsTrue(bytes.Length >= 5, "Minimum BSON document is 5 bytes.");
            count++;
        }

        Assert.AreEqual(seeded, count, "Streamed count must match seeded count.");
    }

    [TestMethod]
    public async Task StreamChangedDocumentsAsync_ReturnsOnlyDocumentsAfterAnchor()
    {
        var anchorTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Documents timestamped before the anchor — should NOT appear.
        await TestDataBuilder.Documents(20, anchorTs: anchorTs - 1000)
            .SeedAsync(_database, "documents");

        // Documents timestamped after the anchor — should appear.
        await TestDataBuilder.Documents(10, anchorTs: anchorTs + 5)
            .SeedAsync(_database, "documents");

        var count = 0;
        await foreach (var _ in _connector.StreamChangedDocumentsAsync(
            DbName, "documents", anchorTs, bufferSeconds: 0))
            count++;

        Assert.AreEqual(10, count,
            "Only documents updated after anchorTs should be returned.");
    }

    [TestMethod]
    public async Task StreamChangedDocumentsAsync_WithFutureAnchor_YieldsNoDocuments()
    {
        long futureTs = DateTimeOffset.UtcNow.AddYears(10).ToUnixTimeSeconds();

        var count = 0;
        await foreach (var _ in _connector.StreamChangedDocumentsAsync(
            DbName, "smoke_test", futureTs, bufferSeconds: 0))
            count++;

        Assert.AreEqual(0, count, "No documents should match a future anchor.");
    }
}

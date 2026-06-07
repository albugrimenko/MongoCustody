using MongoCustody.Connectors.Secrets;
using MongoCustody.Core.Models;

namespace MongoCustody.Connectors.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="MongoDbConnector"/> against a local
/// MongoDB instance (or Cosmos DB emulator).
///
/// These tests are skipped unless <c>MONGOCUSTODY_INTEGRATION_TESTS=1</c>.
/// Start MongoDB before running:
/// <code>docker run -d -p 27017:27017 mongo:7</code>
/// or via Docker Compose in the tests/ directory.
/// </summary>
[TestClass]
public sealed class MongoDbConnectorIntegrationTests : IntegrationTestBase
{
    private MongoDbConnector _connector = null!;

    [TestInitialize]
    public void SetUp()
    {
        RequireIntegration();

        Environment.SetEnvironmentVariable(
            "MONGOCUSTODY_MONGO_CONNECTION_STRING", MongoLocalConnectionString);

        var config = new MongoCustodyConfiguration
        {
            MongoDBAccountEndpoint = MongoLocalConnectionString,
            Databases =
            [
                new DatabaseConfiguration
                {
                    DatabaseName = TestDatabaseName,
                    ExcludedCollections = ["_excluded"]
                }
            ]
        };

        _connector = new MongoDbConnector(config, new EnvironmentSecretProvider());
    }

    [TestCleanup]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(
            "MONGOCUSTODY_MONGO_CONNECTION_STRING", null);
    }

    [TestMethod]
    public async Task CheckHealthAsync_ReturnsHealthy_AgainstLocalMongo()
    {
        RequireIntegration();
        var (isHealthy, latencyMs, error) = await _connector.CheckHealthAsync();
        Assert.IsTrue(isHealthy, $"Health check failed: {error}");
        Assert.IsTrue(latencyMs >= 0);
    }

    [TestMethod]
    public async Task GetServerTimestampAsync_ReturnsReasonableUnixTimestamp()
    {
        RequireIntegration();
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ts = await _connector.GetServerTimestampAsync();
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.IsTrue(ts >= before, "Timestamp should be >= before probe");
        Assert.IsTrue(ts <= after + 1, "Timestamp should be <= after probe (+ 1s tolerance)");
    }

    [TestMethod]
    public async Task ListCollectionsAsync_ExcludesConfiguredCollections()
    {
        RequireIntegration();
        var collections = new List<string>();
        await foreach (var name in _connector.ListCollectionsAsync(TestDatabaseName))
            collections.Add(name);

        Assert.IsFalse(
            collections.Contains("_excluded"),
            "Excluded collection '_excluded' must not appear in results.");
    }

    [TestMethod]
    public async Task StreamAllDocumentsAsync_YieldsValidBsonBytes()
    {
        RequireIntegration();

        // The test collection may be empty — just verify no exception is thrown
        // and all yielded buffers are non-empty valid BSON.
        await foreach (var bytes in _connector.StreamAllDocumentsAsync(
            TestDatabaseName, "smoke_test"))
        {
            Assert.IsTrue(bytes.Length >= 5,
                "Minimum BSON document is 5 bytes (empty document).");
        }
    }

    [TestMethod]
    public async Task StreamChangedDocumentsAsync_WithFutureAnchor_YieldsNoDocuments()
    {
        RequireIntegration();
        // A very large fromTimestamp means no documents qualify
        long futureTs = DateTimeOffset.UtcNow.AddYears(10).ToUnixTimeSeconds();

        var count = 0;
        await foreach (var _ in _connector.StreamChangedDocumentsAsync(
            TestDatabaseName, "smoke_test", futureTs, bufferSeconds: 0))
            count++;

        Assert.AreEqual(0, count,
            "No documents should be returned for a future anchor timestamp.");
    }
}

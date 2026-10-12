using MongoCustody.Core.Models;

namespace MongoCustody.Connectors.Tests;

/// <summary>
/// Unit tests for the branches of <see cref="MongoDbRestoreTargetConnector.CreateCollectionAsync"/>
/// that resolve without any network call — engine-type detection and the
/// no-op/warning short-circuits. <c>MongoClient</c> construction is lazy, so
/// these connection strings are never dialed. Full command-building against
/// a live Cosmos account needs manual/live verification — it cannot be
/// exercised by a Docker-based MongoDB container (<c>customAction</c> is
/// Cosmos-only).
/// </summary>
[TestClass]
public sealed class MongoDbRestoreTargetConnectorTests
{
    private const string CosmosConnectionString =
        "mongodb://fake-account:key@fake-account.mongo.cosmos.azure.com:10255/?ssl=true";
    private const string StandardConnectionString =
        "mongodb://localhost:27017";

    [TestMethod]
    public async Task CreateCollectionAsync_CosmosTarget_NoShardKeyNoIndexes_NoOpReturnsNull()
    {
        await using var target = new MongoDbRestoreTargetConnector(CosmosConnectionString);

        var warning = await target.CreateCollectionAsync(
            "db", "coll", null, [], CancellationToken.None);

        Assert.IsNull(warning);
    }

    [TestMethod]
    public async Task CreateCollectionAsync_StandardTarget_ShardKeyFieldPresent_ReturnsWarning()
    {
        await using var target = new MongoDbRestoreTargetConnector(StandardConnectionString);

        var warning = await target.CreateCollectionAsync(
            "db", "coll", "_pk", [], CancellationToken.None);

        Assert.IsNotNull(warning);
        StringAssert.Contains(warning, "standard MongoDB");
    }

    [TestMethod]
    public async Task CreateCollectionAsync_StandardTarget_NoShardKeyNoIndexes_ReturnsNull()
    {
        await using var target = new MongoDbRestoreTargetConnector(StandardConnectionString);

        var warning = await target.CreateCollectionAsync(
            "db", "coll", null, [], CancellationToken.None);

        Assert.IsNull(warning);
    }
}

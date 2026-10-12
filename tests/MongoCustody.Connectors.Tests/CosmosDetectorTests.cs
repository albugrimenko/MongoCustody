namespace MongoCustody.Connectors.Tests;

[TestClass]
public sealed class CosmosDetectorTests
{
    [TestMethod]
    [DataRow("mongodb://user:pass@my-account.mongo.cosmos.azure.com:10255/?ssl=true")]
    [DataRow("mongodb://user:pass@MY-ACCOUNT.MONGO.COSMOS.AZURE.COM:10255/?ssl=true")]
    [DataRow("mongodb://user:pass@my-account.documents.azure.com:10255/?ssl=true")]
    public void IsCosmos_CosmosHost_ReturnsTrue(string connectionString)
    {
        Assert.IsTrue(CosmosDetector.IsCosmos(connectionString));
    }

    [TestMethod]
    [DataRow("mongodb://localhost:27017")]
    [DataRow("mongodb://user:pass@my-cluster.abc123.mongodb.net/?retryWrites=true")]
    [DataRow("mongodb+srv://user:pass@cluster0.mongodb.net")]
    public void IsCosmos_NonCosmosHost_ReturnsFalse(string connectionString)
    {
        Assert.IsFalse(CosmosDetector.IsCosmos(connectionString));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("not a connection string")]
    public void IsCosmos_InvalidOrEmpty_ReturnsFalse(string connectionString)
    {
        Assert.IsFalse(CosmosDetector.IsCosmos(connectionString));
    }
}

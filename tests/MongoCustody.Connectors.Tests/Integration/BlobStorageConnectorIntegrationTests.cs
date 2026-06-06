using Azure.Storage.Blobs;
using MongoCustody.Connectors.Secrets;
using MongoCustody.Core.Models;

namespace MongoCustody.Connectors.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="BlobStorageConnector"/> against Azurite.
///
/// These tests are skipped unless <c>MONGOCUSTODY_INTEGRATION_TESTS=1</c>.
/// Start Azurite before running:
/// <code>azurite --silent --location ./tmp/azurite</code>
/// or via Docker Compose in the tests/ directory.
/// </summary>
[TestClass]
public sealed class BlobStorageConnectorIntegrationTests : IntegrationTestBase
{
    private BlobStorageConnector _connector = null!;
    private BlobContainerClient _containerClient = null!;

    [TestInitialize]
    public async Task SetUp()
    {
        RequireIntegration();

        _containerClient = new BlobContainerClient(
            AzuriteConnectionString, TestContainerName);
        await _containerClient.CreateIfNotExistsAsync();

        var config = new MongoCustodyConfiguration
        {
            StorageContainerName = TestContainerName,
            StorageAccountEndpoint = "http://127.0.0.1:10000/devstoreaccount1"
        };

        // Use the connection-string path via EnvironmentSecretProvider
        Environment.SetEnvironmentVariable(
            "MONGOCUSTODY_STORAGE_CONNECTION_STRING", AzuriteConnectionString);

        _connector = new BlobStorageConnector(
            config, new EnvironmentSecretProvider());
    }

    [TestCleanup]
    public async Task TearDown()
    {
        Environment.SetEnvironmentVariable(
            "MONGOCUSTODY_STORAGE_CONNECTION_STRING", null);
        if (_containerClient is not null)
            await _containerClient.DeleteIfExistsAsync();
    }

    [TestMethod]
    public async Task WriteAndReadManifest_RoundTrips()
    {
        RequireIntegration();
        const string folder = "full/2025/Q1/test-backup-id/";
        const string json = """{"schemaVersion":1,"backupId":"test-backup-id"}""";

        await _connector.WriteManifestAsync(folder, json);
        var result = await _connector.ReadManifestAsync(folder);

        Assert.AreEqual(json, result);
    }

    [TestMethod]
    public async Task ReadManifest_ReturnsNull_WhenNotExists()
    {
        RequireIntegration();
        var result = await _connector.ReadManifestAsync("full/nonexistent/folder/");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task BlobExistsAsync_ReturnsTrue_AfterWrite()
    {
        RequireIntegration();
        const string folder = "full/2025/Q1/exists-test/";
        await _connector.WriteManifestAsync(folder, "{}");

        var exists = await _connector.BlobExistsAsync(
            StoragePathHelper.ManifestBlobPath(folder));
        Assert.IsTrue(exists);
    }

    [TestMethod]
    public async Task DeleteBlobAsync_RemovesBlob()
    {
        RequireIntegration();
        const string folder = "full/2025/Q1/delete-test/";
        await _connector.WriteManifestAsync(folder, "{}");

        var blobPath = StoragePathHelper.ManifestBlobPath(folder);
        await _connector.DeleteBlobAsync(blobPath);

        var exists = await _connector.BlobExistsAsync(blobPath);
        Assert.IsFalse(exists);
    }

    [TestMethod]
    public async Task OpenWriteAndRead_RoundTripsBytes()
    {
        RequireIntegration();
        const string blobPath = "full/2025/Q1/rw-test/mydb.col.part-001.bson.gz";
        var expected = new byte[] { 0x01, 0x02, 0x03, 0xAB, 0xCD };

        await using (var ws = await _connector.OpenWriteAsync(blobPath))
            await ws.WriteAsync(expected);

        await using var rs = await _connector.OpenReadAsync(blobPath);
        using var ms = new MemoryStream();
        await rs.CopyToAsync(ms);

        CollectionAssert.AreEqual(expected, ms.ToArray());
    }

    [TestMethod]
    public async Task ListFoldersAsync_ReturnsFoldersUnderPrefix()
    {
        RequireIntegration();
        await _connector.WriteManifestAsync("full/2025/Q1/id-a/", "{}");
        await _connector.WriteManifestAsync("full/2025/Q1/id-b/", "{}");

        var folders = new List<string>();
        await foreach (var f in _connector.ListFoldersAsync("full/2025/Q1/"))
            folders.Add(f);

        Assert.IsTrue(folders.Any(f => f.Contains("id-a")));
        Assert.IsTrue(folders.Any(f => f.Contains("id-b")));
    }

    [TestMethod]
    public async Task CheckHealthAsync_ReturnsHealthy_AgainstAzurite()
    {
        RequireIntegration();
        var (isHealthy, latencyMs, error) = await _connector.CheckHealthAsync();
        Assert.IsTrue(isHealthy, $"Health check failed: {error}");
        Assert.IsTrue(latencyMs >= 0);
    }
}

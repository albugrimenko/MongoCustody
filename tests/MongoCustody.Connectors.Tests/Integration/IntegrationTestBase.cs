namespace MongoCustody.Connectors.Tests.Integration;

/// <summary>
/// Base class for all connector integration tests.
///
/// Integration tests require live infrastructure (Azurite for Blob Storage
/// and a MongoDB-compatible endpoint for the MongoDB connector tests).
/// To avoid breaking CI pipelines that have no emulators, every test
/// method in a derived class must call <see cref="RequireIntegration"/>
/// as its first statement.
///
/// To run integration tests locally:
/// <code>
/// $env:MONGOCUSTODY_INTEGRATION_TESTS = "1"
/// dotnet test tests/MongoCustody.Connectors.Tests --filter "Category=Integration"
/// </code>
///
/// Docker Compose alternative (starts both Azurite and MongoDB):
/// <code>
/// docker compose -f tests/docker-compose.yml up -d
/// $env:MONGOCUSTODY_INTEGRATION_TESTS = "1"
/// dotnet test tests/MongoCustody.Connectors.Tests --filter "Category=Integration"
/// docker compose -f tests/docker-compose.yml down
/// </code>
/// </summary>
[TestCategory("Integration")]
public abstract class IntegrationTestBase
{
    /// <summary>
    /// Call at the start of every integration test method.
    /// Marks the test as inconclusive (not failed) when the
    /// <c>MONGOCUSTODY_INTEGRATION_TESTS</c> environment variable is absent
    /// or not set to <c>1</c> / <c>true</c>.
    /// </summary>
    protected static void RequireIntegration()
    {
        var flag = Environment.GetEnvironmentVariable("MONGOCUSTODY_INTEGRATION_TESTS");
        if (flag is not ("1" or "true" or "True"))
            Assert.Inconclusive(
                "Integration tests skipped. " +
                "Set MONGOCUSTODY_INTEGRATION_TESTS=1 to run them.");
    }

    // ------------------------------------------------------------------ //
    //  Shared emulator connection strings (Azurite defaults)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Azurite local emulator connection string (default well-known key).
    /// </summary>
    protected const string AzuriteConnectionString =
        "DefaultEndpointsProtocol=http;" +
        "AccountName=devstoreaccount1;" +
        "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
        "BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;";

    /// <summary>
    /// MongoDB local emulator connection string (default port 27017).
    /// </summary>
    protected const string MongoLocalConnectionString =
        "mongodb://localhost:27017";

    /// <summary>
    /// Integration test database name — isolated from any production data.
    /// </summary>
    protected const string TestDatabaseName = "_mongocustody_integration_test";

    /// <summary>
    /// Integration test container name in Azurite.
    /// </summary>
    protected const string TestContainerName = "integration-test-backups";
}

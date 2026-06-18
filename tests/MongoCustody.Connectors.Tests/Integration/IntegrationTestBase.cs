using MongoCustody.Connectors.Tests.Infrastructure;

namespace MongoCustody.Connectors.Tests.Integration;

/// <summary>
/// Base class for all connector integration tests.
///
/// <para>
/// Infrastructure (Azurite + MongoDB) is started automatically once per test
/// run by <see cref="ContainerFixture"/> using Testcontainers. No manual
/// Docker Compose or environment variable is required. Docker must be
/// running on the host; if it is unavailable Testcontainers throws a clear
/// startup error.
/// </para>
///
/// <para>
/// Each test must use a unique blob folder prefix or MongoDB collection name
/// to avoid interference when tests run in parallel.
/// </para>
/// </summary>
[TestCategory("Integration")]
public abstract class IntegrationTestBase
{
    // ------------------------------------------------------------------ //
    //  Guard — call at the top of every [TestInitialize]
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Marks the test Inconclusive when Docker is unavailable.
    /// Call this as the first statement of every <c>[TestInitialize]</c>.
    /// </summary>
    protected static void RequireContainers()
        => TestContainerContext.RequireContainers();

    // ------------------------------------------------------------------ //
    //  Connection strings — backed by Testcontainers at runtime
    // ------------------------------------------------------------------ //

    /// <summary>Azurite connection string for the running test container.</summary>
    protected static string AzuriteConnectionString
        => TestContainerContext.AzuriteConnectionString;

    /// <summary>MongoDB connection string for the running test container.</summary>
    protected static string MongoConnectionString
        => TestContainerContext.MongoConnectionString;

    // ------------------------------------------------------------------ //
    //  Shared constants
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Integration test database name — isolated from any production data.
    /// </summary>
    protected const string TestDatabaseName = "_mongocustody_integration_test";

    /// <summary>
    /// Integration test blob container name in Azurite.
    /// </summary>
    protected const string TestContainerName = "integration-test-backups";
}

using Testcontainers.Azurite;
using Testcontainers.MongoDb;

namespace MongoCustody.Connectors.Tests.Infrastructure;

/// <summary>
/// Holds singleton Testcontainers instances for the entire test assembly.
/// Containers are started once by <see cref="ContainerFixture"/> and shared
/// across all integration tests, keeping total startup cost to a single
/// Docker pull + container boot per test run.
///
/// <para>
/// Startup is non-throwing: if Docker is unavailable the error is stored and
/// surfaced by <see cref="RequireContainers"/> (called from each integration
/// test initializer), which marks those tests as <em>Inconclusive</em> rather
/// than failing the whole assembly.
/// </para>
/// </summary>
internal static class TestContainerContext
{
    private static AzuriteContainer? _azurite;
    private static MongoDbContainer? _mongo;
    private static Exception?        _startupError;

    /// <summary>
    /// Azurite connection string for the running container.
    /// Throws <see cref="InvalidOperationException"/> if containers never started.
    /// </summary>
    internal static string AzuriteConnectionString =>
        _azurite?.GetConnectionString()
        ?? throw new InvalidOperationException("Azurite container is not running.");

    /// <summary>
    /// MongoDB connection string for the running container.
    /// Throws <see cref="InvalidOperationException"/> if containers never started.
    /// </summary>
    internal static string MongoConnectionString =>
        _mongo?.GetConnectionString()
        ?? throw new InvalidOperationException("MongoDB container is not running.");

    /// <summary>
    /// Marks the calling test <see cref="Assert.Inconclusive"/> when Docker
    /// is unavailable or the containers failed to start.
    /// Call this at the top of every integration test <c>[TestInitialize]</c>.
    /// </summary>
    internal static void RequireContainers()
    {
        if (_startupError is not null)
            Assert.Inconclusive(
                $"Integration tests skipped — Docker containers did not start: " +
                $"{_startupError.Message}");
    }

    // ------------------------------------------------------------------ //
    //  Lifecycle — called by ContainerFixture [AssemblyInitialize/Cleanup]
    // ------------------------------------------------------------------ //

    internal static async Task StartAsync()
    {
        try
        {
            _azurite = new AzuriteBuilder()
                .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
                .Build();

            _mongo = new MongoDbBuilder()
                .WithImage("mongo:7")
                .Build();

            await Task.WhenAll(_azurite.StartAsync(), _mongo.StartAsync())
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Store the error; individual integration tests will call
            // RequireContainers() and be marked inconclusive rather than
            // failing the whole assembly.
            _startupError = ex;
            _azurite      = null;
            _mongo        = null;
        }
    }

    internal static async Task StopAsync()
    {
        List<Task> tasks = [];
        if (_azurite is not null) tasks.Add(_azurite.DisposeAsync().AsTask());
        if (_mongo   is not null) tasks.Add(_mongo.DisposeAsync().AsTask());
        if (tasks.Count > 0)
            await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}

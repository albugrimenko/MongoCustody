namespace MongoCustody.Connectors.Tests.Infrastructure;

/// <summary>
/// MSTest assembly-level fixture that starts and stops the Testcontainers
/// Docker containers (Azurite + MongoDB) once per test run.
///
/// <para>
/// Containers are shared across all tests in the assembly. Each test that
/// uses storage or MongoDB must isolate its data (unique folder prefixes /
/// database names) so tests can run in parallel without interference.
/// </para>
///
/// <para>
/// Docker must be running on the host. If Docker is unavailable, the
/// container startup will throw a clear error indicating what is missing.
/// </para>
/// </summary>
[TestClass]
public sealed class ContainerFixture
{
    [AssemblyInitialize]
    public static async Task StartContainersAsync(TestContext _)
        => await TestContainerContext.StartAsync().ConfigureAwait(false);

    [AssemblyCleanup]
    public static async Task StopContainersAsync()
        => await TestContainerContext.StopAsync().ConfigureAwait(false);
}

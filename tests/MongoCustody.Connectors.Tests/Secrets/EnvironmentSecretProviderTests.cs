namespace MongoCustody.Connectors.Tests.Secrets;

[TestClass]
public sealed class EnvironmentSecretProviderTests
{
    [TestMethod]
    public async Task GetSecretAsync_ReturnsValue_WhenEnvVarIsSet()
    {
        const string key           = "MONGOCUSTODY_MONGO_CONNECTION_STRING";
        const string expectedValue = "super-secret-42";

        Environment.SetEnvironmentVariable(key, expectedValue);
        try
        {
            var provider = new EnvironmentSecretProvider();
            var result   = await provider.GetSecretAsync(key);
            Assert.AreEqual(expectedValue, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [TestMethod]
    public async Task GetSecretAsync_ReturnsNull_WhenEnvVarIsAbsent()
    {
        const string key = "MONGOCUSTODY_ABSENT_KEY_XYZ";
        Environment.SetEnvironmentVariable(key, null);

        var provider = new EnvironmentSecretProvider();
        var result   = await provider.GetSecretAsync(key);
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GetSecretAsync_IsCancellable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // EnvironmentSecretProvider is synchronous — completes even with an
        // already-cancelled token because no I/O is performed.
        var provider = new EnvironmentSecretProvider();
        var result   = await provider.GetSecretAsync("SOME_KEY", cts.Token);
        Assert.IsNull(result);
    }
}

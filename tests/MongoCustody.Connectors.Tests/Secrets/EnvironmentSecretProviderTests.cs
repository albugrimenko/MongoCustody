namespace MongoCustody.Connectors.Tests.Secrets;

[TestClass]
public sealed class EnvironmentSecretProviderTests
{
    // ------------------------------------------------------------------ //
    //  Name transformation
    // ------------------------------------------------------------------ //

    [TestMethod]
    [DataRow("mongocustody-mongo-connection-string",
             "MONGOCUSTODY_MONGO_CONNECTION_STRING")]
    [DataRow("mongocustody-storage-connection-string",
             "MONGOCUSTODY_STORAGE_CONNECTION_STRING")]
    [DataRow("simple",
             "SIMPLE")]
    [DataRow("already-UPPER",
             "ALREADY_UPPER")]
    public void ToEnvVarName_TransformsHyphensAndUppercases(
        string input, string expected)
    {
        Assert.AreEqual(expected, EnvironmentSecretProvider.ToEnvVarName(input));
    }

    // ------------------------------------------------------------------ //
    //  GetSecretAsync
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task GetSecretAsync_ReturnsValue_WhenEnvVarIsSet()
    {
        const string secretName = "test-ph1-secret-value";
        const string envVarName = "TEST_PH1_SECRET_VALUE";
        const string expectedValue = "super-secret-42";

        Environment.SetEnvironmentVariable(envVarName, expectedValue);
        try
        {
            var provider = new EnvironmentSecretProvider();
            var result = await provider.GetSecretAsync(secretName);
            Assert.AreEqual(expectedValue, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVarName, null);
        }
    }

    [TestMethod]
    public async Task GetSecretAsync_ReturnsNull_WhenEnvVarIsAbsent()
    {
        const string secretName = "test-ph1-secret-absent";
        const string envVarName = "TEST_PH1_SECRET_ABSENT";

        // Ensure the variable is not set
        Environment.SetEnvironmentVariable(envVarName, null);

        var provider = new EnvironmentSecretProvider();
        var result = await provider.GetSecretAsync(secretName);
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GetSecretAsync_IsCancellable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // EnvironmentSecretProvider is synchronous; it should complete
        // even with an already-cancelled token because no I/O is performed.
        var provider = new EnvironmentSecretProvider();
        var result = await provider.GetSecretAsync("any-secret", cts.Token);
        Assert.IsNull(result); // env var not set, so null
    }
}

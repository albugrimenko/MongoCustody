namespace MongoCustody.Core.Tests.Models;

[TestClass]
public sealed class MongoCustodyConfigurationDefaultsTests
{
    [TestMethod]
    public void DefaultStorageContainerName_IsBackups()
    {
        var config = new MongoCustodyConfiguration();
        Assert.AreEqual("backups", config.StorageContainerName);
    }

    [TestMethod]
    public void DefaultDatabases_IsEmpty()
    {
        var config = new MongoCustodyConfiguration();
        Assert.IsNotNull(config.Databases);
        Assert.IsEmpty(config.Databases);
    }

    [TestMethod]
    public void DefaultPolicy_IsNotNull()
    {
        var config = new MongoCustodyConfiguration();
        Assert.IsNotNull(config.Policy);
    }

    [TestMethod]
    public void DefaultSecrets_IsNotNull()
    {
        var config = new MongoCustodyConfiguration();
        Assert.IsNotNull(config.Secrets);
    }

    [TestMethod]
    public void DefaultSecretsProvider_IsEnvironmentVariables()
    {
        var config = new MongoCustodyConfiguration();
        Assert.AreEqual(SecretProviderType.EnvironmentVariables, config.Secrets.Provider);
    }
}

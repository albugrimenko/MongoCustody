namespace MongoCustody.Core.Models;

/// <summary>
/// Configuration for the secret provider used to resolve sensitive
/// values (connection strings, storage keys) at runtime.
/// Sensitive values must never appear in plain text in the configuration
/// file; supply them via the configured provider.
/// </summary>
public sealed class SecretsConfiguration
{
    /// <summary>
    /// Which secret provider to use.
    /// Default: <see cref="SecretProviderType.EnvironmentVariables"/>.
    /// </summary>
    public SecretProviderType Provider { get; set; } = SecretProviderType.EnvironmentVariables;

    /// <summary>
    /// Environment variable name that holds the Azure Key Vault URI.
    /// Required when <see cref="Provider"/> is
    /// <see cref="SecretProviderType.AzureKeyVault"/>.
    /// Default: <c>MONGOCUSTODY_KEYVAULT_URI</c>.
    /// </summary>
    public string KeyVaultUriKey { get; set; } = "MONGOCUSTODY_KEYVAULT_URI";

    /// <summary>
    /// Key used to look up the MongoDB connection string.
    /// For <see cref="SecretProviderType.EnvironmentVariables"/> this is the
    /// environment variable name; for <see cref="SecretProviderType.AzureKeyVault"/>
    /// it is the Key Vault secret name (must match <c>[a-zA-Z0-9-]+</c>).
    /// Default: <c>MONGOCUSTODY_MONGO_CONNECTION_STRING</c>.
    /// </summary>
    public string MongoConnectionStringKey { get; set; } =
        "MONGOCUSTODY_MONGO_CONNECTION_STRING";

    /// <summary>
    /// Key used to look up the MongoDB account endpoint URL.
    /// Used as a fallback when <see cref="MongoConnectionStringKey"/> resolves
    /// to nothing — typical for managed identity setups where the endpoint
    /// embeds no credentials.
    /// Default: <c>MONGOCUSTODY_MONGO_ENDPOINT</c>.
    /// </summary>
    public string MongoAccountEndpointKey { get; set; } =
        "MONGOCUSTODY_MONGO_ENDPOINT";

    /// <summary>
    /// Key used to look up the Azure Blob Storage connection string.
    /// For <see cref="SecretProviderType.EnvironmentVariables"/> this is the
    /// environment variable name; for <see cref="SecretProviderType.AzureKeyVault"/>
    /// it is the Key Vault secret name (must match <c>[a-zA-Z0-9-]+</c>).
    /// Default: <c>MONGOCUSTODY_STORAGE_CONNECTION_STRING</c>.
    /// </summary>
    public string StorageConnectionStringKey { get; set; } =
        "MONGOCUSTODY_STORAGE_CONNECTION_STRING";

    /// <summary>
    /// Key used to look up the Azure Blob Storage account endpoint URL.
    /// Used as a fallback when <see cref="StorageConnectionStringKey"/> resolves
    /// to nothing — typical for managed identity setups.
    /// Default: <c>MONGOCUSTODY_STORAGE_ENDPOINT</c>.
    /// </summary>
    public string StorageAccountEndpointKey { get; set; } =
        "MONGOCUSTODY_STORAGE_ENDPOINT";
}

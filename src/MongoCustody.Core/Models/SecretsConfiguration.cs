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
    /// URI of the Azure Key Vault instance.
    /// Required when <see cref="Provider"/> is
    /// <see cref="SecretProviderType.AzureKeyVault"/>.
    /// Example: <c>https://your-keyvault.vault.azure.net/</c>
    /// </summary>
    public string? KeyVaultUri { get; set; }
}

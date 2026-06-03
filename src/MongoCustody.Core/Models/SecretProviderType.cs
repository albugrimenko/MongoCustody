namespace MongoCustody.Core.Models;

/// <summary>
/// The secret provider used to resolve sensitive configuration values
/// (connection strings, storage keys).
/// </summary>
public enum SecretProviderType
{
    /// <summary>
    /// Azure Key Vault — uses <c>DefaultAzureCredential</c>.
    /// Recommended for Azure-hosted workloads (Functions, Container Apps,
    /// App Service) with a system-assigned managed identity.
    /// </summary>
    AzureKeyVault,

    /// <summary>
    /// Environment variables — reads secrets from the process environment.
    /// Suitable for local development, CI pipelines, and any host where
    /// secrets are injected as environment variables.
    /// Not recommended for production deployments.
    /// </summary>
    EnvironmentVariables
}

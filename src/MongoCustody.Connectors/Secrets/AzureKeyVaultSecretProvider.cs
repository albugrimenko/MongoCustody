using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using MongoCustody.Core.Abstractions;

namespace MongoCustody.Connectors.Secrets;

/// <summary>
/// Resolves secrets from Azure Key Vault using
/// <see cref="DefaultAzureCredential"/> (managed identity, workload
/// identity, or <c>az login</c> for local development).
///
/// Secret names in Key Vault must follow the convention documented in
/// README.md § "Key Vault secret naming convention".
/// </summary>
public sealed class AzureKeyVaultSecretProvider : ISecretProvider
{
    private readonly SecretClient _client;

    /// <param name="keyVaultUri">
    /// URI of the Key Vault instance, e.g.
    /// <c>https://your-keyvault.vault.azure.net/</c>.
    /// </param>
    public AzureKeyVaultSecretProvider(string keyVaultUri)
        : this(new SecretClient(new Uri(keyVaultUri), new DefaultAzureCredential()))
    {
    }

    /// <summary>
    /// Constructor for testing — accepts a pre-configured
    /// <see cref="SecretClient"/>.
    /// </summary>
    internal AzureKeyVaultSecretProvider(SecretClient client)
    {
        _client = client;
    }

    /// <inheritdoc />
    public async Task<string?> GetSecretAsync(
        string secretName,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _client
                .GetSecretAsync(secretName, cancellationToken: ct)
                .ConfigureAwait(false);

            return response.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }
}

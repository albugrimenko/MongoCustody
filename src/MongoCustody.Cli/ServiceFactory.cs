using System.Text.Json;
using MongoCustody.Connectors;
using MongoCustody.Connectors.Secrets;
using MongoCustody.Core;
using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;

namespace MongoCustody.Cli;

/// <summary>
/// Builds a ready-to-use <see cref="IMongoCustodyService"/> from a config
/// file path. No DI container — the CLI wires up dependencies directly.
/// </summary>
internal static class ServiceFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Loads <paramref name="configPath"/>, resolves secrets, and constructs
    /// <see cref="MongoCustodyService"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the config file is missing or malformed, or when the Key
    /// Vault URI environment variable is not set for the AzureKeyVault provider.
    /// </exception>
    internal static MongoCustodyConfiguration LoadConfig(string configPath)
    {
        if (!File.Exists(configPath))
            throw new InvalidOperationException(
                $"Config file not found: {Path.GetFullPath(configPath)}");
        try
        {
            var json = File.ReadAllText(configPath);
            return (JsonSerializer.Deserialize<CliConfiguration>(json, JsonOptions)
                    ?? throw new InvalidOperationException("Config file is empty or null."))
                .ToCore();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Config file is not valid JSON: {ex.Message}", ex);
        }
    }

    internal static IMongoCustodyService Build(string configPath)
    {
        if (!File.Exists(configPath))
            throw new InvalidOperationException(
                $"Config file not found: {Path.GetFullPath(configPath)}");

        CliConfiguration cli;
        try
        {
            var json = File.ReadAllText(configPath);
            cli = JsonSerializer.Deserialize<CliConfiguration>(json, JsonOptions)
                  ?? throw new InvalidOperationException("Config file is empty or null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Config file is not valid JSON: {ex.Message}", ex);
        }

        var config         = cli.ToCore();
        var secretProvider = BuildSecretProvider(config.Secrets);
        var source         = new MongoDbConnector(config, secretProvider);
        var storage        = new BlobStorageConnector(config, secretProvider);
        var metadata       = new BlobMetadataRepository(storage);
        var restoreFactory = new MongoDbRestoreTargetConnectorFactory();

        return new MongoCustodyService(source, storage, metadata, config, restoreFactory, secretProvider);
    }

    internal static async Task<string?> ResolveSecretAsync(
        string configPath,
        string key,
        CancellationToken ct = default)
    {
        var config = LoadConfig(configPath);
        var provider = BuildSecretProvider(config.Secrets);
        return await provider.GetSecretAsync(key, ct).ConfigureAwait(false);
    }

    private static ISecretProvider BuildSecretProvider(SecretsConfiguration secrets)
    {
        if (secrets.Provider == SecretProviderType.AzureKeyVault)
        {
            var vaultUri = Environment.GetEnvironmentVariable(secrets.KeyVaultUriKey);
            if (string.IsNullOrWhiteSpace(vaultUri))
                throw new InvalidOperationException(
                    $"Azure Key Vault provider is configured but the Key Vault URI " +
                    $"environment variable '{secrets.KeyVaultUriKey}' is not set.");

            return new AzureKeyVaultSecretProvider(vaultUri);
        }

        return new EnvironmentSecretProvider();
    }
}

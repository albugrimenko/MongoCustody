using System.Text.Json;
using MongoCustody.Connectors;
using MongoCustody.Connectors.Secrets;
using MongoCustody.Core;
using MongoCustody.Core.Abstractions;

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
    /// Loads <paramref name="configPath"/>, resolves secrets from environment
    /// variables, and constructs <see cref="MongoCustodyService"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the config file is missing or malformed.
    /// </exception>
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

        var config          = cli.ToCore();
        var secretProvider  = new EnvironmentSecretProvider();
        var source          = new MongoDbConnector(config, secretProvider);
        var storage         = new BlobStorageConnector(config, secretProvider);
        var metadata        = new BlobMetadataRepository(storage);
        var restoreFactory  = new MongoDbRestoreTargetConnectorFactory();

        return new MongoCustodyService(source, storage, metadata, config, restoreFactory);
    }
}

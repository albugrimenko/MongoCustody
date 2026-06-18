using System.Text.Json.Serialization;
using MongoCustody.Core.Models;

namespace MongoCustody.Cli;

/// <summary>
/// JSON shape of <c>mongocustody.json</c> as documented in the README.
/// Connection strings are never stored here — they come from environment
/// variables resolved by <see cref="MongoCustody.Connectors.Secrets.EnvironmentSecretProvider"/>.
/// </summary>
internal sealed class CliConfiguration
{
    [JsonPropertyName("mongoDBAccountEndpoint")]
    public string MongoDBAccountEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("storageAccountEndpoint")]
    public string StorageAccountEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("storageContainerName")]
    public string StorageContainerName { get; set; } = "backups";

    [JsonPropertyName("databases")]
    public List<DatabaseConfiguration> Databases { get; set; } = [];

    [JsonPropertyName("policy")]
    public BackupPolicy Policy { get; set; } = new();

    /// <summary>Converts to the Core configuration type.</summary>
    internal MongoCustodyConfiguration ToCore() => new()
    {
        MongoDBAccountEndpoint = MongoDBAccountEndpoint,
        StorageAccountEndpoint = StorageAccountEndpoint,
        StorageContainerName   = StorageContainerName,
        Databases              = Databases,
        Policy                 = Policy
    };
}

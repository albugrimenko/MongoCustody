using System.Text.Json.Serialization;
using MongoCustody.Core.Models;

namespace MongoCustody.Cli;

/// <summary>
/// JSON shape of <c>mongocustody.json</c> as documented in the README.
/// No URIs or connection strings are stored here — all environment-specific
/// values are resolved at runtime via the configured secret provider.
/// </summary>
internal sealed class CliConfiguration
{
    [JsonPropertyName("storageContainerName")]
    public string StorageContainerName { get; set; } = "backups";

    [JsonPropertyName("databases")]
    public List<DatabaseConfiguration> Databases { get; set; } = [];

    [JsonPropertyName("policy")]
    public BackupPolicy Policy { get; set; } = new();

    [JsonPropertyName("secrets")]
    public SecretsConfiguration Secrets { get; set; } = new();

    [JsonPropertyName("dryRun")]
    public DryRunConfiguration? DryRun { get; set; }

    internal MongoCustodyConfiguration ToCore() => new()
    {
        StorageContainerName = StorageContainerName,
        Databases            = Databases,
        Policy               = Policy,
        Secrets              = Secrets,
        DryRun               = DryRun
    };
}

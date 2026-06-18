using System.CommandLine;

namespace MongoCustody.Cli.Commands;

/// <summary>
/// Shared <see cref="Option{T}"/> instances reused across multiple commands.
/// Each call returns a new instance (System.CommandLine options cannot be shared).
/// </summary>
internal static class CommonOptions
{
    internal static Option<string> Config() =>
        new("--config", ["-c"])
        {
            Description       = "Path to the mongocustody.json configuration file.",
            DefaultValueFactory = _ => "mongocustody.json"
        };

    internal static Option<OutputFormat> Output() =>
        new("--output", ["-o"])
        {
            Description       = "Output format: text (default) or json.",
            DefaultValueFactory = _ => OutputFormat.Text
        };

    internal static Option<string> Database() =>
        new("--database", ["-d"])
        {
            Description = "Name of the MongoDB database to operate on.",
            Required    = true
        };
}

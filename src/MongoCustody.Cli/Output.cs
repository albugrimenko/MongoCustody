using System.Text.Json;
using System.Text.Json.Serialization;

namespace MongoCustody.Cli;

/// <summary>
/// Output format selected by the user via <c>--output</c>.
/// </summary>
internal enum OutputFormat { Text, Json }

/// <summary>
/// Shared output helpers used by all commands.
/// </summary>
internal static class Output
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented  = true,
        Converters     = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Serialises <paramref name="value"/> as indented JSON to stdout.</summary>
    internal static void WriteJson(object value)
        => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    /// <summary>Writes a separator line to stdout.</summary>
    internal static void WriteSeparator()
        => Console.WriteLine(new string('─', 60));

    /// <summary>
    /// Writes an error message to stderr and returns exit code 1.
    /// </summary>
    internal static int Error(string message)
    {
        Console.Error.WriteLine($"ERROR: {message}");
        return 1;
    }

    /// <summary>
    /// Writes a config/usage error to stderr and returns exit code 2.
    /// </summary>
    internal static int ConfigError(string message)
    {
        Console.Error.WriteLine($"CONFIG ERROR: {message}");
        return 2;
    }
}

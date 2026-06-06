using MongoCustody.Core.Abstractions;

namespace MongoCustody.Connectors.Secrets;

/// <summary>
/// Resolves secrets from process environment variables.
/// Secret names are transformed to environment variable names by replacing
/// hyphens with underscores and converting to uppercase.
/// Example: <c>mongocustody-mongo-connection-string</c>
///       →  <c>MONGOCUSTODY_MONGO_CONNECTION_STRING</c>
///
/// Suitable for local development, CI pipelines, and any host where
/// secrets are injected as environment variables.
/// Not recommended for production deployments — use
/// <see cref="AzureKeyVaultSecretProvider"/> instead.
/// </summary>
public sealed class EnvironmentSecretProvider : ISecretProvider
{
    /// <inheritdoc />
    public Task<string?> GetSecretAsync(
        string secretName,
        CancellationToken ct = default)
    {
        var envVarName = ToEnvVarName(secretName);
        var value = Environment.GetEnvironmentVariable(envVarName);
        return Task.FromResult(value);
    }

    /// <summary>
    /// Converts a Key Vault-style secret name to an environment variable name.
    /// </summary>
    internal static string ToEnvVarName(string secretName)
        => secretName.Replace('-', '_').ToUpperInvariant();
}

using MongoCustody.Core.Abstractions;

namespace MongoCustody.Connectors.Secrets;

/// <summary>
/// Resolves secrets from process environment variables.
/// The secret key is used as-is as the environment variable name, so keys
/// configured in <see cref="MongoCustody.Core.Models.SecretsConfiguration"/>
/// should follow the <c>UPPER_SNAKE_CASE</c> convention used by environment
/// variables (e.g. <c>MONGOCUSTODY_MONGO_CONNECTION_STRING</c>).
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
        var value = Environment.GetEnvironmentVariable(secretName);
        return Task.FromResult(value);
    }
}

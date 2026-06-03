namespace MongoCustody.Core.Abstractions;

/// <summary>
/// Resolves sensitive configuration values (connection strings, storage
/// keys) from a secret store at runtime.
/// </summary>
public interface ISecretProvider
{
    /// <summary>
    /// Retrieves the value of a named secret.
    /// </summary>
    /// <param name="secretName">
    /// The canonical secret name as defined by the Key Vault secret naming
    /// convention (e.g., <c>mongocustody-mongo-connection-string</c>).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The secret value, or <c>null</c> if the secret does not exist.
    /// </returns>
    Task<string?> GetSecretAsync(string secretName, CancellationToken ct = default);
}

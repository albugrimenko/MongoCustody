namespace MongoCustody.Core.Abstractions;

/// <summary>
/// Creates <see cref="IRestoreTargetConnector"/> instances on demand from a
/// connection string. Registered in the DI container so that
/// <see cref="MongoCustodyService"/> can create a per-request connector
/// without taking a dependency on the <c>MongoCustody.Connectors</c> assembly.
/// </summary>
public interface IRestoreTargetConnectorFactory
{
    /// <summary>
    /// Creates a new <see cref="IRestoreTargetConnector"/> connected to the
    /// MongoDB instance identified by <paramref name="connectionString"/>.
    ///
    /// The caller is responsible for disposing the returned connector after
    /// the restore or validation operation completes.
    /// </summary>
    /// <param name="connectionString">
    /// MongoDB connection string for the restore target account.
    /// Must point to an empty account that is not the production source.
    /// </param>
    IRestoreTargetConnector Create(string connectionString);
}

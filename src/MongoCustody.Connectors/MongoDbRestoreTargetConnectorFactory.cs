using MongoCustody.Core.Abstractions;

namespace MongoCustody.Connectors;

/// <summary>
/// <see cref="IRestoreTargetConnectorFactory"/> implementation that creates
/// <see cref="MongoDbRestoreTargetConnector"/> instances from a connection
/// string supplied at restore time.
///
/// Register this factory in the DI container alongside the other connector
/// implementations so that <c>MongoCustodyService</c> can create per-request
/// restore target connectors without taking a compile-time dependency on
/// <c>MongoCustody.Connectors</c>.
/// </summary>
public sealed class MongoDbRestoreTargetConnectorFactory : IRestoreTargetConnectorFactory
{
    /// <inheritdoc />
    public IRestoreTargetConnector Create(string connectionString)
        => new MongoDbRestoreTargetConnector(connectionString);
}

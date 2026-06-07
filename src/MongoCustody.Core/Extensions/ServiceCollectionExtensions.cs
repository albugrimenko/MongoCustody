using Microsoft.Extensions.DependencyInjection;
using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;

namespace MongoCustody.Core.Extensions;

/// <summary>
/// Extension methods for registering MongoCustody services with the
/// Microsoft dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="MongoCustodyService"/> as the
    /// <see cref="IMongoCustodyService"/> singleton and binds the provided
    /// <see cref="MongoCustodyConfiguration"/> as a singleton.
    ///
    /// <para>
    /// The caller is responsible for registering the connector
    /// implementations (<see cref="IBackupSourceConnector"/>,
    /// <see cref="IStorageConnector"/>, <see cref="IMetadataRepository"/>,
    /// and <see cref="ISecretProvider"/>) before or after calling this
    /// method. They are typically registered in the
    /// <c>MongoCustody.Connectors</c> assembly via its own extension method.
    /// </para>
    ///
    /// <example>
    /// <code>
    /// // Program.cs
    /// var config = builder.Configuration
    ///     .GetSection("MongoCustody")
    ///     .Get&lt;MongoCustodyConfiguration&gt;()!;
    ///
    /// builder.Services.AddMongoCustody(config);
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">
    /// The resolved <see cref="MongoCustodyConfiguration"/>. Must not be null.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddMongoCustody(
        this IServiceCollection services,
        MongoCustodyConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(configuration);
        services.AddSingleton<IMongoCustodyService, MongoCustodyService>();

        return services;
    }
}

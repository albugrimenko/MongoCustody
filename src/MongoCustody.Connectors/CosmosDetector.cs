namespace MongoCustody.Connectors;

/// <summary>
/// Detects whether a MongoDB connection string points at Azure Cosmos DB for
/// MongoDB (RU-based API) rather than standard MongoDB. Used to select
/// between the Cosmos-only <c>customAction</c> extension commands and plain
/// MongoDB wire-protocol commands.
///
/// <para>
/// Detection is based on the connection string's host suffix. Cosmos DB for
/// MongoDB (RU-based) accounts are always reachable at
/// <c>*.mongo.cosmos.azure.com</c> (current) or <c>*.documents.azure.com</c>
/// (legacy). A private-endpoint connection string using a custom hostname
/// will not be detected as Cosmos — out of scope unless that becomes a real
/// requirement.
/// </para>
/// </summary>
internal static class CosmosDetector
{
    private static readonly string[] CosmosHostSuffixes =
    [
        ".mongo.cosmos.azure.com",
        ".documents.azure.com"
    ];

    /// <summary>
    /// Returns <c>true</c> when <paramref name="connectionString"/> targets
    /// Azure Cosmos DB for MongoDB (RU-based API).
    /// </summary>
    public static bool IsCosmos(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;

        try
        {
            var url = new MongoDB.Driver.MongoUrl(connectionString);
            foreach (var server in url.Servers)
            {
                if (CosmosHostSuffixes.Any(suffix =>
                        server.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            return false;
        }
        catch (Exception)
        {
            // Malformed/unparsable connection string — treat as non-Cosmos.
            // Whatever downstream call uses the connection string will
            // surface the real error.
            return false;
        }
    }
}

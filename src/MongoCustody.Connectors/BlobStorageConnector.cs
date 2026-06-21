using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace MongoCustody.Connectors;

/// <summary>
/// <see cref="IStorageConnector"/> implementation backed by Azure Blob
/// Storage. Authenticates via <see cref="DefaultAzureCredential"/> when
/// only a storage account endpoint is configured, or via a connection
/// string resolved from the secret provider for development and CI.
///
/// The container client is initialised lazily on first use so that no
/// I/O — including Key Vault secret resolution — occurs during DI
/// container construction. This is safe on all Azure Linux hosting
/// environments (Functions, Container Apps, App Service) and avoids
/// thread-pool starvation on cold starts.
///
/// All chunk blobs are written as block blobs. The blob is not visible
/// in the container until all blocks are committed (i.e. the write stream
/// is disposed), ensuring the storage layer never contains a partial chunk.
/// </summary>
public sealed class BlobStorageConnector : IStorageConnector
{
    private readonly MongoCustodyConfiguration _config;
    private readonly ISecretProvider _secretProvider;
    private readonly long _blockSizeBytes;

    // Lazily initialised on first use — avoids blocking DI construction
    // and eliminates the synchronous Key Vault call at startup.
    private BlobContainerClient? _container;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <param name="configuration">Root MongoCustody configuration.</param>
    /// <param name="secretProvider">
    /// Used to resolve the optional storage connection string using the key
    /// defined in <see cref="SecretsConfiguration.StorageConnectionStringKey"/>.
    /// When the secret is absent the connector uses the endpoint URL with
    /// <see cref="DefaultAzureCredential"/>.
    /// </param>
    public BlobStorageConnector(
        MongoCustodyConfiguration configuration,
        ISecretProvider secretProvider)
    {
        _config = configuration;
        _secretProvider = secretProvider;
        _blockSizeBytes = (long)configuration.Policy.BlobBlockSizeMb * 1024 * 1024;
    }

    /// <summary>Internal constructor for unit testing — bypasses lazy init.</summary>
    internal BlobStorageConnector(
        BlobContainerClient containerClient,
        long blockSizeBytes)
    {
        _config = null!;
        _secretProvider = null!;
        _container = containerClient;
        _blockSizeBytes = blockSizeBytes;
    }

    /// <inheritdoc />
    public async Task<Stream> OpenWriteAsync(
        string blobPath,
        CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct).ConfigureAwait(false);
        var blob = container.GetBlockBlobClient(blobPath);
        return await blob
            .OpenWriteAsync(overwrite: true, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(
        string blobPath,
        CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct).ConfigureAwait(false);
        var blob = container.GetBlobClient(blobPath);
        return await blob
            .OpenReadAsync(cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteManifestAsync(
        string folderPath,
        string manifestJson,
        CancellationToken ct = default)
    {
        var blobPath = StoragePathHelper.ManifestBlobPath(folderPath);
        var container = await GetContainerAsync(ct).ConfigureAwait(false);
        var blob = container.GetBlobClient(blobPath);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson));
        await blob
            .UploadAsync(stream, overwrite: true, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> ReadManifestAsync(
        string folderPath,
        CancellationToken ct = default)
    {
        var blobPath = StoragePathHelper.ManifestBlobPath(folderPath);
        var container = await GetContainerAsync(ct).ConfigureAwait(false);
        var blob = container.GetBlobClient(blobPath);

        try
        {
            var response = await blob
                .DownloadContentAsync(cancellationToken: ct)
                .ConfigureAwait(false);
            return response.Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListFoldersAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct).ConfigureAwait(false);

        await foreach (var page in container
            .GetBlobsByHierarchyAsync(
                traits: default,
                states: default,
                delimiter: "/",
                prefix: prefix,
                cancellationToken: ct)
            .AsPages()
            .ConfigureAwait(false))
        {
            foreach (var item in page.Values)
            {
                if (item.IsPrefix)
                    yield return item.Prefix;
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> BlobExistsAsync(
        string blobPath,
        CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct).ConfigureAwait(false);
        var blob = container.GetBlobClient(blobPath);
        var response = await blob
            .ExistsAsync(cancellationToken: ct)
            .ConfigureAwait(false);
        return response.Value;
    }

    /// <inheritdoc />
    public async Task DeleteBlobAsync(
        string blobPath,
        CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct).ConfigureAwait(false);
        var blob = container.GetBlobClient(blobPath);
        await blob
            .DeleteIfExistsAsync(cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<(bool IsHealthy, long LatencyMs, string? ErrorMessage)> CheckHealthAsync(
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var container = await GetContainerAsync(ct).ConfigureAwait(false);
            await container
                .ExistsAsync(cancellationToken: ct)
                .ConfigureAwait(false);
            sw.Stop();
            return (true, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds,
                $"Blob Storage health check failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ //
    //  Lazy async initialisation
    // ------------------------------------------------------------------ //

    private async Task<BlobContainerClient> GetContainerAsync(CancellationToken ct)
    {
        if (_container is not null) return _container;

        bool acquired = false;
        try
        {
            await _initLock.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            if (_container is not null) return _container;

            var connectionString = await _secretProvider
                .GetSecretAsync(_config.Secrets.StorageConnectionStringKey, ct)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                _container = new BlobContainerClient(
                    connectionString,
                    _config.StorageContainerName);
            }
            else
            {
                // Managed identity / DefaultAzureCredential path (production).
                // DefaultAzureCredential resolves in order:
                //   1. AZURE_* env vars (service principal — CI)
                //   2. Workload Identity (AKS)
                //   3. Managed Identity (Azure Functions, Container Apps, App Service)
                //   4. Azure CLI / azd (local dev)
                // All four work correctly on Linux.
                var endpoint = await _secretProvider
                    .GetSecretAsync(_config.Secrets.StorageAccountEndpointKey, ct)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(endpoint))
                    throw new InvalidOperationException(
                        $"No Blob Storage connection string or endpoint configured. " +
                        $"Set '{_config.Secrets.StorageConnectionStringKey}' or " +
                        $"'{_config.Secrets.StorageAccountEndpointKey}'.");

                var serviceClient = new BlobServiceClient(
                    new Uri(endpoint),
                    new DefaultAzureCredential());
                _container = serviceClient.GetBlobContainerClient(
                    _config.StorageContainerName);
            }

            return _container;
        }
        finally
        {
            if (acquired) _initLock.Release();
        }
    }
}

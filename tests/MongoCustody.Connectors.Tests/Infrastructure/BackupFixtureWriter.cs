using Azure.Storage.Blobs;
using MongoDB.Bson;
using MongoCustody.Core.Models;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace MongoCustody.Connectors.Tests.Infrastructure;

/// <summary>
/// Writes pre-built backup fixtures (manifest + chunk blobs) directly into
/// an Azurite container, bypassing the backup engine.
///
/// <para>
/// Use this in tests that exercise chain resolution, restore, or validation
/// without needing a running full backup. The produced blobs are byte-for-byte
/// compatible with the format written by <c>ChunkWriter</c> and read by
/// <c>BsonChunkReader</c>:
/// <list type="bullet">
///   <item>Chunk blob — concatenated raw BSON documents, gzip-compressed.</item>
///   <item>SHA-256 — computed over the uncompressed byte stream.</item>
///   <item>Manifest — JSON serialised <see cref="BackupRecord"/> at
///         <c>{folder}manifest.json</c>.</item>
/// </list>
/// </para>
///
/// <para>
/// Each <see cref="WriteFullBackupAsync"/> call returns the completed
/// <see cref="BackupRecord"/> so callers can chain fixtures (e.g. create a
/// full backup then an incremental) using the returned <c>BackupFolder</c>
/// and <c>AnchorTs</c>.
/// </para>
/// </summary>
public sealed class BackupFixtureWriter
{
    private readonly BlobContainerClient _container;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = true,
        Converters           = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public BackupFixtureWriter(BlobContainerClient container)
    {
        _container = container;
    }

    // ------------------------------------------------------------------ //
    //  Public API
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Writes a full backup fixture for <paramref name="databaseName"/>
    /// using the provided collection map and returns the sealed
    /// <see cref="BackupRecord"/>.
    /// </summary>
    /// <param name="databaseName">MongoDB database name.</param>
    /// <param name="collections">
    /// Map of collection name → documents to include in the fixture.
    /// </param>
    /// <param name="anchorTs">
    /// Unix-second anchor timestamp. Defaults to the current UTC time.
    /// </param>
    /// <param name="backupId">Explicit backup ID. Defaults to a new GUID.</param>
    public async Task<BackupRecord> WriteFullBackupAsync(
        string databaseName,
        Dictionary<string, IReadOnlyList<BsonDocument>> collections,
        long? anchorTs  = null,
        string? backupId = null,
        CancellationToken ct = default)
    {
        var id        = backupId ?? Guid.NewGuid().ToString();
        var startedAt = DateTimeOffset.UtcNow;
        var ts        = anchorTs ?? startedAt.ToUnixTimeSeconds();
        var quarter   = QuarterLabel(startedAt);
        var folder    = $"full/{startedAt.Year}/{quarter.Split('-')[1]}/{id}/";

        var collRecords = new List<CollectionBackupRecord>();
        foreach (var (name, docs) in collections)
        {
            var collRecord = await WriteCollectionChunkAsync(
                folder, databaseName, name, docs, ct).ConfigureAwait(false);
            collRecords.Add(collRecord);
        }

        var record = new BackupRecord
        {
            BackupId       = id,
            BackupType     = BackupType.Full,
            BackupFolder   = folder,
            ParentFolder   = null,
            StartedAtUtc   = startedAt,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            QueryFromTs    = null,
            AnchorTs       = ts,
            DatabaseName   = databaseName,
            QuarterLabel   = quarter,
            Format         = BackupFormat.Bson,
            Collections    = collRecords,
            TriggeredBy    = "test-fixture",
            CorrelationId  = Guid.NewGuid().ToString()
        };

        await WriteManifestAsync(folder, record, ct).ConfigureAwait(false);
        await WriteIndexPointerAsync(id, folder, ct).ConfigureAwait(false);
        return record;
    }

    /// <summary>
    /// Writes an incremental backup fixture that logically follows
    /// <paramref name="parentFolder"/>.
    /// </summary>
    /// <param name="databaseName">MongoDB database name.</param>
    /// <param name="collections">Changed documents since the last backup.</param>
    /// <param name="queryFromTs">Start of the incremental query window.</param>
    /// <param name="anchorTs">End of the incremental query window.</param>
    /// <param name="fullBackupId">ID of the root full backup (used for path).</param>
    /// <param name="parentFolder">BackupFolder of the preceding chain step.</param>
    public async Task<BackupRecord> WriteIncrementalBackupAsync(
        string databaseName,
        Dictionary<string, IReadOnlyList<BsonDocument>> collections,
        long   queryFromTs,
        long   anchorTs,
        string fullBackupId,
        string parentFolder,
        CancellationToken ct = default)
    {
        var id        = Guid.NewGuid().ToString();
        var startedAt = DateTimeOffset.UtcNow;
        var folder    = IncrementalFolder(startedAt, fullBackupId);

        var collRecords = new List<CollectionBackupRecord>();
        foreach (var (name, docs) in collections)
        {
            var collRecord = await WriteCollectionChunkAsync(
                folder, databaseName, name, docs, ct).ConfigureAwait(false);
            collRecords.Add(collRecord);
        }

        var record = new BackupRecord
        {
            BackupId       = id,
            BackupType     = BackupType.Incremental,
            BackupFolder   = folder,
            ParentFolder   = parentFolder,
            StartedAtUtc   = startedAt,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            QueryFromTs    = queryFromTs,
            AnchorTs       = anchorTs,
            DatabaseName   = databaseName,
            QuarterLabel   = QuarterLabel(startedAt),
            Format         = BackupFormat.Bson,
            Collections    = collRecords,
            TriggeredBy    = "test-fixture",
            CorrelationId  = Guid.NewGuid().ToString()
        };

        await WriteManifestAsync(folder, record, ct).ConfigureAwait(false);
        await WriteIndexPointerAsync(id, folder, ct).ConfigureAwait(false);
        return record;
    }

    // ------------------------------------------------------------------ //
    //  Chunk writing
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Serialises <paramref name="docs"/> as concatenated BSON bytes,
    /// gzip-compresses them, computes SHA-256 over the uncompressed content,
    /// and uploads the chunk blob to Azurite.
    /// </summary>
    private async Task<CollectionBackupRecord> WriteCollectionChunkAsync(
        string folder,
        string databaseName,
        string collectionName,
        IReadOnlyList<BsonDocument> docs,
        CancellationToken ct)
    {
        if (docs.Count == 0)
        {
            return new CollectionBackupRecord
            {
                CollectionName = collectionName,
                TotalDocuments = 0,
                ChunkCount     = 0,
                ExportedAtUtc  = DateTimeOffset.UtcNow,
                Chunks         = []
            };
        }

        var blobPath = $"{folder}{databaseName}.{collectionName}.part-001.bson.gz";
        var blob = _container.GetBlobClient(blobPath);

        string sha256Hex;
        long compressedBytes;

        await using (var blobStream = await blob
            .OpenWriteAsync(overwrite: true, cancellationToken: ct)
            .ConfigureAwait(false))
        {
            // Track compressed size
            var countingStream = new CountingStream(blobStream);

            // SHA-256 is computed over the uncompressed concatenated BSON bytes
            using var sha256 = SHA256.Create();

            await using (var gzip = new GZipStream(
                countingStream, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            {
                foreach (var doc in docs)
                {
                    var bsonBytes = doc.ToBson();
                    sha256.TransformBlock(bsonBytes, 0, bsonBytes.Length, null, 0);
                    await gzip.WriteAsync(bsonBytes, ct).ConfigureAwait(false);
                }
            }

            sha256.TransformFinalBlock([], 0, 0);
            sha256Hex      = Convert.ToHexStringLower(sha256.Hash!);
            compressedBytes = countingStream.BytesWritten;
        }

        var chunk = new ChunkRecord
        {
            ChunkIndex    = 1,
            BlobPath      = blobPath,
            DocumentCount = docs.Count,
            Sha256Hash    = sha256Hex,
            SizeBytes     = compressedBytes
        };

        return new CollectionBackupRecord
        {
            CollectionName = collectionName,
            TotalDocuments = docs.Count,
            ChunkCount     = 1,
            ExportedAtUtc  = DateTimeOffset.UtcNow,
            Chunks         = [chunk]
        };
    }

    // ------------------------------------------------------------------ //
    //  Manifest / index helpers
    // ------------------------------------------------------------------ //

    private async Task WriteManifestAsync(
        string folder, BackupRecord record, CancellationToken ct)
    {
        var json  = JsonSerializer.Serialize(record, JsonOptions);
        var blob  = _container.GetBlobClient($"{folder}manifest.json");
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        await blob.UploadAsync(ms, overwrite: true, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    private async Task WriteIndexPointerAsync(
        string backupId, string folder, CancellationToken ct)
    {
        var blob  = _container.GetBlobClient($"_index/{backupId}");
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(folder));
        await blob.UploadAsync(ms, overwrite: true, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ //
    //  Path helpers (mirrors BackupPathBuilder conventions)
    // ------------------------------------------------------------------ //

    private static string QuarterLabel(DateTimeOffset utc)
    {
        int q = ((utc.Month - 1) / 3) + 1;
        return $"{utc.Year}-Q{q}";
    }

    private static string IncrementalFolder(DateTimeOffset utc, string fullBackupId)
    {
        int q    = ((utc.Month - 1) / 3) + 1;
        var ts   = utc.ToString("yyyy-MM-ddTHHmmZ");
        return $"incremental/{utc.Year}/Q{q}/{fullBackupId}/{ts}/";
    }

    // ------------------------------------------------------------------ //
    //  CountingStream — measures compressed bytes written to the blob
    // ------------------------------------------------------------------ //

    private sealed class CountingStream(Stream inner) : Stream
    {
        internal long BytesWritten { get; private set; }

        public override bool CanRead  => false;
        public override bool CanSeek  => false;
        public override bool CanWrite => true;
        public override long Length   => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct).ConfigureAwait(false);
            BytesWritten += buffer.Length;
        }

        public override void  Flush()  => inner.Flush();
        public override Task  FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override void  SetLength(long v)  => throw new NotSupportedException();
        public override long  Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override int   Read(byte[] b, int o, int c) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}

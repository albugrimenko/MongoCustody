using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoCustody.Core.Engines;
using MongoCustody.Core.Models;

namespace MongoCustody.Core.Tests.Engines;

/// <summary>
/// Unit tests for <see cref="ChunkWriter"/>.
///
/// Uses an in-memory <see cref="IStorageConnector"/> stub — no Azure
/// infrastructure required.
/// </summary>
[TestClass]
public sealed class ChunkWriterTests
{
    // ------------------------------------------------------------------ //
    //  Helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Minimal IStorageConnector that captures blob writes in memory.
    /// </summary>
    private sealed class InMemoryStorageConnector : IStorageConnector
    {
        public readonly Dictionary<string, MemoryStream> Blobs = new();

        public Task<Stream> OpenWriteAsync(string blobPath, CancellationToken ct = default)
        {
            var ms = new MemoryStream();
            Blobs[blobPath] = ms;
            return Task.FromResult<Stream>(new NonDisposingMemoryStream(ms));
        }

        public Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct = default)
        {
            if (!Blobs.TryGetValue(blobPath, out var ms))
                throw new FileNotFoundException(blobPath);
            ms.Position = 0;
            return Task.FromResult<Stream>(ms);
        }

        public Task WriteManifestAsync(string folderPath, string manifestJson, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string?> ReadManifestAsync(string folderPath, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public IAsyncEnumerable<string> ListFoldersAsync(string prefix, CancellationToken ct = default)
            => AsyncEnumerable.Empty<string>();

        public Task<bool> BlobExistsAsync(string blobPath, CancellationToken ct = default)
            => Task.FromResult(Blobs.ContainsKey(blobPath));

        public Task DeleteBlobAsync(string blobPath, CancellationToken ct = default)
        {
            Blobs.Remove(blobPath);
            return Task.CompletedTask;
        }

        public Task<(bool IsHealthy, long LatencyMs, string? ErrorMessage)> CheckHealthAsync(
            CancellationToken ct = default)
            => Task.FromResult((true, 0L, (string?)null));
    }

    /// <summary>
    /// MemoryStream wrapper that ignores Dispose so the caller can still
    /// read the buffer after ChunkWriter seals the chunk.
    /// </summary>
    private sealed class NonDisposingMemoryStream(MemoryStream inner) : Stream
    {
        public override bool CanRead  => inner.CanRead;
        public override bool CanSeek  => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length   => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void  Flush()  => inner.Flush();
        public override Task  FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int   Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long  Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void  SetLength(long value) => inner.SetLength(value);
        public override void  Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override Task  WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => inner.WriteAsync(buffer, offset, count, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            => inner.WriteAsync(buffer, ct);

        protected override void Dispose(bool disposing) { /* intentionally no-op */ }
    }

    private static BackupPolicy MakePolicy(int chunkSizeGb = 10) => new()
    {
        ChunkSizeGb            = chunkSizeGb,
        BlobBlockSizeMb        = 100,
        CompressionLevel       = MongoCustody.Core.Models.CompressionLevel.Fastest,
        MaxParallelCollections = 4
    };

    /// <summary>Creates a minimal 5-byte BSON document: { } (empty document).</summary>
    private static ReadOnlyMemory<byte> EmptyBsonDoc()
    {
        // BSON empty document: int32(5) + 0x00 terminator = [0x05,0x00,0x00,0x00,0x00]
        return new byte[] { 0x05, 0x00, 0x00, 0x00, 0x00 };
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> YieldDocs(
        int count, ReadOnlyMemory<byte> doc)
    {
        for (int i = 0; i < count; i++)
            yield return doc;
        await Task.CompletedTask; // satisfy async requirement
    }

    // ------------------------------------------------------------------ //
    //  Tests
    // ------------------------------------------------------------------ //

    [TestMethod]
    public async Task WriteCollectionChunks_ZeroDocuments_ProducesNoChunks()
    {
        var storage = new InMemoryStorageConnector();
        var writer  = new ChunkWriter(storage, MakePolicy());

        var result = await writer.WriteCollectionChunksAsync(
            "full/2025/Q1/abc/",
            "mydb",
            "col",
            AsyncEnumerable.Empty<ReadOnlyMemory<byte>>(),
            CancellationToken.None);

        Assert.AreEqual(0, result.TotalDocuments);
        Assert.AreEqual(0, result.ChunkCount);
        Assert.AreEqual(0, result.Chunks.Count);
        Assert.AreEqual("col", result.CollectionName);
        Assert.AreEqual(0, storage.Blobs.Count);
    }

    [TestMethod]
    public async Task WriteCollectionChunks_FewDocuments_ProducesSingleChunk()
    {
        var storage = new InMemoryStorageConnector();
        var writer  = new ChunkWriter(storage, MakePolicy(chunkSizeGb: 10));
        var doc     = EmptyBsonDoc();

        var result = await writer.WriteCollectionChunksAsync(
            "full/2025/Q1/abc/",
            "mydb",
            "col",
            YieldDocs(3, doc),
            CancellationToken.None);

        Assert.AreEqual(3, result.TotalDocuments);
        Assert.AreEqual(1, result.ChunkCount);
        Assert.AreEqual(1, result.Chunks.Count);

        var chunk = result.Chunks[0];
        Assert.AreEqual(1, chunk.ChunkIndex);
        Assert.AreEqual("full/2025/Q1/abc/mydb.col.part-001.bson.gz", chunk.BlobPath);
        Assert.AreEqual(3, chunk.DocumentCount);
        Assert.IsFalse(string.IsNullOrEmpty(chunk.Sha256Hash));
        Assert.AreEqual(64, chunk.Sha256Hash.Length); // hex-encoded SHA-256 = 32 bytes = 64 hex chars

        Assert.IsTrue(storage.Blobs.ContainsKey(chunk.BlobPath));
    }

    [TestMethod]
    public async Task WriteCollectionChunks_ChunkRollover_ProducesMultipleChunks()
    {
        // Use a tiny chunk threshold (1 byte) so every document triggers a rollover.
        var policy = MakePolicy(chunkSizeGb: 0); // 0 GB = 0 bytes threshold
        // A threshold of 0 means every document exceeds it → new chunk per doc.
        var storage = new InMemoryStorageConnector();
        var writer  = new ChunkWriter(storage, policy);
        var doc     = EmptyBsonDoc();

        var result = await writer.WriteCollectionChunksAsync(
            "full/2025/Q1/abc/",
            "mydb",
            "col",
            YieldDocs(3, doc),
            CancellationToken.None);

        Assert.AreEqual(3, result.TotalDocuments);
        Assert.AreEqual(3, result.ChunkCount);
        Assert.AreEqual(3, result.Chunks.Count);

        for (int i = 0; i < 3; i++)
        {
            Assert.AreEqual(i + 1, result.Chunks[i].ChunkIndex);
            Assert.AreEqual(1,     result.Chunks[i].DocumentCount);
            StringAssert.Contains(result.Chunks[i].BlobPath, $"part-{i + 1:D3}");
        }
    }

    [TestMethod]
    public async Task WriteCollectionChunks_Sha256_MatchesRawContent()
    {
        var storage = new InMemoryStorageConnector();
        var writer  = new ChunkWriter(storage, MakePolicy());
        var doc     = EmptyBsonDoc();

        var result = await writer.WriteCollectionChunksAsync(
            "full/2025/Q1/abc/",
            "mydb",
            "col",
            YieldDocs(2, doc),
            CancellationToken.None);

        // Recompute the expected hash over the two concatenated raw BSON docs
        var rawBytes = new byte[doc.Length * 2];
        doc.Span.CopyTo(rawBytes.AsSpan(0));
        doc.Span.CopyTo(rawBytes.AsSpan(doc.Length));
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(rawBytes));

        Assert.AreEqual(expectedHash, result.Chunks[0].Sha256Hash);
    }

    [TestMethod]
    public async Task WriteCollectionChunks_OutputIsValidGzip()
    {
        var storage = new InMemoryStorageConnector();
        var writer  = new ChunkWriter(storage, MakePolicy());
        var doc     = EmptyBsonDoc();

        var result = await writer.WriteCollectionChunksAsync(
            "full/2025/Q1/abc/",
            "mydb",
            "col",
            YieldDocs(5, doc),
            CancellationToken.None);

        var blobPath = result.Chunks[0].BlobPath;
        var ms       = storage.Blobs[blobPath];
        ms.Position  = 0;

        // Must decompress without throwing
        using var gz      = new GZipStream(ms, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        await gz.CopyToAsync(decoded);

        // 5 empty BSON docs × 5 bytes each
        Assert.AreEqual(5 * doc.Length, decoded.Length);
    }

    [TestMethod]
    public async Task WriteCollectionChunks_CollectionNameInBlobPath()
    {
        var storage = new InMemoryStorageConnector();
        var writer  = new ChunkWriter(storage, MakePolicy());

        var result = await writer.WriteCollectionChunksAsync(
            "full/2025/Q1/abc/",
            "prod-db",
            "users",
            YieldDocs(1, EmptyBsonDoc()),
            CancellationToken.None);

        Assert.AreEqual("full/2025/Q1/abc/prod-db.users.part-001.bson.gz",
            result.Chunks[0].BlobPath);
    }
}

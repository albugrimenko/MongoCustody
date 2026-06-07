using MongoCustody.Core.Abstractions;
using MongoCustody.Core.Models;
using System.IO.Compression;
using System.Security.Cryptography;

namespace MongoCustody.Core.Engines;

/// <summary>
/// Streams a sequence of raw BSON document bytes from a MongoDB source
/// collection into one or more gzip-compressed chunk blobs in storage.
///
/// <para>
/// Each call to <see cref="WriteCollectionChunksAsync"/> produces a
/// <see cref="CollectionBackupRecord"/> containing one <see cref="ChunkRecord"/>
/// per chunk written. Chunk files are named
/// <c>{db}.{collection}.part-{NNN}.bson.gz</c> (1-based, zero-padded to 3 digits).
/// </para>
///
/// <para>
/// The SHA-256 hash is computed over the <em>uncompressed</em> BSON bytes
/// in a single streaming pass using a <see cref="HashStream"/> write-through
/// wrapper. The blob write stream carries the gzip-compressed output.
/// No document is ever buffered in full — memory use is O(1) with respect
/// to collection or document size.
/// </para>
///
/// <para>
/// A new chunk is started after the last byte of a complete document is
/// flushed and the accumulated uncompressed byte count meets or exceeds
/// the configured threshold. A document is never split across chunks.
/// </para>
/// </summary>
internal sealed class ChunkWriter
{
    private readonly IStorageConnector _storage;
    private readonly long _chunkThresholdBytes;
    private readonly System.IO.Compression.CompressionLevel _compressionLevel;

    internal ChunkWriter(
        IStorageConnector storage,
        BackupPolicy policy)
    {
        _storage = storage;
        _chunkThresholdBytes = (long)policy.ChunkSizeGb * 1024 * 1024 * 1024;
        _compressionLevel = policy.CompressionLevel switch
        {
            Models.CompressionLevel.NoCompression => System.IO.Compression.CompressionLevel.NoCompression,
            Models.CompressionLevel.Optimal       => System.IO.Compression.CompressionLevel.Optimal,
            Models.CompressionLevel.SmallestSize  => System.IO.Compression.CompressionLevel.SmallestSize,
            _                                     => System.IO.Compression.CompressionLevel.Fastest
        };
    }

    /// <summary>
    /// Streams all documents from <paramref name="documentSource"/> into
    /// one or more chunk blobs under <paramref name="folderPath"/> and
    /// returns the completed <see cref="CollectionBackupRecord"/>.
    /// </summary>
    internal async Task<CollectionBackupRecord> WriteCollectionChunksAsync(
        string folderPath,
        string databaseName,
        string collectionName,
        IAsyncEnumerable<ReadOnlyMemory<byte>> documentSource,
        CancellationToken ct)
    {
        var chunks      = new List<ChunkRecord>();
        int chunkIndex  = 1;
        long totalDocs  = 0;

        // State for the current open chunk
        ChunkState? current = null;

        await foreach (var docBytes in documentSource.WithCancellation(ct)
                           .ConfigureAwait(false))
        {
            current ??= await OpenChunkAsync(
                folderPath, databaseName, collectionName, chunkIndex, ct)
                .ConfigureAwait(false);

            await current.HashStream.WriteAsync(docBytes, ct).ConfigureAwait(false);
            current.UncompressedBytes += docBytes.Length;
            current.DocumentCount++;
            totalDocs++;

            // Roll chunk after a complete document when threshold is reached
            if (current.UncompressedBytes >= _chunkThresholdBytes)
            {
                var record = await SealChunkAsync(current, chunkIndex, ct)
                    .ConfigureAwait(false);
                chunks.Add(record);
                chunkIndex++;
                current = null;
            }
        }

        // Seal the last (or only) chunk if any documents were written
        if (current is not null)
        {
            var record = await SealChunkAsync(current, chunkIndex, ct)
                .ConfigureAwait(false);
            chunks.Add(record);
        }

        return new CollectionBackupRecord
        {
            CollectionName  = collectionName,
            TotalDocuments  = totalDocs,
            ChunkCount      = chunks.Count,
            ExportedAtUtc   = DateTimeOffset.UtcNow,
            Chunks          = chunks
        };
    }

    // ------------------------------------------------------------------ //
    //  Private helpers
    // ------------------------------------------------------------------ //

    private async Task<ChunkState> OpenChunkAsync(
        string folderPath,
        string databaseName,
        string collectionName,
        int chunkIndex,
        CancellationToken ct)
    {
        var blobPath   = BackupPathBuilder.ChunkBlobPath(
            folderPath, databaseName, collectionName, chunkIndex);
        var blobStream = await _storage
            .OpenWriteAsync(blobPath, ct)
            .ConfigureAwait(false);

        var sha256     = SHA256.Create();
        var hashStream = new HashStream(sha256);
        var gzip       = new GZipStream(blobStream, _compressionLevel, leaveOpen: false);

        // Wire: caller writes to hashStream → bytes forwarded to gzip → blobStream
        hashStream.SetForwardTarget(gzip);

        return new ChunkState
        {
            BlobPath   = blobPath,
            HashStream = hashStream,
            Gzip       = gzip,
            Sha256     = sha256
        };
    }

    private static async Task<ChunkRecord> SealChunkAsync(
        ChunkState state,
        int chunkIndex,
        CancellationToken ct)
    {
        // 1. Flush any pending writes through HashStream → GZip
        await state.HashStream.FlushAsync(ct).ConfigureAwait(false);

        // 2. Finalise the SHA-256 hash (calls TransformFinalBlock).
        //    Must happen BEFORE reading .Hash and BEFORE closing GZip,
        //    because GZipStream.DisposeAsync will close the underlying
        //    blob stream — after which we only need the already-computed hash.
        state.HashStream.Dispose();

        // 3. Close GZipStream: writes the gzip footer to the blob stream
        //    and (leaveOpen:false) disposes it, which commits the Azure
        //    block-blob upload.
        await state.Gzip.DisposeAsync().ConfigureAwait(false);

        // 4. Read the now-finalised hash
        var hashBytes = state.Sha256.Hash
            ?? throw new InvalidOperationException("SHA-256 hash not available after finalising chunk.");
        var hexHash = Convert.ToHexStringLower(hashBytes);

        state.Sha256.Dispose();

        return new ChunkRecord
        {
            ChunkIndex    = chunkIndex,
            BlobPath      = state.BlobPath,
            DocumentCount = state.DocumentCount,
            Sha256Hash    = hexHash,
            SizeBytes     = 0   // Azure block-blob streams are not seekable; size is informational
        };
    }

    // ------------------------------------------------------------------ //
    //  ChunkState — tracks open streams for a single in-progress chunk
    // ------------------------------------------------------------------ //

    private sealed class ChunkState
    {
        internal required string     BlobPath          { get; init; }
        internal required HashStream HashStream        { get; init; }
        internal required GZipStream Gzip              { get; init; }
        internal required SHA256     Sha256            { get; init; }

        internal long UncompressedBytes { get; set; }
        internal long DocumentCount     { get; set; }
    }
}

// --------------------------------------------------------------------------
//  HashStream — forwards writes to an inner stream while hashing the bytes
// --------------------------------------------------------------------------

/// <summary>
/// A write-only <see cref="Stream"/> that feeds every written byte into both
/// a <see cref="System.Security.Cryptography.HashAlgorithm"/> and a
/// configurable downstream <see cref="Stream"/> (typically a
/// <see cref="GZipStream"/>).
///
/// The hash is finalised when <see cref="Dispose"/> is called. Read,
/// seek, and length operations are not supported.
/// </summary>
internal sealed class HashStream : Stream
{
    private readonly HashAlgorithm _hash;
    private Stream? _target;
    private bool _finalised;

    internal HashStream(HashAlgorithm hash)
    {
        _hash = hash;
    }

    /// <summary>Sets the downstream stream that receives each written buffer.</summary>
    internal void SetForwardTarget(Stream target) => _target = target;

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
        _hash.TransformBlock(buffer, offset, count, null, 0);
        _target?.Write(buffer, offset, count);
    }

    public override async Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken ct)
    {
        _hash.TransformBlock(buffer, offset, count, null, 0);
        if (_target is not null)
            await _target.WriteAsync(buffer.AsMemory(offset, count), ct)
                .ConfigureAwait(false);
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        // TransformBlock requires a byte[] — rent from the buffer if possible
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(
                buffer, out var segment))
        {
            _hash.TransformBlock(
                segment.Array!, segment.Offset, segment.Count, null, 0);
        }
        else
        {
            var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                buffer.CopyTo(rented);
                _hash.TransformBlock(rented, 0, buffer.Length, null, 0);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }

        if (_target is not null)
            await _target.WriteAsync(buffer, ct).ConfigureAwait(false);
    }

    public override void Flush() => _target?.Flush();

    public override Task FlushAsync(CancellationToken ct)
        => _target?.FlushAsync(ct) ?? Task.CompletedTask;

    public override void SetLength(long value)  => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override int  Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_finalised)
        {
            _hash.TransformFinalBlock([], 0, 0);
            _finalised = true;
        }
        base.Dispose(disposing);
    }
}

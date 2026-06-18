using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MongoCustody.Core.Engines;

/// <summary>
/// Reads a concatenated BSON document stream and yields each document as
/// a contiguous <see cref="ReadOnlyMemory{T}"/> of raw BSON bytes.
///
/// <para>
/// BSON document layout: the first four bytes are a little-endian
/// <see cref="int"/> giving the total document length in bytes (inclusive of
/// those four bytes). The remaining <c>length - 4</c> bytes complete the
/// document. Documents are written sequentially with no separator between
/// them — this is the same layout produced by <c>MongoDB.Driver</c> and by
/// <see cref="ChunkWriter"/> during backup.
/// </para>
/// </summary>
internal static class BsonChunkReader
{
    private const int LengthPrefixSize = 4;

    /// <summary>
    /// Asynchronously reads all BSON documents from <paramref name="rawBsonStream"/>
    /// and yields them as <see cref="ReadOnlyMemory{T}"/> values. The stream
    /// must already be decompressed — pass the output of a
    /// <see cref="System.IO.Compression.GZipStream"/> wrapping the blob stream.
    /// </summary>
    /// <param name="rawBsonStream">
    /// Decompressed stream of concatenated BSON documents. Ownership remains
    /// with the caller; this method does not dispose the stream.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="System.IO.InvalidDataException">
    /// Thrown if the stream contains a truncated document or an invalid
    /// length prefix (e.g., less than 5 bytes, which is the minimum
    /// valid BSON document size).
    /// </exception>
    internal static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadDocumentsAsync(
        Stream rawBsonStream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var lenBuf = new byte[LengthPrefixSize];

        while (true)
        {
            // Read 4-byte length prefix; 0 bytes on the first attempt = clean EOF.
            int bytesRead = 0;
            while (bytesRead < LengthPrefixSize)
            {
                int read = await rawBsonStream
                    .ReadAsync(lenBuf.AsMemory(bytesRead, LengthPrefixSize - bytesRead), ct)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    if (bytesRead == 0)
                        yield break; // clean end of stream

                    throw new InvalidDataException(
                        $"Truncated BSON stream: expected {LengthPrefixSize - bytesRead} more " +
                        $"byte(s) in length prefix but the stream ended.");
                }

                bytesRead += read;
            }

            int docLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);

            // Minimum valid BSON document is 5 bytes: 4-byte length + null terminator.
            if (docLen < 5)
                throw new InvalidDataException(
                    $"Invalid BSON document length {docLen}; minimum valid size is 5 bytes.");

            var docBuf = new byte[docLen];
            lenBuf.CopyTo(docBuf, 0);

            // Read the remainder of the document.
            await rawBsonStream
                .ReadExactlyAsync(docBuf.AsMemory(LengthPrefixSize, docLen - LengthPrefixSize), ct)
                .ConfigureAwait(false);

            yield return docBuf;
        }
    }
}

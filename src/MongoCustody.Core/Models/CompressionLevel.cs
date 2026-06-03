namespace MongoCustody.Core.Models;

/// <summary>
/// The gzip compression level applied to backup chunk files.
/// Mirrors <see cref="System.IO.Compression.CompressionLevel"/> but is
/// defined here to avoid leaking BCL types into the public API surface.
/// </summary>
public enum CompressionLevel
{
    /// <summary>
    /// No compression. Use only for diagnostic or testing purposes.
    /// </summary>
    NoCompression,

    /// <summary>
    /// Fastest compression — lower CPU cost, larger files.
    /// Recommended for production; the throughput saving outweighs
    /// the extra storage cost at scale.
    /// </summary>
    Fastest,

    /// <summary>
    /// Optimal compression — higher CPU cost, smallest files.
    /// Use when storage cost is more important than backup duration.
    /// </summary>
    Optimal,

    /// <summary>
    /// SmallestSize — maximum compression ratio.
    /// </summary>
    SmallestSize
}

using System;
using static SharpZstd.Interop.Zstd;

// ReSharper disable once CheckNamespace
namespace Robust.Shared.Utility;

/// <summary>
/// One-shot zstd helpers that avoid the stream wrappers (and their per-call allocations).
/// </summary>
internal static class ZStdStarlight
{
    /// <summary>
    /// Decompresses one or more complete zstd frames into <paramref name="destination"/>.
    /// </summary>
    public static unsafe int Decompress(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        fixed (byte* dst = destination)
        fixed (byte* src = source)
        {
            var result = ZSTD_decompress(dst, (nuint) destination.Length, src, (nuint) source.Length);
            ZStdException.ThrowIfError(result);
            return (int) result;
        }
    }
}

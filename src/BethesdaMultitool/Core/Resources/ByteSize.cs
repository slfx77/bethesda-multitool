using System.Runtime.CompilerServices;

namespace BethesdaMultitool.Core.Resources;

/// <summary>
///     Conventions for estimating the managed footprint of cached values. Estimates feed
///     <see cref="Diagnostics.ResourceStats.EstimatedBytes" /> and LRU byte budgets — they should be
///     cheap and roughly right, not exact.
/// </summary>
internal static class ByteSize
{
    /// <summary>Per-entry object/bookkeeping fudge added by callers that cache small reference graphs.</summary>
    public const long ObjectOverhead = 64;

    /// <summary>Estimates the byte footprint of an unmanaged-element array (element size times length plus object overhead).</summary>
    public static long OfArray<T>(T[]? array) where T : unmanaged
    {
        return array is null
            ? 0
            : (long)array.Length * Unsafe.SizeOf<T>() + ObjectOverhead;
    }

    /// <summary>Estimates the byte footprint of a string (two bytes per char plus object overhead).</summary>
    public static long OfString(string? value)
    {
        return value is null ? 0 : 2L * value.Length + 26;
    }
}

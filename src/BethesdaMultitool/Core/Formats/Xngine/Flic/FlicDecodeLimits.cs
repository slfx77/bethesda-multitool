namespace BethesdaMultitool.Core.Formats.Xngine.Flic;

/// <summary>Admission limits for the strict materialized FLC media profile; these are not a process working-set guarantee.</summary>
internal sealed record FlicDecodeLimits
{
    /// <summary>Maximum encoded input bytes accepted before any decoder allocation.</summary>
    internal int MaximumEncodedBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>Maximum indexed pixels in a canvas; the corresponding RGBA frame must also fit an array.</summary>
    internal int MaximumCanvasPixels { get; init; } = 4 * 1024 * 1024;
    /// <summary>Maximum actual picture blocks, including the one discarded loop-back block.</summary>
    internal int MaximumFrameBlocks { get; init; } = 4097;
    /// <summary>Logical allocation allowance for indexed frames, palettes, the working canvas and one RGBA buffer.</summary>
    /// <remarks>All palette allocations and the discarded ring frame are charged conservatively. Object/list overhead,
    /// caller-owned encoded bytes and Shared/native retained sample copies are additional costs.</remarks>
    internal long MaximumDecodedBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Rejects limits that cannot admit this profile or cannot represent a normalized RGBA array.</summary>
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumEncodedBytes, FlicFile.HeaderLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCanvasPixels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumCanvasPixels, int.MaxValue / 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumFrameBlocks, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumFrameBlocks, 65536);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDecodedBytes);
    }
}

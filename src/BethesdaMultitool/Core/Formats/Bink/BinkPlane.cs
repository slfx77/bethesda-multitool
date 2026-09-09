// ORIGINAL CLEAN-ROOM IMPLEMENTATION.
//
// Written from our own reverse engineering of RAD Game Tools' shipped decoder binkw32.dll
// (Fallout Tactics, md5 ecbd8213e89f8afde368f8eb05ff5a9c), via our Ghidra decompilation at
// tools/GhidraProject/ClassicRE/binkw32.dll.decompiled.txt and our own capstone disassembly of the
// same file, as written up in the specification document
//   scratchpad .../cleanroom/bink/SPEC.md  ("Bink Video (BIKi) - Format Specification"), section 2.2.
//
// NO FFmpeg- or libav-derived code, and no other third-party Bink implementation, was consulted,
// read, copied or paraphrased.

namespace BethesdaMultitool.Core.Formats.Bink;

/// <summary>
///     One 8-bit plane of a Bink frame, with the geometry the block loop walks.
///     <para>
///         ⚠ The DECODED region is larger than the picture: a 720x486 movie decodes 720x488 luma,
///         and the extra rows are simply not displayed. That is the format, not a rounding choice.
///     </para>
///     <para>
///         The buffer carries 16 rows and 16 columns of slack beyond the decoded region. A SCALED
///         (16x16) block on the last even block row genuinely writes past the decoded height — in
///         the DLL that lands in the next plane of its single frame allocation. Padding each plane
///         separately keeps that overspill from corrupting a neighbour; no corpus frame exercises
///         it, so the two models are indistinguishable on real data.
///     </para>
/// </summary>
internal sealed class BinkPlane
{
    private const int Slack = 16;

    internal BinkPlane(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;
        Stride = width + Slack;
        Pixels = new byte[Stride * (height + Slack)];
    }

    /// <summary>Decoded width in pixels — <c>align8</c> of the picture width (or half width).</summary>
    internal int Width { get; }

    /// <summary>Decoded height in pixels.</summary>
    internal int Height { get; }

    /// <summary>Row pitch of <see cref="Pixels" />, which includes the slack columns.</summary>
    internal int Stride { get; }

    /// <summary>The plane's bytes.</summary>
    internal byte[] Pixels { get; }

    /// <summary>Byte offset of pixel (<paramref name="x" />, <paramref name="y" />).</summary>
    internal int OffsetOf(int x, int y)
    {
        return y * Stride + x;
    }

    /// <summary>Rounds up to a multiple of 8, the block grid Bink decodes on.</summary>
    internal static int Align8(int value)
    {
        return (value + 7) & ~7;
    }
}

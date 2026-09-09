using BethesdaMultitool.Core.Formats.VanBuren;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.VanBuren;

/// <summary>
///     Pins the Targa run-length expansion behind the Van Buren prototype's compressed image family,
///     measured on all 85 retail payloads (2026-09-06).
/// </summary>
public sealed class VanBurenTgaRleTests
{
    [Fact]
    public void TryExpand_ReadsARunAsCountPlusOne()
    {
        // ⚠ THE rule. 0x82 is a run of THREE, not two. Reading the count as the length itself
        // decodes an image that looks right at the top and drifts wrong toward the bottom.
        var dest = new byte[3 * 3];
        var ok = VanBurenTgaRle.TryExpand([0x82, 0x10, 0x20, 0x30], 3, 3, dest, out var consumed);

        Assert.True(ok);
        Assert.Equal(4, consumed);
        Assert.Equal<byte[]>([0x10, 0x20, 0x30, 0x10, 0x20, 0x30, 0x10, 0x20, 0x30], dest);
    }

    [Fact]
    public void TryExpand_ReadsALiteralAsCountPlusOne()
    {
        var dest = new byte[2 * 3];
        var ok = VanBurenTgaRle.TryExpand([0x01, 1, 2, 3, 4, 5, 6], 2, 3, dest, out var consumed);

        Assert.True(ok);
        Assert.Equal(7, consumed);
        Assert.Equal<byte[]>([1, 2, 3, 4, 5, 6], dest);
    }

    [Fact]
    public void TryExpand_MixesRunsAndLiterals()
    {
        var dest = new byte[4 * 3];
        var ok = VanBurenTgaRle.TryExpand([0x81, 9, 9, 9, 0x01, 1, 1, 1, 2, 2, 2], 4, 3, dest, out var consumed);

        Assert.True(ok);
        Assert.Equal(11, consumed);
        Assert.Equal<byte[]>([9, 9, 9, 9, 9, 9, 1, 1, 1, 2, 2, 2], dest);
    }

    [Fact]
    public void TryExpand_RefusesAStreamThatOverrunsThePixelCount()
    {
        // Exact consumption is the standard of proof for this family; a packet that would write
        // past the image must fail rather than truncate silently.
        var dest = new byte[2 * 3];

        Assert.False(VanBurenTgaRle.TryExpand([0x8F, 1, 2, 3], 2, 3, dest, out _));
    }

    [Fact]
    public void TryExpand_RefusesATruncatedStream()
    {
        var dest = new byte[4 * 3];

        Assert.False(VanBurenTgaRle.TryExpand([0x83, 1, 2], 4, 3, dest, out _));
    }

    [Fact]
    public void HasFooter_RecognisesTheTgaTwoPointZeroSignature()
    {
        // All 85 retail compressed payloads carry it, which is what identified the family as TGA.
        var footer = new byte[VanBurenTgaRle.FooterLength];
        "TRUEVISION-XFILE."u8.CopyTo(footer.AsSpan(8));

        Assert.True(VanBurenTgaRle.HasFooter(footer));
        Assert.False(VanBurenTgaRle.HasFooter(new byte[VanBurenTgaRle.FooterLength]));
    }

    [Fact]
    public void TheExtensionAreaIsTheStandardFourNinetyFiveBytes()
    {
        // ⚑ Every one of the 85 leaves exactly this between its last pixel and the footer, which is
        // what turned "leftover bytes" into a recognised TGA 2.0 extension area.
        Assert.Equal(495, VanBurenTgaRle.ExtensionAreaLength);
        Assert.Equal(26, VanBurenTgaRle.FooterLength);
    }
}
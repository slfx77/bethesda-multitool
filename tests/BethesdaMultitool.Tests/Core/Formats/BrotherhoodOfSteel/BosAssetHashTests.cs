using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     Pins for the Fallout: Brotherhood of Steel asset-key hash. Every expected value below was
///     READ OFF THE DISC — the tag of a hash-table slot whose payload names itself — never computed
///     by the routine under test.
/// </summary>
public sealed class BosAssetHashTests
{
    [Theory]
    [InlineData("/Final_Assets/sound/FS_Linoleum_4.vag",
        0xA91D4741u)] // BAR_S.CLP slot for page 194 (header name FS_Linoleum_4)
    [InlineData("bar.tex", 0x8DE8D8C8u)] // BAR_T.CLP section at page 2
    [InlineData("bar.hsh", 0x8DE8AC12u)] // BAR_T.CLP section at page 1
    [InlineData("bar.vat", 0x8DE8E41Au)] // BAR_T.CLP section at page 1269
    public void Compute_ReproducesTheSlotKeysOnTheDisc(string key, uint expected)
    {
        Assert.Equal(expected, BosAssetHash.Compute(key));
    }

    [Fact]
    public void Compute_FoldsBackslashToSlash()
    {
        // The movz at 0x0013FDD8: a backslash hashes as a forward slash, so authored Windows paths
        // and disc paths agree.
        Assert.Equal(BosAssetHash.Compute("a/b"), BosAssetHash.Compute("a\\b"));
        Assert.Equal(0x80020C60u, BosAssetHash.Compute("a/b"));
    }

    [Fact]
    public void Compute_IsCaseSensitive()
    {
        Assert.NotEqual(BosAssetHash.Compute("bar.tex"), BosAssetHash.Compute("BAR.TEX"));
        Assert.Equal(0u, BosAssetHash.Compute(string.Empty));
        Assert.Equal(0x41u, BosAssetHash.Compute("A"));
    }

    [Theory]
    [InlineData(3, 8)] // BAR_T.CLP: three sections, an 8-slot tail
    [InlineData(27, 64)] // T_GAZ_S.CLP
    [InlineData(34, 64)] // BAR_S.CLP
    [InlineData(80, 128)] // GLOBAL_S.CLP
    [InlineData(232, 512)] // BAR.CLP
    [InlineData(64, 128)] // n + n/2 = 96 → 128; a power of two exactly (e.g. 42 → 63 → 64) still rounds UP
    [InlineData(42, 64)]
    public void SlotCountFor_MatchesTheLoadersBitLengthLoop(int entries, int expectedSlots)
    {
        Assert.Equal(expectedSlots, BosAssetHash.SlotCountFor(entries));
    }

    [Fact]
    public void Rehash_IsTheProbeStepAt00140978()
    {
        // (~h >> 27) ^ (h << 5), computed by hand for h = 1: ~1 = 0xFFFFFFFE, >> 27 = 0x1F; 1 << 5 = 0x20.
        Assert.Equal(0x3Fu, BosAssetHash.Rehash(1));
    }
}
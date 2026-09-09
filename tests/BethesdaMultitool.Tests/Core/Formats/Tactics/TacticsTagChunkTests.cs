using System.Text;
using BethesdaMultitool.Core.Formats.Tactics;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tactics;

/// <summary>
///     Vectors for the framing every Fallout Tactics asset opens with, measured over all 33,368
///     tagged entries in the shipped <c>core\*.bos</c> archives on 2026-09-06.
/// </summary>
public sealed class TacticsTagChunkTests
{
    private static byte[] Framed(string tag, string version, int bodyBytes = 8)
    {
        var head = Encoding.ASCII.GetBytes($"<{tag}>\0{version}\0");
        var b = new byte[head.Length + bodyBytes];
        head.CopyTo(b, 0);
        return b;
    }

    [Theory]
    [InlineData("zar", "4", 8)]
    [InlineData("entity", "2", 11)]
    [InlineData("character", "1", 14)]
    [InlineData("world", "68", 11)]
    [InlineData("tile", "10", 10)]
    [InlineData("sprite", "4", 11)]
    [InlineData("campaign", "19", 14)]
    public void TryRead_ReadsEachShippedTagAndItsBodyOffset(string tag, string version, int bodyOffset)
    {
        // ⚠ The body offset is NOT a constant: it is len(tag) + len(version) + 4 (the angle
        // brackets and two NULs), so it depends on BOTH the tag and the version LENGTH — which
        // is why every format reads the framing instead of hardcoding an offset. The two-digit
        // versions ("10", "19") are exactly where a per-format constant would go wrong.
        Assert.True(TacticsTagChunk.TryRead(Framed(tag, version), out var chunk));

        Assert.Equal(tag, chunk.Tag);
        Assert.Equal(version, chunk.Version);
        Assert.Equal(bodyOffset, chunk.BodyOffset);
    }

    [Fact]
    public void TryRead_RequiresTheVersionToBeNulTerminated()
    {
        // Without this a binary payload starting with '<' scans until it happens to find a NUL and
        // reports a plausible-looking body offset.
        var b = Encoding.ASCII.GetBytes("<zar>\09999999999999999999999999");

        Assert.False(TacticsTagChunk.TryRead(b, out _));
    }

    [Fact]
    public void TryRead_RequiresTheNulAfterTheTag()
    {
        Assert.False(TacticsTagChunk.TryRead(Encoding.ASCII.GetBytes("<zar>4\0body"), out _));
    }

    [Fact]
    public void TryRead_RejectsAnEmptyVersion()
    {
        Assert.False(TacticsTagChunk.TryRead(Encoding.ASCII.GetBytes("<zar>\0\0body"), out _));
    }

    [Fact]
    public void TryRead_RejectsSomethingThatIsNotFramed()
    {
        Assert.False(TacticsTagChunk.TryRead("PK"u8, out _));
        Assert.False(TacticsTagChunk.TryRead([], out _));
        Assert.False(TacticsTagChunk.TryRead(Encoding.ASCII.GetBytes("<no-close-bracket"), out _));
    }

    [Fact]
    public void Is_MatchesOnlyTheNamedTag()
    {
        var zar = Framed("zar", "4");

        Assert.True(TacticsTagChunk.Is(zar, "zar"));
        Assert.False(TacticsTagChunk.Is(zar, "tile"));

        // ⛔ The .chr tag is <character>, NOT <esh> as earlier notes recorded — no file in the
        // install carries an <esh> framing.
        Assert.True(TacticsTagChunk.Is(Framed("character", "1"), "character"));
        Assert.False(TacticsTagChunk.Is(Framed("character", "1"), "esh"));
    }
}
using System.Numerics;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The two color censuses of <see cref="NifPackedGeometryComparison" /> on synthetic decoded colors:
///     <see cref="NifPackedGeometryComparison.IsOrderSensitive" /> is the reader's byte rule (R, G, B not all equal)
///     and <see cref="NifPackedGeometryComparison.IsOrderDiscriminating" /> the comparison's (R, G, B spanning at
///     least two bytes). They differ exactly on the colors whose bytes spread by one, which the reader counts and the
///     one-byte comparison tolerance cannot see; a single rule serving both consumers compares two different censuses
///     (three manifest rows failed that way: 205 against 156, 128 against 126, 86 against 37).
/// </summary>
public sealed class NifPackedGeometryComparisonRuleTests
{
    [Theory]
    [InlineData(128, 128, 128, false, false)]
    [InlineData(255, 255, 255, false, false)]
    [InlineData(0, 0, 0, false, false)]
    [InlineData(100, 101, 101, true, false)]
    [InlineData(101, 100, 101, true, false)]
    [InlineData(254, 255, 254, true, false)]
    [InlineData(100, 102, 100, true, true)]
    [InlineData(0, 0, 2, true, true)]
    [InlineData(255, 0, 0, true, true)]
    public void Rules_SplitOnAOneByteSpread(int r, int g, int b, bool sensitive, bool discriminating)
    {
        var color = Decoded(r, g, b);
        Assert.Equal(sensitive, NifPackedGeometryComparison.IsOrderSensitive(color));
        Assert.Equal(discriminating, NifPackedGeometryComparison.IsOrderDiscriminating(color));
    }

    /// <summary>
    ///     Over every byte triple within two of a base byte (the whole neighborhood where the rules can differ): the
    ///     byte rule is exactly "not all equal", the discriminating rule exactly "spread of two or more", the second
    ///     implies the first, and the rules differ on exactly the spread-one triples. Control: a tolerance rule read as
    ///     the byte rule (the previous implementation) misses every spread-one triple, 6 of the 25 per base byte.
    /// </summary>
    [Fact]
    public void ByteRule_CountsEverySpreadOneTriple_TheToleranceRuleDoesNot()
    {
        var spreadOne = 0;
        var differing = 0;
        var checkedTriples = 0;
        for (var baseByte = 2; baseByte < 254; baseByte += 7)
        {
            for (var dg = -2; dg <= 2; dg++)
            {
                for (var db = -2; db <= 2; db++)
                {
                    var r = baseByte;
                    var g = baseByte + dg;
                    var b = baseByte + db;
                    var color = Decoded(r, g, b);
                    var sensitive = NifPackedGeometryComparison.IsOrderSensitive(color);
                    var discriminating = NifPackedGeometryComparison.IsOrderDiscriminating(color);
                    var spread = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                    Assert.Equal(spread != 0, sensitive);
                    Assert.Equal(spread >= 2, discriminating);
                    Assert.True(!discriminating || sensitive, $"({r}, {g}, {b}) discriminates without being sensitive");
                    checkedTriples++;
                    if (spread == 1)
                    {
                        spreadOne++;
                    }

                    if (sensitive != discriminating)
                    {
                        differing++;
                    }
                }
            }
        }

        Assert.True(checkedTriples > 0);
        Assert.Equal(spreadOne, differing);
        Assert.Equal(6 * checkedTriples / 25, spreadOne);
    }

    /// <summary>
    ///     Both rules see the same bytes under either platform order: the X360 decode (bytes 1, 2, 3 as R, G, B) and the
    ///     PS3 decode of the same four bytes (bytes 3, 1, 2 as R, G, B) agree on every triple, since each rule is a
    ///     function of the set of bytes.
    /// </summary>
    [Theory]
    [InlineData(10, 11, 11)]
    [InlineData(10, 12, 10)]
    [InlineData(200, 200, 200)]
    [InlineData(255, 0, 128)]
    public void Rules_ArePlatformIndependent(int b1, int b2, int b3)
    {
        var x360 = Decoded(b1, b2, b3);
        var ps3 = Decoded(b3, b1, b2);
        Assert.Equal(NifPackedGeometryComparison.IsOrderSensitive(x360), NifPackedGeometryComparison.IsOrderSensitive(ps3));
        Assert.Equal(NifPackedGeometryComparison.IsOrderDiscriminating(x360),
            NifPackedGeometryComparison.IsOrderDiscriminating(ps3));
    }

    /// <summary>A decoded color the way NifPackedGeometryDecoder writes it: byte / 255 per channel, alpha opaque.</summary>
    private static Vector4 Decoded(int r, int g, int b)
    {
        return new Vector4(r / 255f, g / 255f, b / 255f, 1f);
    }
}

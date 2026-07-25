using BethesdaMultitool.Core.Formats.Nif.Conversion;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Conversion;

/// <summary>
///     Characterization tests for <see cref="NifSchemaConverter.ParseVersionString" /> after adding
///     memoization. The parse is a pure function, so the cache must return values identical to a
///     fresh parse for every input.
/// </summary>
public class NifSchemaConverterVersionTests
{
    [Theory]
    [InlineData("20.2.0.7", 0x14020007u)] // FO3/FNV/Skyrim
    [InlineData("20.0.0.5", 0x14000005u)] // Oblivion
    [InlineData("4.0.0.2", 0x04000002u)] // Morrowind
    [InlineData("4.2.2.0", 0x04020200u)]
    [InlineData("0.0.0.0", 0x00000000u)]
    [InlineData("255.255.255.255", 0xFFFFFFFFu)]
    public void ParseVersionString_FourParts_PacksBigEndianBytes(string version, uint expected)
    {
        Assert.Equal(expected, NifSchemaConverter.ParseVersionString(version));
    }

    [Theory]
    [InlineData("20.2")]
    [InlineData("20.2.0")]
    [InlineData("")]
    public void ParseVersionString_FewerThanFourParts_ReturnsZero(string version)
    {
        Assert.Equal(0u, NifSchemaConverter.ParseVersionString(version));
    }

    [Fact]
    public void ParseVersionString_RepeatedCalls_AreStable()
    {
        // Memoization must not change the result on subsequent calls.
        var first = NifSchemaConverter.ParseVersionString("20.2.0.7");
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(first, NifSchemaConverter.ParseVersionString("20.2.0.7"));
        }

        Assert.Equal(0x14020007u, first);
    }
}
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Pins header classification against the Bethesda rows of <c>nif.xml</c>'s version table. The versions are
///     written as packed literals (one byte per octet) so a wrong named constant cannot hide a wrong row.
/// </summary>
public sealed class NifExportFamiliesTests
{
    /// <summary>Each Bethesda version row maps to its family, and each near miss to Unknown.</summary>
    [Theory]
    // Morrowind: NetImmerse 4.0.0.2, no BS stream header.
    [InlineData(0x04000002u, 0u, NifExportFamily.Morrowind)]
    // Oblivion: every Gamebryo version nif.xml lists for it, 10.0.1.0 through 20.0.0.5.
    [InlineData(0x0A000100u, 0u, NifExportFamily.Oblivion)]
    [InlineData(0x0A000102u, 3u, NifExportFamily.Oblivion)]
    [InlineData(0x0A010065u, 4u, NifExportFamily.Oblivion)]
    [InlineData(0x0A01006Au, 5u, NifExportFamily.Oblivion)]
    [InlineData(0x0A020000u, 9u, NifExportFamily.Oblivion)]
    [InlineData(0x14000004u, 11u, NifExportFamily.Oblivion)]
    [InlineData(0x14000005u, 11u, NifExportFamily.Oblivion)]
    // 20.2.0.7 split by BS stream version.
    [InlineData(0x14020007u, 14u, NifExportFamily.Fo3Fnv)]
    [InlineData(0x14020007u, 21u, NifExportFamily.Fo3Fnv)]
    [InlineData(0x14020007u, 34u, NifExportFamily.Fo3Fnv)]
    [InlineData(0x14020007u, 83u, NifExportFamily.Skyrim)]
    [InlineData(0x14020007u, 100u, NifExportFamily.SkyrimSe)]
    [InlineData(0x14020007u, 130u, NifExportFamily.Fo4)]
    [InlineData(0x14020007u, 132u, NifExportFamily.Fo4)]
    [InlineData(0x14020007u, 139u, NifExportFamily.Fo4)]
    [InlineData(0x14020007u, 155u, NifExportFamily.Fo76)]
    [InlineData(0x14020007u, 170u, NifExportFamily.Starfield)]
    [InlineData(0x14020007u, 172u, NifExportFamily.Starfield)]
    [InlineData(0x14020007u, 173u, NifExportFamily.Starfield)]
    [InlineData(0x14020007u, 175u, NifExportFamily.Starfield)]
    // Near misses: the neighbours of each accepted value are not guessed into a family.
    [InlineData(0x04000000u, 0u, NifExportFamily.Unknown)] // 4.0.0.0 Freedom Force
    [InlineData(0x04020200u, 0u, NifExportFamily.Unknown)] // 4.2.2.0, the last NetImmerse, is not Morrowind
    [InlineData(0x14010003u, 0u, NifExportFamily.Unknown)] // 20.1.0.3
    [InlineData(0x14020007u, 0u, NifExportFamily.Unknown)] // 20.2.0.7 user 0: non-Bethesda
    [InlineData(0x14020007u, 11u, NifExportFamily.Unknown)]
    [InlineData(0x14020007u, 35u, NifExportFamily.Unknown)]
    [InlineData(0x14020007u, 84u, NifExportFamily.Unknown)]
    [InlineData(0x14020007u, 101u, NifExportFamily.Unknown)]
    [InlineData(0x14020007u, 140u, NifExportFamily.Unknown)]
    [InlineData(0x14020007u, 154u, NifExportFamily.Unknown)]
    [InlineData(0x14020007u, 156u, NifExportFamily.Unknown)]
    [InlineData(0x14020007u, 169u, NifExportFamily.Unknown)]
    [InlineData(0x14020008u, 34u, NifExportFamily.Unknown)] // 20.2.0.8 is not 20.2.0.7
    internal void FromHeader_ClassifiesByBinaryAndBsStreamVersion(
        uint binaryVersion,
        uint bsVersion,
        NifExportFamily expected)
    {
        var header = new NifInfo { BinaryVersion = binaryVersion, BsVersion = bsVersion };

        Assert.Equal(expected, NifExportFamilies.FromHeader(header));
    }

    /// <summary>Conversion does not touch either version field, so endianness never changes the family.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FromHeader_IgnoresEndianness(bool isBigEndian)
    {
        var header = new NifInfo { BinaryVersion = 0x14020007u, BsVersion = 34u, IsBigEndian = isBigEndian };

        Assert.Equal(NifExportFamily.Fo3Fnv, NifExportFamilies.FromHeader(header));
    }

    /// <summary>No NIF header is ever SpeedTree, and a missing header is an argument error.</summary>
    [Fact]
    public void FromHeader_NeverReportsSpeedTreeAndRejectsNull()
    {
        foreach (var binaryVersion in new[] { 0x04000002u, 0x14000005u, 0x14020007u, 0x14020008u })
        {
            for (var bsVersion = 0u; bsVersion <= 200u; bsVersion++)
            {
                var header = new NifInfo { BinaryVersion = binaryVersion, BsVersion = bsVersion };
                Assert.NotEqual(NifExportFamily.SpeedTree, NifExportFamilies.FromHeader(header));
            }
        }

        Assert.Throws<ArgumentNullException>(() => NifExportFamilies.FromHeader(null!));
    }
}

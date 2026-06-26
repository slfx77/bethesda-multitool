using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif;

/// <summary>
///     Pins <see cref="NifParser.IsBethesdaVersion" /> version/user-version gating. Regression guard for
///     the Oblivion "no geometry on ~a third of meshes" bug: stock Oblivion ships meshes at BOTH User
///     Version 11 and User Version 10, and the gate originally accepted only 11 — so every UV10 mesh was
///     rejected at the header and produced no geometry.
/// </summary>
public class NifBethesdaVersionTests
{
    // NIF binary versions.
    private const uint Oblivion2004 = 0x14000004; // 20.0.0.4
    private const uint Oblivion2005 = 0x14000005; // 20.0.0.5
    private const uint Oblivion1020 = 0x0A020000; // 10.2.0.0 (older-exporter architecture, e.g. ICPalaceTower01)
    private const uint Oblivion101101 = 0x0A010065; // 10.1.0.101 (bsVersion 4)
    private const uint Oblivion101106 = 0x0A01006A; // 10.1.0.106 (bsVersion 5, e.g. fort-ruins rfcastlearchfront)
    private const uint Fo3Fnv = 0x14020007; // 20.2.0.7
    private const uint NetImmerse10010 = 0x0A000100; // 10.0.1.0 (oldest Gamebryo: fort tiles, darkelf ears)
    private const uint NetImmerse10012 = 0x0A000102; // 10.0.1.2 (groundcover plants; carries a BSStreamHeader)

    [Theory]
    [InlineData(Oblivion2004, 10u)] // the fix: UV10 Oblivion meshes
    [InlineData(Oblivion2004, 11u)]
    [InlineData(Oblivion2005, 10u)]
    [InlineData(Oblivion2005, 11u)]
    public void Oblivion_AcceptsUserVersion10And11(uint binaryVersion, uint userVersion)
    {
        Assert.True(NifParser.IsBethesdaVersion(binaryVersion, userVersion));
    }

    [Theory]
    [InlineData(Oblivion1020, 10u)]
    [InlineData(Oblivion1020, 11u)]
    [InlineData(Oblivion101101, 10u)] // older-exporter Gamebryo (fort-ruins / castle architecture)
    [InlineData(Oblivion101101, 11u)]
    [InlineData(Oblivion101106, 10u)] // regression: these rendered "no geometry" before the fix
    [InlineData(Oblivion101106, 11u)]
    public void Oblivion_AcceptsOldGamebryoArchitecture(uint binaryVersion, uint userVersion)
    {
        Assert.True(NifParser.IsBethesdaVersion(binaryVersion, userVersion));
    }

    [Theory]
    [InlineData(NetImmerse10010)] // 10.0.1.0 — fort tiles (rf1xhousingtiles), darkelf ears
    [InlineData(NetImmerse10012)] // 10.0.1.2 — groundcover plants
    public void Oblivion_AcceptsOldestNetImmerseWithoutUserVersion(uint binaryVersion)
    {
        // These versions predate the Header User Version field (added 10.0.1.8), so UserVersion is always 0.
        Assert.True(NifParser.IsBethesdaVersion(binaryVersion, 0u));
    }

    [Theory]
    [InlineData(Fo3Fnv, 11u)]
    [InlineData(Fo3Fnv, 12u)]
    public void Fo3Fnv_AcceptsUserVersion11And12(uint binaryVersion, uint userVersion)
    {
        Assert.True(NifParser.IsBethesdaVersion(binaryVersion, userVersion));
    }

    [Theory]
    [InlineData(Oblivion2004, 12u)] // UV12 is not an Oblivion value
    [InlineData(Oblivion101106, 12u)] // UV12 is not a 10.1.0.x value
    [InlineData(Fo3Fnv, 10u)] // UV10 is not an FO3/FNV value
    [InlineData(NetImmerse10010, 11u)] // 10.0.1.x predates User Version; a non-zero value means a misparse
    [InlineData(NetImmerse10012, 1u)] // (1 here would be the BS Version misread as User Version)
    [InlineData(0x04000002u, 0u)] // Morrowind 4.0.0.2 — handled by a different path, not this gate
    public void RejectsNonMatchingVersions(uint binaryVersion, uint userVersion)
    {
        Assert.False(NifParser.IsBethesdaVersion(binaryVersion, userVersion));
    }
}

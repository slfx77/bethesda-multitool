using FalloutXbox360Utils.Core.Formats.Nif;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif;

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
    private const uint Fo3Fnv = 0x14020007;       // 20.2.0.7
    private const uint NetImmerse10010 = 0x0A000100; // 10.0.1.0 (pre-20 legacy — not yet supported)

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
    [InlineData(Fo3Fnv, 11u)]
    [InlineData(Fo3Fnv, 12u)]
    public void Fo3Fnv_AcceptsUserVersion11And12(uint binaryVersion, uint userVersion)
    {
        Assert.True(NifParser.IsBethesdaVersion(binaryVersion, userVersion));
    }

    [Theory]
    [InlineData(Oblivion2004, 12u)] // UV12 is not an Oblivion value
    [InlineData(Fo3Fnv, 10u)]       // UV10 is not an FO3/FNV value
    [InlineData(NetImmerse10010, 11u)] // pre-20 legacy header layout — documented limitation, not yet handled
    [InlineData(0x04000002u, 0u)]   // Morrowind 4.0.0.2 — handled by a different path, not this gate
    public void RejectsNonMatchingVersions(uint binaryVersion, uint userVersion)
    {
        Assert.False(NifParser.IsBethesdaVersion(binaryVersion, userVersion));
    }
}

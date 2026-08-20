using System.Text.Json;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Commands.Dmp;

/// <summary>
///     Pins the hand-curated Hoover Dam attribution in <c>data/cell_worldspace_authority.json</c>.
///     <para>
///         <c>Fallout_Release_Beta.xex21.dmp</c> (Feb 2010) carries a page-truncated disk-format ESM
///         cache fragment holding 467 REFR records (FormIDs 0x001352C6–0x001354A2, the Hoover Dam
///         power-plant <c>Utl*</c> tileset + 3 <c>NVHooverDamGenerator</c> turbines) whose owning CELL
///         header fell outside the captured pages. The parent was recovered from the dump itself
///         (2026-08-19): sibling door <c>VHDPPToLLDoor03REF</c> 0x00135320 — allocated inside the same
///         FormID band, positioned inside the same XYZ envelope — is captured as a runtime REFR whose
///         parent-cell pointer chases to TESObjectCELL 0x001206D8, Feb-2010 EditorID
///         <c>HooverDamIntPowerPlant</c> ("Hoover Dam Power Plant"; renamed PowerPlantNorth in xex22,
///         split into PP01–04 by retail — which is why these ref FormIDs exist in NO ESM of any era and
///         no sibling dump, and why only a hand seed can attribute them).
///     </para>
///     <para>
///         The seed lives in the authority's <c>references</c> map, the sanctioned channel for
///         hand-attributed entries (<c>dmp build-cell-authority</c> preserves seeds on rebuild — seed
///         wins on conflict). These pins keep a rebuild or a hand edit from silently dropping it.
///     </para>
/// </summary>
public sealed class CellWorldspaceAuthorityHooverSeedTests
{
    private const uint BandFirst = 0x001352C6;
    private const uint BandLast = 0x001354A2;
    private const string HooverPowerPlant = "0x001206D8";

    /// <summary>
    ///     FormIDs inside the band that are NOT part of the orphan run: unused/deleted editor
    ///     allocations, plus 0x00135320 (<c>VHDPPToLLDoor03REF</c>, the persistent door whose runtime
    ///     struct PROVED the attribution). The door legitimately appears in the corpus references with
    ///     its JULY-era parent (0x0013EC23 HooverDamIntPowerPlant03, from that ESM's GRUP) — the pin
    ///     only demands that no gap ride the hand seed into 0x001206D8.
    /// </summary>
    private static readonly uint[] BandGaps =
    [
        0x00135320, 0x0013536C, 0x0013536D, 0x00135389, 0x001353CB,
        0x001353CC, 0x001353CE, 0x001353CF, 0x00135402, 0x0013545D,
    ];

    private static JsonElement LoadReferences()
    {
        var path = Path.Combine(SourceContract.RepoRoot, "data", "cell_worldspace_authority.json");
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("references").Clone();
    }

    [Fact]
    public void EveryOrphanInTheBandMapsToHooverDamPowerPlant()
    {
        var references = LoadReferences();
        var gaps = new HashSet<uint>(BandGaps);
        var mapped = 0;
        for (var fid = BandFirst; fid <= BandLast; fid++)
        {
            var key = $"0x{fid:X8}";
            var present = references.TryGetProperty(key, out var value);
            if (gaps.Contains(fid))
            {
                if (present)
                {
                    Assert.NotEqual(HooverPowerPlant, value.GetString());
                }

                continue;
            }

            Assert.True(present, $"orphan {key} lost its hand-curated Hoover attribution");
            Assert.Equal(HooverPowerPlant, value.GetString());
            mapped++;
        }

        // 477-slot band minus 10 gaps = the 467 records decoded from the xex21 cache fragment.
        Assert.Equal(467, mapped);
    }

    [Fact]
    public void TheTargetCellIsAKnownInterior()
    {
        var path = Path.Combine(SourceContract.RepoRoot, "data", "cell_worldspace_authority.json");
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var cell = doc.RootElement.GetProperty("cells").GetProperty(HooverPowerPlant);

        Assert.True(cell.GetProperty("is_interior").GetBoolean());
        // The corpus entry carries the retail identity; xex21's own captured runtime cell supplies the
        // Feb-2010 name (HooverDamIntPowerPlant) at load time, so this is only the fallback label.
        Assert.StartsWith("HooverDamIntPowerPlant", cell.GetProperty("editor_id").GetString(),
            StringComparison.Ordinal);
    }
}

using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Games;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Parsers;

/// <summary>
///     TES4 city child worldspaces render the PARENT worldspace's terrain wherever the child cell
///     has no usable LAND (user-verified in-game A/B at AnvilWorld (-45,-8), 2026-07-21; proven at
///     ICMarketDistrict by flora planted ~500 units above the relic sculpt, 2026-07-22). Two parts
///     mirror that: (1) pre-release relic LANDs — UNCOMPRESSED with no BTXT/ATXT/VTXT layers; 579
///     exist in retail Oblivion.esm (29 carrying the legacy VTEX grid + 550 bare VHGT sculpts), all
///     in Tamriel-parented child worlds — are skipped at attach time (retail's 9,338 COMPRESSED
///     layer-less ocean/plane LANDs are genuine and must attach); (2) child-worldspace cells without
///     a heightmap borrow the WNAM ancestor's same-grid terrain by reference after linkage.
/// </summary>
public class Tes4ParentTerrainInheritanceTests
{
    private const uint CompressedFlag = 0x00040000;

    private static LandHeightmap Heightmap(float baseHeight)
    {
        return new LandHeightmap
        {
            HeightOffset = baseHeight,
            HeightDeltas = new sbyte[1089]
        };
    }

    private static ExtractedLandRecord Land(
        uint formId, uint parentCell, float baseHeight, int vtex, int btxt, int atxt, int vtxt,
        uint flags = 0)
    {
        return new ExtractedLandRecord
        {
            Header = new DetectedMainRecord("LAND", 100, flags, formId, 0, false),
            ParentCellFormId = parentCell,
            Heightmap = Heightmap(baseHeight),
            VtexCount = vtex,
            BtxtCount = btxt,
            AtxtCount = atxt,
            VtxtCount = vtxt
        };
    }

    private static EsmRecordScanResult ScanResult(BethesdaGame game, params ExtractedLandRecord[] lands)
    {
        var result = MakeScanResult([]) with { Game = game };
        result.LandRecords.AddRange(lands);
        return result;
    }

    [Fact]
    public void AttachTerrainData_Tes4VtexOnlyRelicLandIsSkipped()
    {
        var relicCell = new CellRecord { FormId = 0x100, GridX = -45, GridY = -8 };
        var normalCell = new CellRecord { FormId = 0x200, GridX = -46, GridY = -8 };
        var cells = new List<CellRecord> { relicCell, normalCell };
        var scan = ScanResult(BethesdaGame.Oblivion,
            Land(0x1000, 0x100, 31f, 64, 0, 0, 0), // the relic signature
            Land(0x1001, 0x200, 29f, 0, 4, 9, 9));

        CellRecordHandler.AttachTerrainDataFromLandRecords(cells, scan);

        Assert.Null(cells[0].Heightmap);
        Assert.Equal(29f, cells[1].Heightmap?.HeightOffset);
    }

    [Fact]
    public void AttachTerrainData_Tes4UntexturedUncompressedRelicIsSkipped()
    {
        // The 550-strong relic species: uncompressed, NO layers, no VTEX either (bare VHGT sculpt —
        // e.g. ICMarketDistrict 0x0001BEEF at base 381, ~500 units below the district's flora).
        var relicCell = new CellRecord { FormId = 0x100, GridX = 8, GridY = 16 };
        var cells = new List<CellRecord> { relicCell };
        var scan = ScanResult(BethesdaGame.Oblivion,
            Land(0x1000, 0x100, 381f, 0, 0, 0, 0));

        CellRecordHandler.AttachTerrainDataFromLandRecords(cells, scan);

        Assert.Null(cells[0].Heightmap); // left for the parent-terrain inheritance pass
    }

    [Fact]
    public void AttachTerrainData_Tes4CompressedLayerLessLandStillAttaches()
    {
        // The critical protection: retail carries 9,338 COMPRESSED layer-less LANDs (Tamriel/SEWorld
        // ocean + plane cells on the engine-default texture) — genuine terrain that must attach.
        // Guards the !IsCompressed condition against a future "simplification" to layer-absence.
        var cell = new CellRecord { FormId = 0x100, GridX = 0, GridY = 0 };
        var cells = new List<CellRecord> { cell };
        var scan = ScanResult(BethesdaGame.Oblivion,
            Land(0x1000, 0x100, 7f, 0, 0, 0, 0, CompressedFlag));

        CellRecordHandler.AttachTerrainDataFromLandRecords(cells, scan);

        Assert.Equal(7f, cells[0].Heightmap?.HeightOffset);
    }

    [Fact]
    public void AttachTerrainData_Fo3FamilyKeepsVtexLands()
    {
        // The relic gate is TES4-era only — an FNV LAND with a VTEX block still attaches.
        var cell = new CellRecord { FormId = 0x100, GridX = 0, GridY = 0 };
        var cells = new List<CellRecord> { cell };
        var scan = ScanResult(BethesdaGame.FalloutNewVegas,
            Land(0x1000, 0x100, 12f, 64, 0, 0, 0));

        CellRecordHandler.AttachTerrainDataFromLandRecords(cells, scan);

        Assert.Equal(12f, cells[0].Heightmap?.HeightOffset);
    }

    [Fact]
    public void AttachTerrainData_UnknownGameNeverDropsLands()
    {
        // The relic gate is destructive — it must require an AFFIRMATIVELY-Oblivion scan.
        // Unknown-game synthetic/DMP-carve fixtures own uncompressed layer-less LANDs legitimately.
        var cell = new CellRecord { FormId = 0x100, GridX = 0, GridY = 0 };
        var cells = new List<CellRecord> { cell };
        var scan = ScanResult(BethesdaGame.Unknown,
            Land(0x1000, 0x100, 3f, 0, 0, 0, 0));

        CellRecordHandler.AttachTerrainDataFromLandRecords(cells, scan);

        Assert.Equal(3f, cells[0].Heightmap?.HeightOffset);
    }

    [Fact]
    public void AttachTerrainData_Fo3FamilyKeepsUncompressedLayerLessLands()
    {
        // DMP-carved partial FNV LANDs are decompressed in memory and often layer-less; the TES4
        // relic gate must never reach them.
        var cell = new CellRecord { FormId = 0x100, GridX = 0, GridY = 0 };
        var cells = new List<CellRecord> { cell };
        var scan = ScanResult(BethesdaGame.FalloutNewVegas,
            Land(0x1000, 0x100, 5f, 0, 0, 0, 0));

        CellRecordHandler.AttachTerrainDataFromLandRecords(cells, scan);

        Assert.Equal(5f, cells[0].Heightmap?.HeightOffset);
    }

    [Fact]
    public void InheritTerrain_ChildCellBorrowsParentSameGridTerrain()
    {
        var parentHeightmap = Heightmap(29f);
        var parentVisuals = new LandVisualData();
        var parentCell = new CellRecord
        {
            FormId = 0x10, GridX = -45, GridY = -8, Heightmap = parentHeightmap,
            LandVisualData = parentVisuals
        };
        var childCell = new CellRecord { FormId = 0x20, GridX = -45, GridY = -8 };
        var untouchedChild = new CellRecord
        {
            FormId = 0x21, GridX = -46, GridY = -8, Heightmap = Heightmap(31f)
        };
        var worldspaces = new List<WorldspaceRecord>
        {
            new() { FormId = 0x3C, Cells = [parentCell] },
            new()
            {
                FormId = 0x1C31A, ParentWorldspaceFormId = 0x3C,
                Cells = [childCell, untouchedChild]
            }
        };

        CellLinkageHandler.InheritTerrainFromParentWorldspaces(worldspaces, BethesdaGame.Oblivion);

        Assert.Same(parentHeightmap, childCell.Heightmap);
        Assert.Same(parentVisuals, childCell.LandVisualData);
        Assert.Equal(31f, untouchedChild.Heightmap?.HeightOffset); // own terrain never replaced
    }

    [Fact]
    public void InheritTerrain_WalksWnamChainAndGuardsCycles()
    {
        var rootCell = new CellRecord { FormId = 0x10, GridX = 1, GridY = 1, Heightmap = Heightmap(5f) };
        var grandchildCell = new CellRecord { FormId = 0x30, GridX = 1, GridY = 1 };
        var worldspaces = new List<WorldspaceRecord>
        {
            new() { FormId = 0x1, Cells = [rootCell] },
            // Middle world has no cells — the walk continues to the root.
            new() { FormId = 0x2, ParentWorldspaceFormId = 0x1 },
            new() { FormId = 0x3, ParentWorldspaceFormId = 0x2, Cells = [grandchildCell] },
            // Malformed cycle A↔B with cells but no terrain must terminate without inheriting.
            new()
            {
                FormId = 0xA, ParentWorldspaceFormId = 0xB,
                Cells = [new CellRecord { FormId = 0x40, GridX = 2, GridY = 2 }]
            },
            new()
            {
                FormId = 0xB, ParentWorldspaceFormId = 0xA,
                Cells = [new CellRecord { FormId = 0x41, GridX = 2, GridY = 2 }]
            }
        };

        CellLinkageHandler.InheritTerrainFromParentWorldspaces(worldspaces, BethesdaGame.Oblivion);

        Assert.Same(rootCell.Heightmap, grandchildCell.Heightmap);
    }

    [Fact]
    public void InheritTerrain_Fo3FamilyIsUntouched()
    {
        var parentCell = new CellRecord { FormId = 0x10, GridX = 0, GridY = 0, Heightmap = Heightmap(1f) };
        var childCell = new CellRecord { FormId = 0x20, GridX = 0, GridY = 0 };
        var worldspaces = new List<WorldspaceRecord>
        {
            new() { FormId = 0x1, Cells = [parentCell] },
            new() { FormId = 0x2, ParentWorldspaceFormId = 0x1, Cells = [childCell] }
        };

        CellLinkageHandler.InheritTerrainFromParentWorldspaces(worldspaces, BethesdaGame.FalloutNewVegas);

        Assert.Null(childCell.Heightmap);
    }
}
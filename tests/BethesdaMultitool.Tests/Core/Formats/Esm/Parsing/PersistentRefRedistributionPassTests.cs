using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Minidump;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Pins the OBLIV-3 fix: <see cref="PersistentRefRedistributionPass" /> may synthesize a virtual
///     exterior tile for a persistent ref only when parsing a DMP capture (whose streaming cache can be
///     empty). For a plain ESM parse, a worldspace with no real exterior cell at the ref's grid is genuinely
///     interior-only (e.g. Oblivion's ArkvedsTowerWorld), so synthesizing a (0,0) tile from interior-local
///     persistent-ref coords would fabricate a phantom virtual cell. The ref must stay on its container.
/// </summary>
public sealed class PersistentRefRedistributionPassTests
{
    // A persistent container cell in an interior-only worldspace, holding one persistent ref whose
    // (near-origin) coords map to a grid that has NO real exterior cell.
    private static (List<CellRecord> cells, Dictionary<uint, CellRecord> byFormId) BuildInteriorOnlyWorldspace()
    {
        var pref = new PlacedReference
        {
            FormId = 0x0900_0001,
            BaseFormId = 0x0900_0002,
            RecordType = "REFR",
            X = 200f,
            Y = 200f,
            Z = 0f,
            IsPersistent = true,
            AssignmentSource = "ParentCell"
        };

        var persistentCell = new CellRecord
        {
            FormId = 0x0010_0000,
            EditorId = "ArkvedsTowerWorldPersistent",
            WorldspaceFormId = 0x0009_88A3, // ArkvedsTowerWorld — no exterior cells
            IsPersistentCell = true,
            PlacedObjects = [pref]
        };

        var cells = new List<CellRecord> { persistentCell };
        var byFormId = new Dictionary<uint, CellRecord> { [persistentCell.FormId] = persistentCell };
        return (cells, byFormId);
    }

    [Fact]
    public void Run_EsmParse_NoMinidump_DoesNotSynthesizeVirtualTile()
    {
        var (cells, byFormId) = BuildInteriorOnlyWorldspace();
        var context = new RecordParserContext(new EsmRecordScanResult { Game = BethesdaGame.Oblivion });
        Assert.Null(context.MinidumpInfo); // sanity: this is the ESM (no-DMP) path

        var moved = PersistentRefRedistributionPass.Run(cells, byFormId, context);

        Assert.Equal(0, moved);
        Assert.DoesNotContain(cells, c => c.IsVirtual);
        Assert.Single(cells); // no phantom (0,0) tile added
        Assert.Single(cells[0].PlacedObjects); // the ref stayed on its persistent container
    }

    [Fact]
    public void Run_DmpParse_WithMinidump_StillSynthesizesVirtualTile()
    {
        var (cells, byFormId) = BuildInteriorOnlyWorldspace();
        var context = new RecordParserContext(
            new EsmRecordScanResult { Game = BethesdaGame.Oblivion },
            minidumpInfo: new MinidumpInfo());
        Assert.NotNull(context.MinidumpInfo); // sanity: this is the DMP path

        var moved = PersistentRefRedistributionPass.Run(cells, byFormId, context);

        Assert.Equal(1, moved);
        Assert.Contains(cells, c => c.IsVirtual); // DMP synthesis path preserved
    }
}

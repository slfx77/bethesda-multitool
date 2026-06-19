using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

public sealed class CoordBasedCellPairingPassTests
{
    [Fact]
    public void Folds_DmpNew_Into_Master_When_Same_Worldspace_And_Coord()
    {
        // Master cell 0x000DDF1C at (7,7) of WastelandNV; proto DMP cell at the same coord
        // but with a different FormID (0x01001670). Without pairing, both survive into the
        // output and the engine destroys one. After Reconcile: single DmpOverride entry
        // keyed on master's FormID, DMP model attached.
        const uint masterCellFid = 0x000DDF1Cu;
        const uint protoCellFid = 0x01001670u;
        const uint worldspaceFid = 0x000DA726u;

        var masterCtx = MakeExteriorContext(masterCellFid, worldspaceFid);
        var masterRec = MakeMasterCellRecordWithXclc(masterCellFid, 7, 7);
        var protoCell = new CellRecord
        {
            FormId = protoCellFid,
            WorldspaceFormId = worldspaceFid,
            GridX = 7,
            GridY = 7
        };

        var input = new List<CellCatalogEntry>
        {
            new()
            {
                CellFormId = masterCellFid, Source = SourceKind.MasterOnly, MasterContext = masterCtx,
                MasterRecord = masterRec
            },
            new() { CellFormId = protoCellFid, Source = SourceKind.DmpNew, DmpModel = protoCell }
        };

        var result = CoordBasedCellPairingPass.Reconcile(
            input,
            new Dictionary<uint, ParsedMainRecord> { [masterCellFid] = masterRec });

        var entry = Assert.Single(result);
        Assert.Equal(masterCellFid, entry.CellFormId);
        Assert.Equal(SourceKind.DmpOverride, entry.Source);
        Assert.Same(masterCtx, entry.MasterContext);
        Assert.Same(masterRec, entry.MasterRecord);
        Assert.Same(protoCell, entry.DmpModel);
    }

    [Fact]
    public void Leaves_DmpNew_Untouched_When_No_Master_At_Same_Coord()
    {
        // Proto cell at (10,10) of a NEW worldspace; master has nothing there. After
        // Reconcile the proto stays as DmpNew and gets a fresh FormID downstream.
        const uint protoCellFid = 0x01001670u;
        const uint newWorldspaceFid = 0x01001F51u;
        var protoCell = new CellRecord
        {
            FormId = protoCellFid,
            WorldspaceFormId = newWorldspaceFid,
            GridX = 10,
            GridY = 10
        };

        var input = new List<CellCatalogEntry>
        {
            new() { CellFormId = protoCellFid, Source = SourceKind.DmpNew, DmpModel = protoCell }
        };

        var result = CoordBasedCellPairingPass.Reconcile(
            input, new Dictionary<uint, ParsedMainRecord>());

        var entry = Assert.Single(result);
        Assert.Equal(SourceKind.DmpNew, entry.Source);
        Assert.Equal(protoCellFid, entry.CellFormId);
    }

    [Fact]
    public void Leaves_DmpOverride_Entries_Untouched()
    {
        // A DMP cell that ALREADY paired with master by FormID (DmpOverride) is left alone
        // — the pass operates only on DmpNew entries.
        const uint cellFid = 0x000ABCDEu;
        const uint worldspaceFid = 0x000DA726u;
        var masterCtx = MakeExteriorContext(cellFid, worldspaceFid);
        var masterRec = MakeMasterCellRecordWithXclc(cellFid, 0, 0);
        var dmpCell = new CellRecord
        {
            FormId = cellFid,
            WorldspaceFormId = worldspaceFid,
            GridX = 0,
            GridY = 0
        };

        var input = new List<CellCatalogEntry>
        {
            new()
            {
                CellFormId = cellFid,
                Source = SourceKind.DmpOverride,
                MasterContext = masterCtx,
                MasterRecord = masterRec,
                DmpModel = dmpCell
            }
        };

        var result = CoordBasedCellPairingPass.Reconcile(
            input, new Dictionary<uint, ParsedMainRecord> { [cellFid] = masterRec });

        var entry = Assert.Single(result);
        Assert.Equal(SourceKind.DmpOverride, entry.Source);
    }

    [Fact]
    public void Skips_Master_Cells_Without_Worldspace_Context()
    {
        // Interior cells (no WorldspaceFormId on the master context) can't be coord-paired
        // because they don't have a coord space. Proto cells should stay DmpNew.
        const uint masterCellFid = 0x000ABCDEu;
        const uint protoCellFid = 0x01001670u;
        var interiorCtx = new PcEsmCellContext
        {
            CellFormId = masterCellFid,
            IsInterior = true,
            BlockGroupType = 2,
            SubblockGroupType = 3
            // WorldspaceFormId is null for interiors
        };
        var masterRec = MakeMasterCellRecordWithXclc(masterCellFid, 0, 0);
        var protoCell = new CellRecord
        {
            FormId = protoCellFid,
            WorldspaceFormId = 0x000DA726u,
            GridX = 0,
            GridY = 0
        };

        var input = new List<CellCatalogEntry>
        {
            new()
            {
                CellFormId = masterCellFid, Source = SourceKind.MasterOnly, MasterContext = interiorCtx,
                MasterRecord = masterRec
            },
            new() { CellFormId = protoCellFid, Source = SourceKind.DmpNew, DmpModel = protoCell }
        };

        var result = CoordBasedCellPairingPass.Reconcile(
            input, new Dictionary<uint, ParsedMainRecord> { [masterCellFid] = masterRec });

        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.Source == SourceKind.MasterOnly);
        Assert.Contains(result, e => e.Source == SourceKind.DmpNew);
    }

    [Fact]
    public void Skips_DmpNew_Without_Worldspace_Or_Coord()
    {
        // DmpNew without complete coord (e.g. interior cell with no grid) shouldn't pair.
        const uint masterCellFid = 0x000DDF1Cu;
        const uint protoCellFid = 0x01001670u;
        const uint worldspaceFid = 0x000DA726u;

        var masterCtx = MakeExteriorContext(masterCellFid, worldspaceFid);
        var masterRec = MakeMasterCellRecordWithXclc(masterCellFid, 0, 0);
        var protoCell = new CellRecord
        {
            FormId = protoCellFid
            // WorldspaceFormId null → no coord key → no pairing
        };

        var input = new List<CellCatalogEntry>
        {
            new()
            {
                CellFormId = masterCellFid, Source = SourceKind.MasterOnly, MasterContext = masterCtx,
                MasterRecord = masterRec
            },
            new() { CellFormId = protoCellFid, Source = SourceKind.DmpNew, DmpModel = protoCell }
        };

        var result = CoordBasedCellPairingPass.Reconcile(
            input, new Dictionary<uint, ParsedMainRecord> { [masterCellFid] = masterRec });

        Assert.Equal(2, result.Count);
    }

    private static PcEsmCellContext MakeExteriorContext(uint cellFormId, uint worldspaceFormId)
    {
        return new PcEsmCellContext
        {
            CellFormId = cellFormId,
            IsInterior = false,
            WorldspaceFormId = worldspaceFormId,
            BlockGroupType = 4,
            SubblockGroupType = 5,
            BlockLabel = [0, 0, 0, 0],
            SubblockLabel = [0, 0, 0, 0]
        };
    }

    private static ParsedMainRecord MakeMasterCellRecordWithXclc(uint cellFormId, int gridX, int gridY)
    {
        // XCLC payload: int32 gridX + int32 gridY (8 bytes minimum; full XCLC has more,
        // but TryGetMasterCellCoord only reads the first 8 bytes).
        var xclcData = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(xclcData.AsSpan(0, 4), gridX);
        BinaryPrimitives.WriteInt32LittleEndian(xclcData.AsSpan(4, 4), gridY);

        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "CELL",
                DataSize = 0,
                Flags = 0,
                FormId = cellFormId,
                Timestamp = 0,
                VcsInfo = 0,
                Version = 15
            },
            Offset = 0,
            Subrecords =
            [
                new ParsedSubrecord { Signature = "XCLC", Data = xclcData }
            ]
        };
    }
}
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public class CellCaptureUnionerTests
{
    [Fact]
    public void Union_WhenFirstCaptureLacksGrid_KeepsGridFromSecondaryCapture()
    {
        var firstCapture = new CellRecord
        {
            FormId = 0x0010C2B9,
            WorldspaceFormId = 0x0010B96F,
            WorldspaceAssignmentSource = "Authority"
        };
        var secondCapture = new CellRecord
        {
            FormId = 0x0010C2B9,
            GridX = 3,
            GridY = -2,
            WorldspaceFormId = 0x0010B96F,
            WorldspaceAssignmentSource = "FragmentRun",
            PlacedObjects =
            [
                new PlacedReference
                {
                    FormId = 0x0010C398,
                    BaseFormId = 0x000F0698,
                    RecordType = "REFR"
                }
            ]
        };

        var result = CellCaptureUnioner.Union([firstCapture, secondCapture]);
        var cell = Assert.Single(result.Cells);

        Assert.Equal(3, cell.GridX);
        Assert.Equal(-2, cell.GridY);
        Assert.Equal(0x0010B96Fu, cell.WorldspaceFormId);
        Assert.Equal("Authority", cell.WorldspaceAssignmentSource);
        Assert.Single(cell.PlacedObjects);
    }

    [Fact]
    public void Union_MergesDistinctCandidateWorldspacesAndLinkedCells()
    {
        var result = CellCaptureUnioner.Union(
        [
            new CellRecord
            {
                FormId = 0x0010C2B9,
                CandidateWorldspaceFormIds = [0x0010B96F],
                LinkedCellFormIds = [0x00000001]
            },
            new CellRecord
            {
                FormId = 0x0010C2B9,
                CandidateWorldspaceFormIds = [0x0010B96F, 0x00031E12],
                LinkedCellFormIds = [0x00000001, 0x00000002]
            }
        ]);

        var cell = Assert.Single(result.Cells);

        Assert.Equal([0x0010B96Fu, 0x00031E12u], cell.CandidateWorldspaceFormIds);
        Assert.Equal([0x00000001u, 0x00000002u], cell.LinkedCellFormIds);
    }
}

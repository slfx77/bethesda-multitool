using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Parsing;

public class CellLinkageWorldspaceInferenceTests
{
    [Fact]
    public void InferCellWorldspaces_DoesNotAssignWhenBoundsOverlap()
    {
        var cells = new List<CellRecord>
        {
            new()
            {
                FormId = 0x1000,
                GridX = 3,
                GridY = -2
            }
        };
        var worldspaces = new List<WorldspaceRecord>
        {
            CreateWorldspace(0x10, "Large", -3, 5, 7, -8),
            CreateWorldspace(0x20, "Small", -1, 5, 3, -3)
        };

        CellLinkageHandler.InferCellWorldspaces(cells, worldspaces);

        Assert.Null(cells[0].WorldspaceFormId);
        Assert.Equal("AmbiguousBounds", cells[0].WorldspaceAssignmentSource);
        Assert.Equal(new uint[] { 0x10, 0x20 }, cells[0].CandidateWorldspaceFormIds);
    }

    [Fact]
    public void ResolveRuntimeAnchoredCellRuns_AssignsAdjacentAmbiguousCellsToRuntimeAnchor()
    {
        var cells = new List<CellRecord>
        {
            new()
            {
                FormId = 0x1000,
                GridX = 3,
                GridY = -1,
                WorldspaceFormId = 0x10,
                WorldspaceAssignmentSource = "RuntimeCellMap"
            },
            new()
            {
                FormId = 0x1001,
                GridX = 3,
                GridY = -2,
                WorldspaceAssignmentSource = "AmbiguousBounds",
                CandidateWorldspaceFormIds = [0x10, 0x20]
            },
            new()
            {
                FormId = 0x1002,
                GridX = 3,
                GridY = -3,
                WorldspaceAssignmentSource = "AmbiguousBounds",
                CandidateWorldspaceFormIds = [0x10, 0x20]
            }
        };
        var worldspaces = new List<WorldspaceRecord>
        {
            CreateWorldspace(0x10, "TheStripWorld", -3, 5, 7, -8),
            CreateWorldspace(0x20, "GreenhouseWorld01", -1, 5, 3, -3)
        };
        var runtimeMaps = new Dictionary<uint, RuntimeWorldspaceData>
        {
            [0x10] = new()
            {
                FormId = 0x10,
                Cells =
                [
                    new RuntimeCellMapEntry
                    {
                        CellFormId = 0x1000,
                        GridX = 3,
                        GridY = -1,
                        WorldspaceFormId = 0x10
                    }
                ]
            }
        };

        var reassigned = CellLinkageHandler.ResolveRuntimeAnchoredCellRuns(cells, worldspaces, runtimeMaps);

        Assert.Equal(2, reassigned);
        Assert.Equal(0x10u, cells[1].WorldspaceFormId);
        Assert.Equal(0x10u, cells[2].WorldspaceFormId);
        Assert.Equal("FragmentRun", cells[1].WorldspaceAssignmentSource);
        Assert.Equal("FragmentRun", cells[2].WorldspaceAssignmentSource);
    }

    [Fact]
    public void ResolveRuntimeAnchoredCellRuns_DoesNotAssignWhenAnchorWorldspaceIsNotCandidate()
    {
        var cells = new List<CellRecord>
        {
            new()
            {
                FormId = 0x1000,
                GridX = 3,
                GridY = -1,
                WorldspaceFormId = 0x10,
                WorldspaceAssignmentSource = "RuntimeCellMap"
            },
            new()
            {
                FormId = 0x1001,
                GridX = 3,
                GridY = -2,
                WorldspaceAssignmentSource = "AmbiguousBounds",
                CandidateWorldspaceFormIds = [0x20]
            }
        };
        var worldspaces = new List<WorldspaceRecord>
        {
            CreateWorldspace(0x10, "TheStripWorld", -3, 5, 7, -8),
            CreateWorldspace(0x20, "Other", -1, 5, 3, -3)
        };
        var runtimeMaps = new Dictionary<uint, RuntimeWorldspaceData>
        {
            [0x10] = new()
            {
                FormId = 0x10,
                Cells =
                [
                    new RuntimeCellMapEntry
                    {
                        CellFormId = 0x1000,
                        GridX = 3,
                        GridY = -1,
                        WorldspaceFormId = 0x10
                    }
                ]
            }
        };

        var reassigned = CellLinkageHandler.ResolveRuntimeAnchoredCellRuns(cells, worldspaces, runtimeMaps);

        Assert.Equal(0, reassigned);
        Assert.Null(cells[1].WorldspaceFormId);
    }

    private static WorldspaceRecord CreateWorldspace(
        uint formId,
        string editorId,
        short nwX,
        short nwY,
        short seX,
        short seY)
    {
        return new WorldspaceRecord
        {
            FormId = formId,
            EditorId = editorId,
            MapNWCellX = nwX,
            MapNWCellY = nwY,
            MapSECellX = seX,
            MapSECellY = seY
        };
    }
}

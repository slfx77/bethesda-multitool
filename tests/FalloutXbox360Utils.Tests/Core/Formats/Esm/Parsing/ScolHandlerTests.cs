using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Model-shape tests for the SCOL typed parser path. The full handler pipeline depends
///     on a real <see cref="FalloutXbox360Utils.Core.Formats.Esm.Parsing.RecordParserContext" />
///     (scan result + memory accessor), which is exercised by integration runs against real
///     ESM samples. These tests pin the model contract so encoder/handler stay in sync.
/// </summary>
public class ScolHandlerTests
{
    [Fact]
    public void StaticCollectionPlacement_HoldsSevenFloats()
    {
        var p = new StaticCollectionPlacement(1, 2, 3, 4, 5, 6, 7);

        Assert.Equal(1f, p.X);
        Assert.Equal(2f, p.Y);
        Assert.Equal(3f, p.Z);
        Assert.Equal(4f, p.RotX);
        Assert.Equal(5f, p.RotY);
        Assert.Equal(6f, p.RotZ);
        Assert.Equal(7f, p.Scale);
    }

    [Fact]
    public void StaticCollectionPart_CarriesOnamAndPlacementList()
    {
        var part = new StaticCollectionPart
        {
            OnamFormId = 0x0003D377,
            Placements =
            {
                new StaticCollectionPlacement(0, 0, 0, 0, 0, 0, 1),
                new StaticCollectionPlacement(100, 200, 300, 0, 0, 0, 1.5f)
            }
        };

        Assert.Equal(0x0003D377u, part.OnamFormId);
        Assert.Equal(2, part.Placements.Count);
        Assert.Equal(100f, part.Placements[1].X);
        Assert.Equal(1.5f, part.Placements[1].Scale);
    }

    [Fact]
    public void StaticCollectionRecord_AggregatesPartsInStreamOrder()
    {
        var scol = new StaticCollectionRecord
        {
            FormId = 0x100,
            EditorId = "ScolMixed",
            ModelPath = "meshes/m.nif",
            Parts =
            {
                new StaticCollectionPart { OnamFormId = 0xAAAA },
                new StaticCollectionPart { OnamFormId = 0xBBBB }
            }
        };

        Assert.Equal(0xAAAAu, scol.Parts[0].OnamFormId);
        Assert.Equal(0xBBBBu, scol.Parts[1].OnamFormId);
        Assert.Equal("ScolMixed", scol.EditorId);
        Assert.Equal("meshes/m.nif", scol.ModelPath);
    }
}

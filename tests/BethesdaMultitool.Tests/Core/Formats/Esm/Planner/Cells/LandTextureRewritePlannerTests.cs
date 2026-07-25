using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

public sealed class LandTextureRewritePlannerTests
{
    [Fact]
    public void Apply_Remaps_Valid_Ltex_And_Grass_And_Drops_Dangling_Prototype_Refs()
    {
        const uint cellSource = 0x0010B901;
        const uint ltexSource = 0xAA001000;
        const uint ltexEmitted = 0x01000810;
        const uint masterLtex = 0x00001000;
        const uint missingLtex = 0xAA001001;
        const uint grassSource = 0xAA002000;
        const uint grassEmitted = 0x01000820;
        const uint missingGrass = 0xAA002001;

        var land = new CellLandDecision
        {
            CellSourceFormId = cellSource,
            Heightmap = new LandHeightmap { HeightDeltas = new sbyte[33 * 33] },
            HeightSource = CellLandHeightSource.CapturedHeightmap,
            VisualData = new LandVisualData
            {
                TextureLayers =
                [
                    new LandTextureLayer { Kind = LandTextureLayerKind.Base, TextureFormId = ltexSource },
                    new LandTextureLayer { Kind = LandTextureLayerKind.Base, TextureFormId = masterLtex },
                    new LandTextureLayer { Kind = LandTextureLayerKind.Base, TextureFormId = missingLtex }
                ],
                TextureIndices = [ltexSource, masterLtex, missingLtex, 0]
            }
        };
        var landPlan = Record("LAND", 0x01000830, land);
        var cell = new CellPlan
        {
            CellFormId = 0x01000800,
            CellRecordPlan = Record("CELL", 0x01000800, null),
            Context = new PcEsmCellContext
            {
                CellFormId = 0x01000800,
                IsInterior = false,
                WorldspaceFormId = 0x0010B96F,
                BlockGroupType = 4,
                SubblockGroupType = 5,
                BlockLabel = [0, 0, 0, 0],
                SubblockLabel = [0, 0, 0, 0]
            },
            PersistentChildren = [],
            VwdChildren = [],
            TemporaryChildren = [landPlan]
        };

        var ltex = new LandscapeTextureRecord
        {
            FormId = ltexSource,
            GrassFormIds = [grassSource, missingGrass]
        };
        var records = ImmutableArray.Create(
            Record("LTEX", ltexEmitted, ltex, ltexSource),
            Record("GRAS", grassEmitted, new object(), grassSource));
        var plan = EmptyPlan() with
        {
            Records = records,
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(cell.CellFormId, cell),
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty
                .Add(ltexSource, ltexEmitted)
                .Add(grassSource, grassEmitted)
        };
        var master = new Dictionary<uint, ParsedMainRecord>
        {
            [masterLtex] = Parsed("LTEX", masterLtex)
        };

        var rewritten = LandTextureRewritePlanner.Apply(plan, master);

        var rewrittenLand = Assert.IsType<CellLandDecision>(
            Assert.Single(rewritten.CellsByFormId[cell.CellFormId].TemporaryChildren).Model);
        Assert.Equal(
            [ltexEmitted, masterLtex],
            rewrittenLand.VisualData!.TextureLayers.Select(layer => layer.TextureFormId));
        Assert.NotNull(rewrittenLand.VisualData.TextureIndices);
        Assert.Equal([ltexEmitted, masterLtex, missingLtex, 0u], rewrittenLand.VisualData.TextureIndices);

        var rewrittenLtex = Assert.IsType<LandscapeTextureRecord>(
            Assert.Single(rewritten.Records, record => record.Type == "LTEX").Model);
        Assert.Equal([grassEmitted], rewrittenLtex.GrassFormIds);
        Assert.Contains(rewritten.Diagnostics, diagnostic => diagnostic.Code == "land.texture-layer-dropped");
        Assert.Contains(rewritten.Diagnostics, diagnostic => diagnostic.Code == "land.ltex-grass-dropped");
    }

    private static RecordPlan Record(string type, uint formId, object? model, uint? source = null)
    {
        return new RecordPlan
        {
            Type = type,
            Disposition = RecordDisposition.New,
            FormId = formId,
            SourceFormId = source,
            Model = model,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
    }

    private static ParsedMainRecord Parsed(string type, uint formId)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = type,
                FormId = formId,
                Version = 15
            }
        };
    }

    private static EmitPlan EmptyPlan()
    {
        return new EmitPlan
        {
            Records = ImmutableArray<RecordPlan>.Empty,
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = ImmutableHashSet<uint>.Empty,
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty,
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = 0x800,
                PlannerCoverage = ImmutableHashSet<string>.Empty
            }
        };
    }
}
using System.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class ObjectIndexBuilderTests
{
    [Fact]
    public void BuildAndEnrich_ResolvesScolModelPathOntoPlacedReference()
    {
        // Regression: a placed reference whose base object is a SCOL (static collection, e.g.
        // SSHQExterior03) must resolve to the SCOL's merged meshes\scol\*.nif. SCOL was omitted from
        // the model index, so these refs got no ModelPath and never rendered — masked in the viewer
        // by the imposter stand-in that sits nearby (see viewer_imposter_doubling).
        const uint scolFormId = 0x174D17;
        var scol = new StaticCollectionRecord
        {
            FormId = scolFormId,
            EditorId = "SSHQExterior03",
            ModelPath = "SCOL\\SSHQExterior03.NIF"
        };
        var cells = new List<CellRecord>
        {
            new()
            {
                FormId = 0x100,
                GridX = 0,
                GridY = 0,
                PlacedObjects =
                [
                    new PlacedReference
                    {
                        FormId = 0x200, BaseFormId = scolFormId, RecordType = "REFR", X = 0, Y = 0, Z = 0
                    }
                ]
            }
        };

        ObjectIndexBuilder.BuildAndEnrich(
            [], [], [], [], [],
            [scol],
            [], [], [], [], [], [],
            [], [], [], [], [], [],
            cells, [], new Dictionary<uint, string>(),
            new Stopwatch());

        Assert.Equal("SCOL\\SSHQExterior03.NIF", cells[0].PlacedObjects[0].ModelPath);
    }
}
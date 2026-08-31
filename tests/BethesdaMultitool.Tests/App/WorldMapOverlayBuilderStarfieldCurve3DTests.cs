using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.SaveGame.Models;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class WorldMapOverlayBuilderStarfieldCurve3DTests
{
    [Fact]
    public void BuildFromRecords_IndexesLastWinsMergedCur3SourceRecords()
    {
        var retained = Curve(0x10, "Retained");
        var replaced = Curve(0x11, "Base");
        var replacement = Curve(0x11, "Override");
        var added = Curve(0x12, "Added");
        var merged = new RecordCollection
        {
            Curves3D = [retained, replaced]
        }.MergeWith(new RecordCollection
        {
            Curves3D = [replacement, added]
        });

        var world = WorldMapOverlayBuilder.BuildFromRecords(merged, null);

        Assert.Equal(3, world.Curves3DByFormId.Count);
        Assert.Same(retained, world.Curves3DByFormId[retained.FormId]);
        Assert.Same(replacement, world.Curves3DByFormId[replacement.FormId]);
        Assert.Same(added, world.Curves3DByFormId[added.FormId]);
    }

    [Fact]
    public void BuildFromSave_IndexesSupplementaryCur3SourceRecords()
    {
        var curve = Curve(0x20, "Supplementary");
        var supplementary = new RecordCollection { Curves3D = [curve] };
        var save = new SaveFile
        {
            Header = new SaveFileHeader(),
            Statistics = new SaveStatistics(),
            LocationTable = new FileLocationTable()
        };

        var world = WorldMapOverlayBuilder.BuildFromSave(
            save,
            supplementary,
            FormIdResolver.Empty,
            null);

        Assert.Same(curve, world.Curves3DByFormId[curve.FormId]);
    }

    private static StarfieldCurve3DRecord Curve(uint formId, string editorId) =>
        new()
        {
            FormId = formId,
            EditorId = editorId
        };
}

using BethesdaMultitool.Core.Formats.Esm.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Models;

public sealed class RecordCollectionRuntimeScriptMergeTests
{
    [Fact]
    public void MergeWith_MetadataFreeBase_RetainsOnlyOverlayRuntimeScripts()
    {
        var runtime = new RuntimeScriptData { FormId = 0x01001234, DumpOffset = 0x20 };
        var overlay = new RecordCollection { RuntimeScripts = [runtime] };

        var merged = new RecordCollection().MergeWith(overlay);

        Assert.Equal([runtime], merged.RuntimeScripts);
        Assert.NotSame(overlay.RuntimeScripts, merged.RuntimeScripts);
    }

    [Fact]
    public void MergeWith_RuntimeMetadataOnBothSides_ClearsPotentialCrossDumpSources()
    {
        var baseCollection = new RecordCollection
        {
            RuntimeScripts = [new RuntimeScriptData { FormId = 0x01000001, DumpOffset = 0x10 }]
        };
        var overlay = new RecordCollection
        {
            RuntimeScripts = [new RuntimeScriptData { FormId = 0x01000002, DumpOffset = 0x20 }]
        };

        var merged = baseCollection.MergeWith(overlay);

        Assert.Empty(merged.RuntimeScripts);
    }

    [Fact]
    public void MergeWith_RuntimeMetadataOnlyOnBase_DoesNotMisattributeItToOverlay()
    {
        var baseCollection = new RecordCollection
        {
            RuntimeScripts = [new RuntimeScriptData { FormId = 0x01000001, DumpOffset = 0x10 }]
        };

        var merged = baseCollection.MergeWith(new RecordCollection());

        Assert.Empty(merged.RuntimeScripts);
    }
}
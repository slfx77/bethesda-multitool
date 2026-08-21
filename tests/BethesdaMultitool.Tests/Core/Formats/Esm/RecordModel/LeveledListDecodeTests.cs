using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     Guards that leveled-list entries (LVLO) decode structurally instead of passing through raw. The schema
///     generator used to emit a RawMemberDef("wbLeveledListEntry") placeholder, so LVLI/LVLC/LVSP — whose
///     entries are the entire point of the record — came back as opaque bytes. Expanding the helper into the
///     12-byte struct (Level u16 / unused / Reference FormID / Count u16 / tail) fixed it. Env-gated.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class LeveledListDecodeTests
{
    [Theory]
    [InlineData("Oblivion", @"Data\Oblivion.esm")]
    public async Task LeveledLists_DecodeEntriesStructurally(string gameFolder, string relativePath)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esm = RealAssetPaths.SteamGameFile(gameFolder, relativePath);
        Assert.SkipWhen(esm is null, RealAssetPaths.SkipMessage(Path.GetFileName(relativePath)));

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, TestContext.Current.CancellationToken);

        var leveled = result.Records.GenericRecords
            .Where(r => r.RecordType is "LVLI" or "LVLC" or "LVSP")
            .ToList();
        Assert.NotEmpty(leveled);

        var lvloNodes = leveled
            .Where(r => r.DecodedTree is { Count: > 0 })
            .SelectMany(r => Flatten(r.DecodedTree!))
            .Where(n => n.Signature == "LVLO")
            .ToList();

        Assert.NotEmpty(lvloNodes);

        // Every LVLO entry must be structurally decoded (children), never a raw byte blob.
        Assert.DoesNotContain(lvloNodes, n => n.IsRaw);
        Assert.All(lvloNodes, n => Assert.NotEmpty(n.Children));

        // A representative entry carries the fields the record exists for: a level, a reference, a count.
        var sample = lvloNodes.First(n => n.Children.Any(c => c.FormId is > 0));
        Assert.Contains(sample.Children, c => c.Label == "Level");
        Assert.Contains(sample.Children, c => c.Label == "Count");
        Assert.Contains(sample.Children, c => c.FormId is > 0);
    }

    private static IEnumerable<DecodedNode> Flatten(IReadOnlyList<DecodedNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children))
            {
                yield return c;
            }
        }
    }
}
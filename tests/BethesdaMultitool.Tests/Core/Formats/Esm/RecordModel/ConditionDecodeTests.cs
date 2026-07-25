using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     Guards that CTDA conditions decode structurally. They used to come back raw twice over: the generator
///     emitted phantom "WbNum" members (it grabbed the wbStructSK sort-key list instead of the
///     wbConditionMembers symbol), and the decoder halted on the CompValue/parameter UnionDefs. With
///     member-array-symbol resolution + union support, a condition exposes Type / Comparison Value / Function
///     / Parameter #1 / #2. Env-gated.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class ConditionDecodeTests
{
    [Theory]
    [InlineData(@"E:\SteamLibrary\SteamApps\common\Oblivion\Data\Oblivion.esm")]
    public async Task Conditions_DecodeStructurally(string esm)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(File.Exists(esm), $"Not found: {esm}");

        var result = await RealAssetEsmCache.LoadAsync(
            esm, TestContext.Current.CancellationToken);

        // A condition node has the signature CTDA and the structural members; collect them across every
        // record type that carries conditions (INFO, QUST, ...).
        var conditions = result.Records.GenericRecords
            .Where(r => r.DecodedTree is { Count: > 0 })
            .SelectMany(r => Flatten(r.DecodedTree!))
            .Where(n => n.Signature == "CTDA")
            .ToList();

        Assert.NotEmpty(conditions);
        Assert.DoesNotContain(conditions, c => c.IsRaw);

        // A representative condition must expose the decoded fields, not an opaque byte blob.
        var sample = conditions.First(c => c.Children.Count > 0);
        var labels = sample.Children.Select(c => c.Label).ToList();
        Assert.Contains("Type", labels);
        Assert.Contains("Comparison Value", labels);
        Assert.Contains("Function", labels);
        Assert.Contains(sample.Children, c => c.Label == "Function" && !string.IsNullOrEmpty(c.Value));
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
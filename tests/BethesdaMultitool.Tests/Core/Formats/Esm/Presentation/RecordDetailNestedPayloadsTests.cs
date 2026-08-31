using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Presentation;

/// <summary>
///     The nested payloads are presented from one place that feeds both the CLI <c>show</c>
///     renderers and the GUI record browser, so how an incomplete capture reads is decided here
///     rather than per consumer.
/// </summary>
public sealed class RecordDetailNestedPayloadsTests
{
    private const uint SubjectFormId = 0x000A473A;

    [Fact]
    public void AlternateTextureWithNoResolvedTextureSet_SaysSoInsteadOfLinkingToFormIdZero()
    {
        // The browse path deliberately keeps an entry whose TXST pointer did not resolve — the
        // shape name and 3D index are still real captured data. But TextureSetFormId is then 0,
        // and rendering that as a FormID makes a failed read indistinguishable from a genuine
        // reference to the null form. In the GUI it was worse: a navigable link to nothing.
        var model = Append([new AlternateTextureEntry("Barrel", 0u, 2)]);

        var item = Assert.Single(Section(model, "Alternate Textures").Entries[0].Items!);

        Assert.Equal("Barrel", item.Label);
        Assert.Contains("unresolved", item.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("0x00000000", item.Value, StringComparison.Ordinal);
        Assert.Null(item.LinkedFormId);
    }

    [Fact]
    public void AlternateTextureThatResolved_KeepsItsNavigableLink()
    {
        var model = Append([new AlternateTextureEntry("Body", 0x000A4733, 0)]);

        var item = Assert.Single(Section(model, "Alternate Textures").Entries[0].Items!);

        Assert.Equal(0x000A4733u, item.LinkedFormId);
        Assert.Contains("1stPersonCowboyRepeaterTexture", item.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("unresolved", item.Value, StringComparison.Ordinal);
    }

    private static RecordDetailModel Append(List<AlternateTextureEntry> swaps)
    {
        var records = new RecordCollection
        {
            AlternateTexturesByFormId = new Dictionary<uint, IReadOnlyList<AlternateTextureEntry>>
            {
                [SubjectFormId] = swaps
            }
        };

        var resolver = new FormIdResolver(
            new Dictionary<uint, string> { [0x000A4733] = "1stPersonCowboyRepeaterTexture" },
            []);

        return RecordDetailNestedPayloads.Append(
            new RecordDetailModel { RecordSignature = "STAT", FormId = SubjectFormId },
            records,
            resolver);
    }

    private static RecordDetailSection Section(RecordDetailModel model, string title)
    {
        return Assert.Single(model.Sections, section => section.Title == title);
    }
}

using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

[Collection(SequentialIntegrationGroup.Name)]
public sealed class RuntimeMagicDumpRegressionTests
{
    [Fact]
    public async Task RuntimePerkEntries_ReadRankFromInlineListItem()
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(DmpSnippetReader.DefaultSnippetDir, "xex44_dump");
        var reader = snippet.CreateStructReader();
        var entry = Assert.Single(snippet.RuntimeEditorIds, e =>
            e.FormType == 0x56 &&
            string.Equals(e.EditorId, "IntenseTraining", StringComparison.OrdinalIgnoreCase));

        var perk = reader.ReadRuntimePerk(entry);
        Assert.NotNull(perk);

        Assert.NotEmpty(perk.Entries);
        Assert.Equal((byte)0, perk.Entries[0].Rank);
        Assert.DoesNotContain(perk.Entries, e => e.Rank == 130);
    }
}

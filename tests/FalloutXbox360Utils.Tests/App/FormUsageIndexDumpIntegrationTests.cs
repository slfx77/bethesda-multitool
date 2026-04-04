using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.App;

public class FormUsageIndexDumpIntegrationTests
{
    private static readonly string SnippetDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "Dmp");

    [Fact]
    public async Task RuntimeReader_OnReleaseDump_ReadsDialogueConditionsFromStructs()
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, "release_dump");

        var runtimeEntries = snippet.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x46 && entry.TesFormOffset.HasValue)
            .ToList();
        Assert.NotEmpty(runtimeEntries);

        var npcEntries = snippet.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x2A)
            .ToList();

        var runtimeReader = RuntimeStructReader.CreateWithAutoDetect(
            snippet.Accessor,
            snippet.FileSize,
            snippet.MinidumpInfo,
            snippet.RuntimeRefrFormEntries,
            npcEntries);

        var runtimeInfos = runtimeEntries
            .Select(runtimeReader.ReadRuntimeDialogueInfo)
            .Where(info => info is not null)
            .Select(info => info!)
            .ToList();

        Assert.NotEmpty(runtimeInfos);
        Assert.Contains(runtimeInfos, info => info.Conditions.Count > 0);
        Assert.Contains(runtimeInfos, info => info.ConditionFunctions.Count > 0);
    }
}
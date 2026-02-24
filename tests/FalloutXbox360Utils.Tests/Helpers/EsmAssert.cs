using FalloutXbox360Utils.Core.Formats.Esm;
using Xunit;

namespace FalloutXbox360Utils.Tests.Helpers;

/// <summary>
///     Custom assertion helpers for ESM-related test data.
/// </summary>
internal static class EsmAssert
{
    /// <summary>
    ///     Assert that all collection properties of an EsmRecordScanResult are empty.
    ///     Prevents copy-paste drift when new properties are added.
    /// </summary>
    public static void AllCollectionsEmpty(EsmRecordScanResult result)
    {
        Assert.Empty(result.EditorIds);
        Assert.Empty(result.FullNames);
        Assert.Empty(result.Descriptions);
        Assert.Empty(result.GameSettings);
        Assert.Empty(result.ScriptSources);
        Assert.Empty(result.FormIdReferences);
        Assert.Empty(result.MainRecords);
        Assert.Empty(result.NameReferences);
        Assert.Empty(result.Positions);
        Assert.Empty(result.ActorBases);
        Assert.Empty(result.ResponseTexts);
        Assert.Empty(result.ResponseData);
        Assert.Empty(result.ModelPaths);
        Assert.Empty(result.IconPaths);
        Assert.Empty(result.TexturePaths);
        Assert.Empty(result.ScriptRefs);
        Assert.Empty(result.EffectRefs);
        Assert.Empty(result.SoundRefs);
        Assert.Empty(result.QuestRefs);
        Assert.Empty(result.Conditions);
        Assert.Empty(result.Heightmaps);
        Assert.Empty(result.CellGrids);
        Assert.Empty(result.AssetStrings);
        Assert.Empty(result.RuntimeEditorIds);
    }
}

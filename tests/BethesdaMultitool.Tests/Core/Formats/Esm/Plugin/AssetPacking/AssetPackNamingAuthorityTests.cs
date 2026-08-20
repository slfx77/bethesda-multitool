using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Structural guard: exactly one place decides what a packed asset ends up being called.
///     <para>
///         The 2026-08-13 defect was not a wrong rule — it was two rules. The packer derived the
///         BSA entry name in <c>AssetPackingService</c> and the rename pass derived the record's
///         path in <c>AssetPathRewriter</c>, independently, and they disagreed: 49 SOUN records
///         per build named <c>.xma</c> files the archive never contained. Both now call
///         <c>PrototypeAssetConverter.PredictPackedPath</c>, and a direct
///         <c>Path.ChangeExtension</c> at either site is how that drift would come back.
///     </para>
/// </summary>
public sealed class AssetPackNamingAuthorityTests
{
    [Theory]
    [InlineData("AssetPackingService.cs")]
    [InlineData("AssetPathRewriter.cs")]
    public void NamingSites_DeriveTheExtension_FromTheSharedAuthority(string fileName)
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Esm", "Plugin", "AssetPacking",
            fileName);

        Assert.DoesNotContain("Path.ChangeExtension", source, StringComparison.Ordinal);
        Assert.Contains("PredictPackedPath", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionBranches_DeriveTheirOutputExtension_FromTheSharedAuthority()
    {
        // Every ConvertAsync branch must compute its output path through
        // ExtensionAfterConversion, so the predictor cannot fall out of step with what
        // conversion actually emits. Literal target extensions there are the smell.
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Esm", "Plugin", "AssetPacking",
            "PrototypeAssetConverter.cs");

        foreach (var literal in new[]
                 {
                     "ChangeExtension(sourcePath, \".dds\")",
                     "ChangeExtension(sourcePath, \".wav\")",
                     "ChangeExtension(sourcePath, \".ogg\")"
                 })
        {
            Assert.DoesNotContain(literal, source, StringComparison.Ordinal);
        }
    }
}
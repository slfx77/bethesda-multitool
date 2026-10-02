using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Pins the per-call-site flip table for this round: no production call site has been admitted to the normalized
///     writer, and the WebView compatibility preview can never be moved off the native writer.
/// </summary>
public sealed class NifGlbExportDefaultsTests
{
    /// <summary>Every defined call site, by name.</summary>
    public static TheoryData<string> CallSites()
    {
        var data = new TheoryData<string>();
        foreach (var callSite in Enum.GetNames<NifGlbExportCallSite>())
        {
            data.Add(callSite);
        }

        return data;
    }

    /// <summary>A new call site must be added to the table and to this list deliberately.</summary>
    [Fact]
    public void TheCallSiteSetIsExactlyTheFourPlannedEntryPoints()
    {
        Assert.Equal(
            new[] { "CliExportNif", "GuiNifViewerExport", "CliExportSpt", "ViewerCompatibilityPreview" },
            Enum.GetNames<NifGlbExportCallSite>());
    }

    /// <summary>No call site is admitted in this round, so every row is Native.</summary>
    [Theory]
    [MemberData(nameof(CallSites))]
    public void EveryCallSiteDefaultsToTheNativeWriter(string callSiteName)
    {
        var callSite = Enum.Parse<NifGlbExportCallSite>(callSiteName);

        Assert.Equal(NifGlbWriterPreference.Native, NifGlbExportDefaults.For(callSite));
    }

    /// <summary>Only the WebView compatibility preview is pinned to the native writer.</summary>
    [Theory]
    [MemberData(nameof(CallSites))]
    public void OnlyTheCompatibilityPreviewIsPinned(string callSiteName)
    {
        var callSite = Enum.Parse<NifGlbExportCallSite>(callSiteName);

        Assert.Equal(callSiteName == "ViewerCompatibilityPreview", NifGlbExportDefaults.IsPinnedNative(callSite));
    }

    /// <summary>
    ///     A pinned call site's row is Native. Unlike the all-Native test above, this one must keep passing after
    ///     other call sites are flipped, because nothing else stops the compatibility preview from following them.
    /// </summary>
    [Theory]
    [MemberData(nameof(CallSites))]
    public void EveryPinnedRowIsNative(string callSiteName)
    {
        var callSite = Enum.Parse<NifGlbExportCallSite>(callSiteName);

        if (NifGlbExportDefaults.IsPinnedNative(callSite))
        {
            Assert.Equal(NifGlbWriterPreference.Native, NifGlbExportDefaults.For(callSite));
        }
    }

    /// <summary>An undefined call site is an argument error in both table lookups.</summary>
    [Fact]
    public void AnUndefinedCallSiteIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NifGlbExportDefaults.For((NifGlbExportCallSite)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => NifGlbExportDefaults.IsPinnedNative((NifGlbExportCallSite)9));
    }
}

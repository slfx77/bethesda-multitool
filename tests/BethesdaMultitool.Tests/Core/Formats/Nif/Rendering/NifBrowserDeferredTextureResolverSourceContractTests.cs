using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class NifBrowserDeferredTextureResolverSourceContractTests
{
    private static string BrowserSource() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering",
        "NifBrowserService.cs");

    [Fact]
    public void SourceSelectionStoresTexturePathsWithoutOpeningResolver()
    {
        var source = BrowserSource();
        var fieldsAndConstructor = SourceContract.Extract(
            source,
            "internal sealed class NifBrowserService",
            "public bool IsBsaMode");
        var directoryFactory = SourceContract.Extract(
            source,
            "internal static NifBrowserService CreateFromDirectory(",
            "internal static NifBrowserService CreateFromBsa(");
        var archiveFactory = SourceContract.Extract(
            source,
            "internal static NifBrowserService CreateFromBsa(",
            "internal List<NifTreeEntry> ListNifFiles(");

        Assert.Contains(
            "SynchronizedLazyDisposable<NifTextureResolver>",
            fieldsAndConstructor,
            StringComparison.Ordinal);
        Assert.Contains(
            "new SynchronizedLazyDisposable<NifTextureResolver>(() =>",
            fieldsAndConstructor,
            StringComparison.Ordinal);
        Assert.DoesNotContain("new NifTextureResolver", directoryFactory, StringComparison.Ordinal);
        Assert.DoesNotContain("new NifTextureResolver", archiveFactory, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelRenderAndExportBorrowOwnedResolverThroughSynchronizedGate()
    {
        var source = BrowserSource();
        var disposal = SourceContract.Extract(
            source,
            "public void Dispose()",
            "internal static NifBrowserService CreateFromDirectory(");

        Assert.Equal(3, CountOccurrences(source, "_textureResolver.Use("));
        Assert.Contains("_textureResolver.Dispose();", source, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            disposal,
            "_textureResolver.Dispose();",
            "_siblingMeshArchives?.Dispose();",
            "_archiveLease?.Dispose();");
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}

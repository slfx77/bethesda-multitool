using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class NifConverterTextureArchiveStateSourceContractTests
{
    [Fact]
    public void ManualOverrideAndActualTextureSourcesUseSeparateControls()
    {
        var xaml = SourceContract.ReadAppSource("NifConverterTab.xaml");
        var code = SourceContract.ReadAppSource("NifConverterTab.xaml.cs");

        Assert.Contains("x:Name=\"NifViewerTextureOverrideTextBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NifViewerTextureSourcesText\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.Name=\"Active texture sources\"", xaml,
            StringComparison.Ordinal); // Preserve the dynamic Text as the accessible name.

        Assert.Contains("var overrideText = NifViewerTextureOverrideTextBox.Text;", code,
            StringComparison.Ordinal);
        Assert.Contains("NifViewerTextureSourcesText.Text =", code, StringComparison.Ordinal);
        Assert.Contains("$\"Texture sources: {state.TexturePathsDisplay}\"", code, StringComparison.Ordinal);
        Assert.Equal(1, SourceContract.CountOccurrences(code, "NifViewerTextureOverrideTextBox.Text ="));
        Assert.Contains("NifViewerTextureOverrideTextBox.Text = file.Path;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("var hasOverride =", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceAndTextureArchivePickersBothAdmitBsaAndBa2()
    {
        var code = SourceContract.ReadAppSource("NifConverterTab.xaml.cs");

        Assert.Equal(2, SourceContract.CountOccurrences(code, "FileTypeFilter.Add(\".bsa\")"));
        Assert.Equal(2, SourceContract.CountOccurrences(code, "FileTypeFilter.Add(\".ba2\")"));
    }

    [Fact]
    public void WorkflowParsesPathListsAndViewModelAlwaysReturnsActualSources()
    {
        var workflow = SourceContract.ReadAppSource("NifConverterWorkflowService.cs");
        var viewModel = SourceContract.ReadAppSource("NifConverterViewModel.cs");

        Assert.Contains("NifTextureSourcePathText.ParseOverride(texturePathOverride)", workflow,
            StringComparison.Ordinal);
        Assert.Contains("NifTextureSourcePathText.Format(service.TexturePaths)", workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("new[] { texturePathOverride.Trim() }", workflow, StringComparison.Ordinal);

        Assert.Contains("result.TexturePathsDisplay", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("keepTextureOverride", viewModel, StringComparison.Ordinal);
    }
}
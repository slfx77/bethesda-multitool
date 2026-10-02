using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class WorldSettingsPanelPolishSourceContractTests
{
    [Fact]
    public void RestoredExpanderHeadersPairGlyphsWithAccessibleHeadings()
    {
        var world3D = SourceContract.ReadAppSource("WorldView3DSettingsPanel.xaml");
        AssertHeader(world3D, "&#xE706;", "Lighting");
        AssertHeader(world3D, "&#xE7F4;", "Video");
        AssertHeader(world3D, "&#xE81E;", "Overlays");
        AssertHeader(world3D, "&#xE890;", "Visibility");
        AssertHeader(world3D, "&#xE809;", "Camera");

        var world2D = SourceContract.ReadAppSource("WorldMapSettingsPanel.xaml");
        AssertHeader(world2D, "&#xE706;", "Lighting");
        AssertHeader(world2D, "&#xE81E;", "Layers");
        AssertHeader(world2D, "&#xE890;", "Visibility");
    }

    [Fact]
    public void CollapsedExpanderContentIsMeasuredOnceBeforeFirstInteraction()
    {
        var world3D = SourceContract.ReadAppSource("WorldView3DSettingsPanel.xaml.cs");
        AssertWarmup(world3D,
            "PremeasureCollapsedContent(availableWidth, VideoExpander, OverlaysExpander, VisibilityExpander, CameraExpander);");

        var world2D = SourceContract.ReadAppSource("WorldMapSettingsPanel.xaml.cs");
        AssertWarmup(world2D,
            "PremeasureCollapsedContent(availableWidth, LayersExpander, VisibilityExpander, ShadingExpander);");
    }

    [Fact]
    public void RightPanelUsesAThemeAwareLayerSurface()
    {
        var singleFileTab = SourceContract.ReadAppSource("SingleFileTab.xaml");
        var panel = SourceContract.Extract(singleFileTab,
            "<Border x:Name=\"WorldRightPanelContent\"",
            "<Grid.RowDefinitions>");

        // The theme-aware surface moved into the shared AWT-derived card style, which sets a
        // CardBackgroundBrush background, a CardBorderBrush border and CornerRadius 8. Pinning the style
        // covers all three; repeating them here would only assert the style's own contents.
        Assert.Contains("Style=\"{StaticResource MultitoolCardBorderStyle}\"", panel,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Background=\"#", panel, StringComparison.Ordinal);
    }

    private static void AssertHeader(string xaml, string glyph, string heading)
    {
        SourceContract.AssertOrder(xaml,
            $"<FontIcon Glyph=\"{glyph}\" FontSize=\"14\" />",
            $"<TextBlock Text=\"{heading}\" FontWeight=\"SemiBold\"",
            "AutomationProperties.HeadingLevel=\"Level2\"");
    }

    private static void AssertWarmup(string source, string expectedExpanders)
    {
        SourceContract.AssertOrder(source,
            "Loaded += SettingsPanel_Loaded;",
            "Loaded -= SettingsPanel_Loaded;",
            expectedExpanders,
            "if (!expander.IsExpanded && expander.Content is UIElement content)",
            "content.Measure(new Size(availableWidth, double.PositiveInfinity));");
        Assert.DoesNotContain("expander.IsExpanded =", source, StringComparison.Ordinal);
    }
}
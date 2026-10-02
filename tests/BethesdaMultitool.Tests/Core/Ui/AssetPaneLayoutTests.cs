using BethesdaMultitool.Core.Ui;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Ui;

/// <summary>Pins the persisted three-column layout of the asset browser: defaults, minima, clamping and the settings round trip.</summary>
public sealed class AssetPaneLayoutTests
{
    /// <summary>First launch shows a 280 DIP tree and a 420 DIP preview, both expanded, over the documented minima.</summary>
    [Fact]
    public void Default_IsTheDocumentedLayout()
    {
        Assert.Equal(new AssetPaneLayout(280, 420, false, false), AssetPaneLayout.Default);
        Assert.Equal(280, AssetPaneLayout.Default.TreeWidth);
        Assert.Equal(420, AssetPaneLayout.Default.PreviewWidth);
        Assert.False(AssetPaneLayout.Default.TreeCollapsed);
        Assert.False(AssetPaneLayout.Default.PreviewCollapsed);
        Assert.Equal(180, AssetPaneLayout.TreeMinWidth);
        Assert.Equal(240, AssetPaneLayout.GalleryMinWidth);
        Assert.Equal(320, AssetPaneLayout.PreviewMinWidth);
        Assert.Equal(36, AssetPaneLayout.StripWidth);
        Assert.Equal(4096, AssetPaneLayout.MaxWidth);
        Assert.Equal("Browser.Assets.TreeWidth", AssetPaneLayout.TreeWidthKey);
        Assert.Equal("Browser.Assets.PreviewWidth", AssetPaneLayout.PreviewWidthKey);
        Assert.Equal("Browser.Assets.TreeCollapsed", AssetPaneLayout.TreeCollapsedKey);
        Assert.Equal("Browser.Assets.PreviewCollapsed", AssetPaneLayout.PreviewCollapsedKey);
    }

    /// <summary>Stored values override their defaults individually; the two that are absent keep theirs.</summary>
    [Fact]
    public void Parse_ReadsStoredValuesAndKeepsDefaultsForTheRest()
    {
        var layout = AssetPaneLayout.Parse(new Dictionary<string, string>
        {
            ["Browser.Assets.TreeWidth"] = "312.5",
            ["Browser.Assets.PreviewCollapsed"] = "true"
        });

        Assert.Equal(new AssetPaneLayout(312.5, 420, false, true), layout);
    }

    /// <summary>A stored width below its minimum or above the ceiling is clamped, not ignored.</summary>
    [Theory]
    [InlineData("50", 180)]
    [InlineData("179.9", 180)]
    [InlineData("180", 180)]
    [InlineData("99999", 4096)]
    [InlineData("1e9", 4096)]
    public void Parse_ClampsNumericWidths(string stored, double expected)
    {
        var layout = AssetPaneLayout.Parse(new Dictionary<string, string> { ["Browser.Assets.TreeWidth"] = stored });

        Assert.Equal(expected, layout.TreeWidth);
        Assert.Equal(420, layout.PreviewWidth);
    }

    /// <summary>Text that is not an invariant positive number falls back to the default rather than throwing or clamping.</summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("1,5")]
    [InlineData("")]
    public void Parse_IgnoresGarbageWidths(string stored)
    {
        var layout = AssetPaneLayout.Parse(new Dictionary<string, string>
        {
            ["Browser.Assets.TreeWidth"] = stored,
            ["Browser.Assets.PreviewWidth"] = stored
        });

        Assert.Equal(AssetPaneLayout.Default, layout);
    }

    /// <summary>Unknown keys and unparseable flags are ignored; flags are read case-insensitively.</summary>
    [Fact]
    public void Parse_IgnoresUnknownKeysAndGarbageFlags()
    {
        var layout = AssetPaneLayout.Parse(new Dictionary<string, string>
        {
            ["Browser.Assets.Unknown"] = "500",
            ["Browser.Images.Zoom"] = "2",
            ["Browser.Assets.TreeCollapsed"] = "yes",
            ["Browser.Assets.PreviewCollapsed"] = "TRUE"
        });

        Assert.Equal(new AssetPaneLayout(280, 420, false, true), layout);
        Assert.Equal(AssetPaneLayout.Default, AssetPaneLayout.Parse(new Dictionary<string, string>()));
    }

    /// <summary>What is written is read back identically, in invariant text.</summary>
    [Fact]
    public void ToSettings_RoundTripsThroughParse()
    {
        var layout = new AssetPaneLayout(312.5, 640, true, false);

        var settings = layout.ToSettings();

        Assert.Equal("312.5", settings["Browser.Assets.TreeWidth"]);
        Assert.Equal("640", settings["Browser.Assets.PreviewWidth"]);
        Assert.Equal("true", settings["Browser.Assets.TreeCollapsed"]);
        Assert.Equal("false", settings["Browser.Assets.PreviewCollapsed"]);
        Assert.Equal(4, settings.Count);
        Assert.Equal(layout, AssetPaneLayout.Parse(settings));
        Assert.Equal(AssetPaneLayout.Default, AssetPaneLayout.Parse(AssetPaneLayout.Default.ToSettings()));
    }

    /// <summary>A dragged width is clamped into range; a non-finite one is a programming error.</summary>
    [Fact]
    public void WithWidths_ClampAndRejectNonFinite()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AssetPaneLayout.Default.WithTreeWidth(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => AssetPaneLayout.Default.WithTreeWidth(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => AssetPaneLayout.Default.WithPreviewWidth(double.NegativeInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AssetPaneLayout(double.NaN, 420, false, false));

        Assert.Equal(180, AssetPaneLayout.Default.WithTreeWidth(100).TreeWidth);
        Assert.Equal(4096, AssetPaneLayout.Default.WithTreeWidth(10_000).TreeWidth);
        Assert.Equal(300.25, AssetPaneLayout.Default.WithTreeWidth(300.25).TreeWidth);
        Assert.Equal(320, AssetPaneLayout.Default.WithPreviewWidth(-5).PreviewWidth);
        Assert.Equal(500, AssetPaneLayout.Default.WithPreviewWidth(500).PreviewWidth);
        Assert.Equal(180, new AssetPaneLayout(1, 1, false, false).TreeWidth);
        Assert.Equal(320, new AssetPaneLayout(1, 1, false, false).PreviewWidth);

        var moved = AssetPaneLayout.Default.WithTreeWidth(300).WithPreviewWidth(500);
        Assert.Equal(new AssetPaneLayout(300, 500, false, false), moved);
        Assert.Equal(AssetPaneLayout.Default, AssetPaneLayout.Default.WithTreeWidth(280));
    }

    /// <summary>Collapsing one pane leaves the other pane and both widths alone.</summary>
    [Fact]
    public void WithCollapsed_TogglesOnePaneAtATime()
    {
        var treeCollapsed = AssetPaneLayout.Default.WithTreeCollapsed(true);
        Assert.Equal(new AssetPaneLayout(280, 420, true, false), treeCollapsed);

        var both = treeCollapsed.WithPreviewCollapsed(true);
        Assert.Equal(new AssetPaneLayout(280, 420, true, true), both);

        Assert.Equal(AssetPaneLayout.Default, both.WithTreeCollapsed(false).WithPreviewCollapsed(false));
        Assert.Equal(312.5, new AssetPaneLayout(312.5, 420, false, false).WithTreeCollapsed(true).TreeWidth);
    }
}

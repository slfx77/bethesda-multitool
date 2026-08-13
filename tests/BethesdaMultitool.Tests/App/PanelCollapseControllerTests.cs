using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     Pins the collapse/expand contract of <see cref="PanelCollapseState" />, the WinUI-free half of
///     <c>PanelCollapseController</c> (the side panels flanking the world and actor viewers). The
///     controller itself is a thin WinUI adapter over this state, so the width math is pinned here
///     rather than against a GUI.
/// </summary>
public sealed class PanelCollapseControllerTests
{
    [Fact]
    public void StartsExpanded()
    {
        var state = new PanelCollapseState();

        Assert.False(state.IsCollapsed);
        Assert.Equal(PanelCollapseState.DefaultStripWidth, state.StripWidth);
    }

    [Fact]
    public void Collapse_ShrinksColumnToStripWidth()
    {
        var state = new PanelCollapseState();

        var collapsed = state.Collapse(new PanelColumnWidth(520, PanelColumnUnit.Pixel, 380));

        Assert.True(state.IsCollapsed);
        Assert.Equal(new PanelColumnWidth(36, PanelColumnUnit.Pixel, 36), collapsed);
    }

    [Fact]
    public void Collapse_MinWidthNeverDropsToZero()
    {
        // A 0 minimum would let a surrounding layout squeeze the strip — and with it the only
        // expand affordance — out of existence, stranding the panel closed.
        var state = new PanelCollapseState(stripWidth: 24);

        var collapsed = state.Collapse(new PanelColumnWidth(300, PanelColumnUnit.Pixel, 0));

        Assert.Equal(24, collapsed!.Value.MinWidth);
        Assert.Equal(24, collapsed.Value.Value);
    }

    [Fact]
    public void Expand_RestoresTheWidthCapturedOnCollapse()
    {
        var state = new PanelCollapseState();
        var authored = new PanelColumnWidth(412.5, PanelColumnUnit.Pixel, 280);

        state.Collapse(authored);
        var restored = state.Expand();

        Assert.False(state.IsCollapsed);
        Assert.Equal(authored, restored);
    }

    [Fact]
    public void Expand_PreservesStarAndAutoUnits()
    {
        var star = new PanelCollapseState();
        star.Collapse(new PanelColumnWidth(3, PanelColumnUnit.Star, 400));
        Assert.Equal(new PanelColumnWidth(3, PanelColumnUnit.Star, 400), star.Expand());

        var auto = new PanelCollapseState();
        auto.Collapse(new PanelColumnWidth(0, PanelColumnUnit.Auto, 0));
        Assert.Equal(new PanelColumnWidth(0, PanelColumnUnit.Auto, 0), auto.Expand());
    }

    [Fact]
    public void RepeatedCollapse_DoesNotOverwriteTheRememberedWidth()
    {
        // The regression this guards: a second collapse capturing the STRIP width as the
        // "expanded" width, after which expand restores a 36px panel forever.
        var state = new PanelCollapseState();
        var authored = new PanelColumnWidth(520, PanelColumnUnit.Pixel, 380);

        state.Collapse(authored);
        Assert.Null(state.Collapse(new PanelColumnWidth(36, PanelColumnUnit.Pixel, 36)));
        Assert.True(state.IsCollapsed);

        Assert.Equal(authored, state.Expand());
    }

    [Fact]
    public void RepeatedExpand_IsANoOp()
    {
        var state = new PanelCollapseState();

        Assert.Null(state.Expand());
        state.Collapse(new PanelColumnWidth(300, PanelColumnUnit.Pixel, 280));
        Assert.NotNull(state.Expand());
        Assert.Null(state.Expand());
        Assert.False(state.IsCollapsed);
    }

    [Fact]
    public void RoundTrip_SurvivesAUserResizeBetweenCycles()
    {
        var state = new PanelCollapseState();

        state.Collapse(new PanelColumnWidth(520, PanelColumnUnit.Pixel, 380));
        state.Expand();
        // User drags the splitter after reopening; the next collapse must capture the NEW width.
        state.Collapse(new PanelColumnWidth(640, PanelColumnUnit.Pixel, 380));

        Assert.Equal(new PanelColumnWidth(640, PanelColumnUnit.Pixel, 380), state.Expand());
    }

    [Fact]
    public void SetCollapsed_MatchesTheExplicitTransitions()
    {
        var state = new PanelCollapseState();
        var authored = new PanelColumnWidth(280, PanelColumnUnit.Pixel, 260);

        Assert.Equal(new PanelColumnWidth(36, PanelColumnUnit.Pixel, 36),
            state.SetCollapsed(true, authored));
        // Already collapsed: no transition, and the current geometry is ignored.
        Assert.Null(state.SetCollapsed(true, new PanelColumnWidth(36, PanelColumnUnit.Pixel, 36)));
        Assert.Equal(authored, state.SetCollapsed(false, authored));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-8d)]
    [InlineData(double.NaN)]
    public void RejectsNonPositiveStripWidth(double stripWidth) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PanelCollapseState(stripWidth));
}

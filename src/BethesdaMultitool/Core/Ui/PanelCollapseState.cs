namespace BethesdaMultitool;

/// <summary>Grid-length unit of a collapsible side panel's column, mirrored without a WinUI dependency.</summary>
internal enum PanelColumnUnit
{
    Pixel,
    Star,
    Auto
}

/// <summary>
///     The geometry a collapsible side-panel column carries: its width (with unit) and its minimum
///     width. Captured on collapse and restored verbatim on expand, so whatever width the user last
///     dragged the splitter to survives the round trip.
/// </summary>
internal readonly record struct PanelColumnWidth(double Value, PanelColumnUnit Unit, double MinWidth);

/// <summary>
///     Collapse/expand state machine for a side panel, kept free of WinUI types so the width math is
///     unit-testable without a GUI. The WinUI wrapper is <c>PanelCollapseController</c>.
///     <para>
///         Both transitions are idempotent by contract: a request that matches the current state
///         returns <c>null</c> and mutates nothing. That guard is load-bearing — a second
///         <see cref="Collapse" /> would otherwise capture the strip width as the "expanded" width
///         and the panel could never be restored to its real size.
///     </para>
/// </summary>
internal sealed class PanelCollapseState
{
    /// <summary>Width of the slim strip left behind when a panel is collapsed, in DIPs.</summary>
    public const double DefaultStripWidth = 36;

    private PanelColumnWidth _expanded;

    public PanelCollapseState(double stripWidth = DefaultStripWidth)
    {
        if (stripWidth <= 0 || double.IsNaN(stripWidth))
        {
            throw new ArgumentOutOfRangeException(nameof(stripWidth), stripWidth,
                "Strip width must be a positive number of DIPs.");
        }

        StripWidth = stripWidth;
    }

    public double StripWidth { get; }

    public bool IsCollapsed { get; private set; }

    /// <summary>
    ///     Collapses the panel, remembering <paramref name="current" /> for the later expand.
    ///     Returns the geometry the column should adopt, or <c>null</c> if already collapsed.
    /// </summary>
    public PanelColumnWidth? Collapse(PanelColumnWidth current)
    {
        if (IsCollapsed) return null;

        _expanded = current;
        IsCollapsed = true;

        // MinWidth becomes the strip width rather than 0: a column minimum of 0 lets any
        // surrounding layout squeeze the collapsed strip — and its expand button — out of
        // existence, stranding the panel closed.
        return new PanelColumnWidth(StripWidth, PanelColumnUnit.Pixel, StripWidth);
    }

    /// <summary>
    ///     Restores the geometry captured by the matching <see cref="Collapse" />. Returns
    ///     <c>null</c> if the panel is not collapsed.
    /// </summary>
    public PanelColumnWidth? Expand()
    {
        if (!IsCollapsed) return null;

        IsCollapsed = false;
        return _expanded;
    }

    /// <summary>
    ///     Drives the panel to <paramref name="collapsed" />. <paramref name="current" /> is only
    ///     read on the expanded-to-collapsed edge.
    /// </summary>
    public PanelColumnWidth? SetCollapsed(bool collapsed, PanelColumnWidth current) =>
        collapsed ? Collapse(current) : Expand();
}

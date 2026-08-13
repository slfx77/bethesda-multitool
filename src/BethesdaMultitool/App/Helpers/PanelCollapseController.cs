using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace BethesdaMultitool;

/// <summary>
///     Collapses a side-panel grid column down to a slim strip and restores it on demand, preserving
///     whatever width the user last dragged the splitter to. The panel content and the strip swap
///     visibility; the adjacent splitter hides while collapsed so its gap column stays as spacing.
///     <para>
///         Ported from the Neversoft fork so both tools share the same affordance: a chevron button
///         in the panel header collapses, a chevron on the strip expands.
///     </para>
///     <para>
///         State is in-memory only — this app has no persisted user-settings store, so a collapsed
///         panel reopens expanded on the next session.
///     </para>
/// </summary>
public sealed class PanelCollapseController
{
    private readonly ColumnDefinition _column;
    private readonly UIElement _content;
    private readonly UIElement? _splitter;
    private readonly PanelCollapseState _state;
    private readonly UIElement _strip;

    /// <summary>
    ///     Wires <paramref name="collapseButton" /> and <paramref name="expandButton" /> to the
    ///     panel. The controller is kept alive by those Click subscriptions, so callers may discard
    ///     the instance.
    /// </summary>
    public PanelCollapseController(
        ColumnDefinition column,
        UIElement? splitter,
        UIElement content,
        UIElement strip,
        ButtonBase collapseButton,
        ButtonBase expandButton,
        double stripWidth = PanelCollapseState.DefaultStripWidth)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(strip);
        ArgumentNullException.ThrowIfNull(collapseButton);
        ArgumentNullException.ThrowIfNull(expandButton);

        _column = column;
        _splitter = splitter;
        _content = content;
        _strip = strip;
        _state = new PanelCollapseState(stripWidth);

        collapseButton.Click += (_, _) => SetCollapsed(true);
        expandButton.Click += (_, _) => SetCollapsed(false);
    }

    public bool IsCollapsed => _state.IsCollapsed;

    public void SetCollapsed(bool collapsed)
    {
        var current = new PanelColumnWidth(_column.Width.Value, UnitOf(_column.Width), _column.MinWidth);
        if (_state.SetCollapsed(collapsed, current) is not { } target) return;

        _column.MinWidth = target.MinWidth;
        _column.Width = ToGridLength(target);
        if (_splitter != null)
        {
            _splitter.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        }

        _content.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _strip.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Toggle() => SetCollapsed(!_state.IsCollapsed);

    // GridUnitType is read directly rather than through IsStar/IsAuto so the mapping stays a
    // total, exhaustive switch in both directions.
    private static PanelColumnUnit UnitOf(GridLength length) => length.GridUnitType switch
    {
        GridUnitType.Star => PanelColumnUnit.Star,
        GridUnitType.Auto => PanelColumnUnit.Auto,
        _ => PanelColumnUnit.Pixel
    };

    private static GridLength ToGridLength(PanelColumnWidth width) => width.Unit switch
    {
        PanelColumnUnit.Star => new GridLength(width.Value, GridUnitType.Star),
        PanelColumnUnit.Auto => new GridLength(width.Value, GridUnitType.Auto),
        _ => new GridLength(width.Value, GridUnitType.Pixel)
    };
}

using System.Globalization;

namespace BethesdaMultitool.Core.Ui;

/// <summary>
///     The asset browser's three-column layout as it is persisted across sessions: the tree and preview
///     column widths and whether each side pane is collapsed. Kept free of WinUI types so the clamping
///     and the settings round trip are unit-testable without a GUI; the tab applies it to its columns.
///     <para>
///         Widths are clamped to <c>[minimum, <see cref="MaxWidth" />]</c> on every route in, so a
///         settings file that was edited by hand or written by a wider display can never restore a
///         column narrower than its splitter allows. A non-finite width is a programming error and
///         throws; a non-numeric or non-positive stored value is ignored in favor of the default.
///     </para>
/// </summary>
/// <param name="TreeWidth">The asset tree column's expanded width in DIPs.</param>
/// <param name="PreviewWidth">The preview column's expanded width in DIPs.</param>
/// <param name="TreeCollapsed">Whether the asset tree pane is collapsed to its strip.</param>
/// <param name="PreviewCollapsed">Whether the preview pane is collapsed to its strip.</param>
internal sealed record AssetPaneLayout(double TreeWidth, double PreviewWidth, bool TreeCollapsed, bool PreviewCollapsed)
{
    /// <summary>The narrowest the asset tree column can be dragged, in DIPs.</summary>
    internal const double TreeMinWidth = 180;

    /// <summary>The narrowest the gallery column can be squeezed, in DIPs; recorded here beside its siblings.</summary>
    internal const double GalleryMinWidth = 240;

    /// <summary>The narrowest the preview column can be dragged, in DIPs.</summary>
    internal const double PreviewMinWidth = 320;

    /// <summary>The widest either side column is ever restored to, in DIPs.</summary>
    internal const double MaxWidth = 4096;

    /// <summary>The width of the strip a collapsed pane leaves behind, in DIPs.</summary>
    internal const double StripWidth = 36;

    /// <summary>The asset tree column's width on first launch, in DIPs.</summary>
    internal const double DefaultTreeWidth = 280;

    /// <summary>The preview column's width on first launch, in DIPs.</summary>
    internal const double DefaultPreviewWidth = 420;

    /// <summary>The workflow-settings key of <see cref="TreeWidth" />.</summary>
    internal const string TreeWidthKey = "Browser.Assets.TreeWidth";

    /// <summary>The workflow-settings key of <see cref="PreviewWidth" />.</summary>
    internal const string PreviewWidthKey = "Browser.Assets.PreviewWidth";

    /// <summary>The workflow-settings key of <see cref="TreeCollapsed" />.</summary>
    internal const string TreeCollapsedKey = "Browser.Assets.TreeCollapsed";

    /// <summary>The workflow-settings key of <see cref="PreviewCollapsed" />.</summary>
    internal const string PreviewCollapsedKey = "Browser.Assets.PreviewCollapsed";

    /// <summary>The layout on first launch: 280 and 420 DIPs, both panes expanded.</summary>
    internal static AssetPaneLayout Default { get; } = new(DefaultTreeWidth, DefaultPreviewWidth, false, false);

    /// <summary>The asset tree column's expanded width in DIPs, clamped to its allowed range.</summary>
    internal double TreeWidth { get; init; } = ClampWidth(TreeWidth, TreeMinWidth, nameof(TreeWidth));

    /// <summary>The preview column's expanded width in DIPs, clamped to its allowed range.</summary>
    internal double PreviewWidth { get; init; } = ClampWidth(PreviewWidth, PreviewMinWidth, nameof(PreviewWidth));

    /// <summary>
    ///     Reads a layout out of stored settings. Each missing, unparseable, non-finite or non-positive
    ///     value falls back to <see cref="Default" />'s; a numeric width outside its range is clamped.
    ///     Numbers are read in the invariant culture only, so a locale-formatted <c>"1,5"</c> is garbage.
    /// </summary>
    /// <param name="values">The settings as stored, keyed by the <c>Browser.Assets.*</c> names; unknown keys are ignored.</param>
    internal static AssetPaneLayout Parse(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new AssetPaneLayout(
            ParseWidth(values, TreeWidthKey, TreeMinWidth, DefaultTreeWidth),
            ParseWidth(values, PreviewWidthKey, PreviewMinWidth, DefaultPreviewWidth),
            ParseFlag(values, TreeCollapsedKey, Default.TreeCollapsed),
            ParseFlag(values, PreviewCollapsedKey, Default.PreviewCollapsed));
    }

    /// <summary>The four settings this layout persists, formatted in the invariant culture.</summary>
    internal IReadOnlyDictionary<string, string> ToSettings() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TreeWidthKey] = TreeWidth.ToString(CultureInfo.InvariantCulture),
            [PreviewWidthKey] = PreviewWidth.ToString(CultureInfo.InvariantCulture),
            [TreeCollapsedKey] = TreeCollapsed ? "true" : "false",
            [PreviewCollapsedKey] = PreviewCollapsed ? "true" : "false"
        };

    /// <summary>A copy with the tree column at <paramref name="width" />, clamped to its range.</summary>
    /// <param name="width">The dragged width in DIPs.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width" /> is NaN or infinite.</exception>
    internal AssetPaneLayout WithTreeWidth(double width) =>
        this with { TreeWidth = ClampWidth(width, TreeMinWidth, nameof(width)) };

    /// <summary>A copy with the preview column at <paramref name="width" />, clamped to its range.</summary>
    /// <param name="width">The dragged width in DIPs.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width" /> is NaN or infinite.</exception>
    internal AssetPaneLayout WithPreviewWidth(double width) =>
        this with { PreviewWidth = ClampWidth(width, PreviewMinWidth, nameof(width)) };

    /// <summary>A copy with the tree pane collapsed or expanded.</summary>
    /// <param name="collapsed">True for the strip, false for the expanded pane.</param>
    internal AssetPaneLayout WithTreeCollapsed(bool collapsed) => this with { TreeCollapsed = collapsed };

    /// <summary>A copy with the preview pane collapsed or expanded.</summary>
    /// <param name="collapsed">True for the strip, false for the expanded pane.</param>
    internal AssetPaneLayout WithPreviewCollapsed(bool collapsed) => this with { PreviewCollapsed = collapsed };

    private static double ClampWidth(double width, double minimum, string paramName)
    {
        if (!double.IsFinite(width))
        {
            throw new ArgumentOutOfRangeException(paramName, width, "A pane width must be a finite number of DIPs.");
        }

        return Math.Clamp(width, minimum, MaxWidth);
    }

    private static double ParseWidth(IReadOnlyDictionary<string, string> values, string key, double minimum, double fallback)
    {
        if (!values.TryGetValue(key, out var text) ||
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var width) ||
            !double.IsFinite(width) || width <= 0)
        {
            return fallback;
        }

        return Math.Clamp(width, minimum, MaxWidth);
    }

    private static bool ParseFlag(IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var text) && bool.TryParse(text, out var flag) ? flag : fallback;
}

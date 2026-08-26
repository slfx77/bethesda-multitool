using System.Globalization;
using BethesdaMultitool.Core.WorldData;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

/// <summary>
///     The tooltip text shown while dragging the draw-distance and heatmap-range sliders.
///     <para>
///         Replaces two source pins that asserted the literal <c>"{0:0.#} c"</c> still appeared in
///         a WinUI converter. That checked the format string, not the output: it could not tell
///         whether the slider position was converted to cells correctly, whether the top stop
///         showed "Unlimited", or what a user in a comma-decimal locale actually sees.
///     </para>
///     <para>
///         Culture is passed explicitly here. Production uses <c>CurrentCulture</c> — deliberately,
///         since this is user-facing text — so the tests fix a culture rather than inheriting the
///         agent's, which would make them pass or fail by machine.
///     </para>
/// </summary>
public class SliderTooltipFormattingTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>A locale that uses a comma as the decimal separator.</summary>
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    [Fact]
    public void DrawDistance_AtMinimumStop_ShowsTheMinimumCellCount()
    {
        var text = RenderDistanceScale.FormatCells(RenderDistanceScale.SliderMinimum, Invariant);

        Assert.Equal(
            string.Format(Invariant, "{0:0.#} c", RenderDistanceScale.MinCells), text);
    }

    [Fact]
    public void DrawDistance_AtMaximumStop_ShowsTheMaximumCellCount()
    {
        var text = RenderDistanceScale.FormatCells(RenderDistanceScale.SliderMaximum, Invariant);

        Assert.Equal(
            string.Format(Invariant, "{0:0.#} c", RenderDistanceScale.MaxCells), text);
    }

    /// <summary>
    ///     The tooltip must round-trip the scale: formatting the slider position derived from N
    ///     cells has to report N cells back, or the label contradicts the slider.
    /// </summary>
    [Theory]
    [InlineData(4f)]
    [InlineData(8f)]
    [InlineData(16f)]
    [InlineData(64f)]
    [InlineData(200f)]
    public void DrawDistance_RoundTripsTheCellCountThroughTheSliderScale(float cells)
    {
        var slider = RenderDistanceScale.SliderFromCells(cells);

        Assert.Equal(string.Format(Invariant, "{0:0.#} c", cells),
            RenderDistanceScale.FormatCells(slider, Invariant));
    }

    /// <summary>One decimal place, not more — the slider is logarithmic and lands on fractions.</summary>
    [Fact]
    public void DrawDistance_FractionalCellCount_ShowsAtMostOneDecimalPlace()
    {
        var slider = RenderDistanceScale.SliderFromCells(3.14159f);

        var text = RenderDistanceScale.FormatCells(slider, Invariant);

        var number = text.Replace(" c", "", StringComparison.Ordinal);
        var decimalPlaces = number.Contains('.', StringComparison.Ordinal)
            ? number.Split('.')[1].Length
            : 0;
        Assert.True(decimalPlaces <= 1, $"Expected at most 1 decimal place, got `{text}`.");
    }

    [Fact]
    public void DrawDistance_UsesTheSuppliedCulturesDecimalSeparator()
    {
        // 5.5 is inside [MinCells, MaxCells], so it survives the clamp and keeps its fraction.
        var slider = RenderDistanceScale.SliderFromCells(5.5f);

        Assert.Equal("5.5 c", RenderDistanceScale.FormatCells(slider, Invariant));
        Assert.Equal("5,5 c", RenderDistanceScale.FormatCells(slider, German));
    }

    /// <summary>
    ///     Out-of-range requests clamp into the slider's domain rather than reporting a distance the
    ///     slider cannot represent.
    /// </summary>
    [Theory]
    [InlineData(0.5f, RenderDistanceScale.MinCells, "below the floor")]
    [InlineData(1_000f, RenderDistanceScale.MaxCells, "above the ceiling")]
    public void DrawDistance_OutOfRangeCellCount_ClampsIntoTheSliderDomain(
        float requested, float expected, string because)
    {
        _ = because;

        var text = RenderDistanceScale.FormatCells(
            RenderDistanceScale.SliderFromCells(requested), Invariant);

        Assert.Equal(string.Format(Invariant, "{0:0.#} c", expected), text);
    }

    [Fact]
    public void HeatmapRange_AtTheTopStop_ShowsUnlimitedRatherThanANumber()
    {
        var text = FormIdHeatmapRangeScale.FormatCells(FormIdHeatmapRangeScale.SliderMaximum, Invariant);

        Assert.Equal(FormIdHeatmapRangeScale.UnlimitedLabel, text);
        Assert.DoesNotContain("c", text, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Below the top stop the range is finite and must read as a cell count — the "Unlimited"
    ///     label applies to the dedicated stop only, not to the largest finite value.
    /// </summary>
    [Fact]
    public void HeatmapRange_AtTheLargestFiniteValue_ShowsACellCount()
    {
        var slider = FormIdHeatmapRangeScale.SliderFromCells(FormIdHeatmapRangeScale.MaxFiniteCells);

        var text = FormIdHeatmapRangeScale.FormatCells(slider, Invariant);

        Assert.NotEqual(FormIdHeatmapRangeScale.UnlimitedLabel, text);
        Assert.EndsWith(" c", text, StringComparison.Ordinal);
    }

    /// <summary>The heatmap tooltip uses whole cells — no decimal point at any finite stop.</summary>
    [Theory]
    [InlineData(2f)]
    [InlineData(3f)]
    [InlineData(7f)]
    [InlineData(12f)]
    [InlineData(128f)]
    public void HeatmapRange_FiniteValues_AreShownAsWholeCells(float cells)
    {
        var slider = FormIdHeatmapRangeScale.SliderFromCells(cells);

        var text = FormIdHeatmapRangeScale.FormatCells(slider, Invariant);

        Assert.DoesNotContain(".", text, StringComparison.Ordinal);
        Assert.Equal(string.Format(Invariant, "{0:0} c", cells), text);
    }

    /// <summary>
    ///     Which converter each slider is wired to is XAML, so it stays a source pin — but it is
    ///     now the only thing pinned by text.
    /// </summary>
    [Fact]
    public void SettingsPanel_WiresTheSlidersToTheirTooltipConverters()
    {
        var xaml = SourceContract.ReadAppSource("WorldView3DSettingsPanel.xaml");

        Assert.Contains("ThumbToolTipValueConverter=\"{StaticResource DrawDistanceTooltip}\"", xaml,
            StringComparison.Ordinal);
    }
}

namespace BethesdaMultitool.Core.WorldData;

/// <summary>
///     Maps the 3D viewer's FormID-heatmap range slider to a camera-centred distance in cells and
///     back. Same log₂-cells shape as <see cref="RenderDistanceScale" /> (value = log₂(cells), so a
///     ×1.25 step is a constant slider increment) with two additions: the slider's TOP band — one
///     ×1.25 step (≈0.32) past the finite ceiling — is a dedicated UNLIMITED stop that maps to
///     <see cref="float.PositiveInfinity" /> (every loaded ref participates in the normalization),
///     and finite positions quantize to WHOLE cells — the heatmap selects full cells on the cell
///     grid, never a fractional-radius slice, so the value the user reads is the value applied.
///     Pure math so the fast suite can hold it — the WinUI panel is windows-TFM-only.
/// </summary>
internal static class FormIdHeatmapRangeScale
{
    public const float MinCells = 2f;

    /// <summary>Largest FINITE range; every slider position past log₂ of this reads as unlimited.</summary>
    public const float MaxFiniteCells = 128f;

    /// <summary>Slider travel reserved for the unlimited top stop (≈ log₂ 1.25, one keyboard step).</summary>
    private const double UnlimitedStopWidth = 0.32;

    public static double SliderMinimum => Math.Log2(MinCells);
    public static double SliderMaximum => Math.Log2(MaxFiniteCells) + UnlimitedStopWidth;

    /// <summary>Slider position for a range in cells; PositiveInfinity pins the unlimited top stop.
    /// Finite inputs clamp into [<see cref="MinCells" />, <see cref="MaxFiniteCells" />].</summary>
    public static double SliderFromCells(float cells) =>
        float.IsPositiveInfinity(cells)
            ? SliderMaximum
            : Math.Log2(Math.Clamp(cells, MinCells, MaxFiniteCells));

    /// <summary>Range in WHOLE cells for a slider position (nearest integer — the selection is
    /// full cells on the cell grid); anything past the finite ceiling is unlimited.</summary>
    public static float CellsFromSlider(double sliderValue)
    {
        if (sliderValue > Math.Log2(MaxFiniteCells))
        {
            return float.PositiveInfinity;
        }

        var cells = MathF.Round((float)Math.Pow(2d, sliderValue));
        return Math.Clamp(cells, MinCells, MaxFiniteCells);
    }
}

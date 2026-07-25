namespace BethesdaMultitool.Core.WorldData;

/// <summary>
///     Maps the 3D viewer's draw-distance slider to render-distance cells and back. The slider is
///     log₂-scaled so the keyboard's ×1.25 PageUp/PageDown step is a constant slider increment
///     (log₂ 1.25 ≈ 0.32) and the low end keeps fine resolution: value = log₂(cells), spanning
///     4 cells (value 2) … 200 cells (≈7.64 — the usable ceiling; the owner's clamp on the actual
///     render distance stays authoritative). Pure math so the mapping is fast-suite testable — the
///     WinUI panel is windows-TFM-only.
/// </summary>
internal static class RenderDistanceScale
{
    public const float MinCells = 4f;
    public const float MaxCells = 200f;

    public static double SliderMinimum => Math.Log2(MinCells);
    public static double SliderMaximum => Math.Log2(MaxCells);

    /// <summary>Slider position for a distance in cells (clamped into the slider's range).</summary>
    public static double SliderFromCells(float cells)
    {
        var clamped = Math.Clamp(cells, MinCells, MaxCells);
        return Math.Log2(clamped);
    }

    /// <summary>Distance in cells for a slider position (clamped into [MinCells, MaxCells]).</summary>
    public static float CellsFromSlider(double sliderValue)
    {
        var cells = (float)Math.Pow(2d, sliderValue);
        return Math.Clamp(cells, MinCells, MaxCells);
    }
}

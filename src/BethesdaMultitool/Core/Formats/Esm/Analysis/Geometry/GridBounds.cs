namespace BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;

/// <summary>Inclusive min/max cell-grid coordinate bounds of a worldspace.</summary>
public sealed class GridBounds
{
    public int MinX { get; set; }
    public int MaxX { get; set; }
    public int MinY { get; set; }
    public int MaxY { get; set; }
}

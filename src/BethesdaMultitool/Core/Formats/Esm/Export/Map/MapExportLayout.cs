namespace BethesdaMultitool.Core.Formats.Esm.Export.Map;

/// <summary>Complete layout result for one map export frame.</summary>
public sealed class MapExportLayout
{
    public required IReadOnlyList<MarkerLayout> Markers { get; init; }
    public required IReadOnlyList<LabelLayout> Labels { get; init; }
    public required IReadOnlyList<GridLine> GridLines { get; init; }
    public required MapExportSizing Sizing { get; init; }
}

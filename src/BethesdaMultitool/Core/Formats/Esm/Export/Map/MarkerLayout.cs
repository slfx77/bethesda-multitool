using BethesdaMultitool.Core.Formats.Esm.Enums;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Map;

/// <summary>A positioned marker circle with color and glyph.</summary>
public readonly record struct MarkerLayout(
    int OriginalIndex,
    float PixelX,
    float PixelY,
    MapMarkerType? Type,
    byte ColorR,
    byte ColorG,
    byte ColorB,
    string Glyph);

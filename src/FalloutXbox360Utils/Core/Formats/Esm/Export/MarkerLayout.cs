using FalloutXbox360Utils.Core.Formats.Esm.Enums;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

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
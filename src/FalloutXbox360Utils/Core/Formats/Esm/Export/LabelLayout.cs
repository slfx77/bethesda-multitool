namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>A positioned label with optional leader line back to its marker.</summary>
public readonly record struct LabelLayout(
    int MarkerIndex,
    float LabelX,
    float LabelY,
    float PillWidth,
    float PillHeight,
    float PadH,
    float PadV,
    float TextHeight,
    string Text,
    bool NeedsLeader,
    float MarkerPixelX,
    float MarkerPixelY);

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>Proportional sizing values computed from the image long edge.</summary>
public readonly record struct MapExportSizing(
    float MarkerRadius,
    float LabelFontSize,
    float OutlineWidth,
    float LabelPadH,
    float LabelPadV,
    float Gap);
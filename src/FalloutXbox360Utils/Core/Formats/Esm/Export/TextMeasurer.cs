namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Measures rendered text dimensions. Each renderer provides its own implementation
///     (precise Win2D CanvasTextLayout vs estimated character-width multiplication).
/// </summary>
public delegate (float Width, float Height) TextMeasurer(string text, float fontSize);

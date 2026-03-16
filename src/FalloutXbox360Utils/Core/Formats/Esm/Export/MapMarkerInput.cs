using FalloutXbox360Utils.Core.Formats.Esm.Enums;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Lightweight projection of any marker type for layout computation.
///     Both PlacedReference (GUI) and ExtractedRefrRecord (CLI) project into this.
/// </summary>
public readonly record struct MapMarkerInput(float WorldX, float WorldY, MapMarkerType? Type, string? Name);
namespace FalloutXbox360Utils.Core.Formats.Esm.Models.World;

/// <summary>
///     Runtime package-start location decoded from ExtraPackageStartLocation.
/// </summary>
public record RuntimePackageStartLocation(
    uint? LocationFormId,
    float X,
    float Y,
    float Z,
    float RotZ);

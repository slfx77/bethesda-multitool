namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Scanned ARMA record data used for skinned hand-to-hand weapon visuals.
/// </summary>
internal sealed class ArmaAddonScanEntry
{
    public string? EditorId { get; init; }
    public uint BipedFlags { get; init; }
    public string? MaleModelPath { get; init; }
    public string? FemaleModelPath { get; init; }
}
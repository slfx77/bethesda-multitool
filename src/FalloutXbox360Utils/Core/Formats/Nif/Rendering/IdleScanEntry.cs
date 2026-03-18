namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Scanned IDLE record data used for special-idle / weapon-idle resolution.
/// </summary>
internal sealed class IdleScanEntry
{
    public string? EditorId { get; init; }
    public string? ModelPath { get; init; }
    public uint? ParentIdleFormId { get; init; }
    public uint? PreviousIdleFormId { get; init; }
    public byte AnimData { get; init; }
    public byte LoopMin { get; init; }
    public byte LoopMax { get; init; }
    public ushort ReplayDelay { get; init; }
    public byte FlagsEx { get; init; }
}

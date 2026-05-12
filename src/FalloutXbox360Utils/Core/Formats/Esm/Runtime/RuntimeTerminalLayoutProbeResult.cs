namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime;

/// <summary>
///     Result of the BGSTerminal layout probe.
///     <see cref="DataShift" /> applies to the TERMINAL_DATA block (Difficulty / Flags /
///     ServerType bytes) at the reference position +132. <see cref="MenuListShift" />
///     applies to the MenuItemList head pointer at the reference position +152. Both
///     default to 0 when no probe ran or confidence was low — leaves the existing
///     hardcoded offsets unchanged.
/// </summary>
internal sealed record RuntimeTerminalLayoutProbeResult(
    int DataShift,
    int MenuListShift,
    int WinnerScore,
    int RunnerUpScore,
    int SampleCount,
    bool IsHighConfidence)
{
    public int Margin => WinnerScore - RunnerUpScore;
}

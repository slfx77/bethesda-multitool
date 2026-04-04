namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime;

/// <summary>
///     Container for all runtime layout probe results. Passed to <see cref="RuntimeStructReader" />
///     constructor so individual readers can access their probe results without parameter explosion.
/// </summary>
internal sealed class RuntimeProbeResults
{
    public RuntimeNpcLayoutProbeResult? NpcLayout { get; init; }
    public RuntimeWorldCellLayoutProbeResult? WorldCellLayout { get; init; }
    public RuntimeLayoutProbeResult<int[]>? BookLayout { get; init; }
    public RuntimeLayoutProbeResult<int[]>? RaceLayout { get; init; }
    public RuntimeLayoutProbeResult<int[]>? EffectLayout { get; init; }
    public RuntimeLayoutProbeResult<int[]>? MagicLayout { get; init; }
    public RuntimeLayoutProbeResult<int[]>? WeaponSoundLayout { get; init; }

    /// <summary>
    ///     Per-FormType uniform shift for the generic PDB reader.
    ///     Key = FormType byte, Value = shift in bytes to apply to all non-TESForm fields.
    /// </summary>
    public IReadOnlyDictionary<byte, int>? GenericTypeShifts { get; init; }
}

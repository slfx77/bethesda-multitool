namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

/// <summary>
///     Identifies engine-owned runtime state records captured from memory that should not
///     be emitted as plugin overrides.
/// </summary>
internal static class RuntimeStateRecordPolicy
{
    private static readonly HashSet<uint> FormIds =
    [
        0x00000007, // Player NPC
        0x00000014, // PlayerRef ACHR (player's placed-actor instance)
        0x00000035, // GameYear GLOB
        0x00000036, // GameMonth GLOB
        0x00000037, // GameDay GLOB
        0x00000038, // GameHour GLOB
        0x00000039, // GameDaysPassed GLOB
        0x0000003A, // TimeScale GLOB
        0x000001F4 // Hand-to-Hand WEAP (engine default unarmed fallback)
    ];

    public static bool IsRuntimeStateFormId(uint formId)
    {
        return FormIds.Contains(formId);
    }

    /// <summary>
    ///     The set of engine-hardcoded FormIDs that aren't stored as records in any ESM but
    ///     that scripts and conditions are allowed to reference. Used by validators that
    ///     would otherwise null these out (e.g. SCRO refs to PlayerRef).
    /// </summary>
    public static IReadOnlySet<uint> EngineFormIds => FormIds;
}

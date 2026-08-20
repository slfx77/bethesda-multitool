namespace BethesdaMultitool.Core.Formats.Esm.Plugin;

/// <summary>
///     Identifies engine-owned runtime state records captured from memory that should not
///     be emitted as plugin overrides.
/// </summary>
internal static class RuntimeStateRecordPolicy
{
    private static readonly HashSet<uint> FormIds =
    [
        0x00000007, // Player NPC
        0x00000014, // PlayerRef (engine-created player reference; no master record)
        0x00000035, // GameYear GLOB
        0x00000036, // GameMonth GLOB
        0x00000037, // GameDay GLOB
        0x00000038, // GameHour GLOB
        0x00000039, // GameDaysPassed GLOB
        0x0000003A, // TimeScale GLOB
        0x000001F4 // Hand-to-Hand WEAP (engine default unarmed fallback)
    ];

    /// <summary>
    ///     The protected engine-owned identities that scripts and conditions may reference.
    ///     Some are master-backed records (for example Player and the clock globals), while
    ///     others such as PlayerRef are created by the engine. Validators use this set so
    ///     neither kind is mistaken for a dangling reference.
    /// </summary>
    public static IReadOnlySet<uint> EngineFormIds => FormIds;

    /// <summary>
    ///     True if the FormID is an engine-owned runtime-state record (player, game clock globals, unarmed weapon) that
    ///     must not be overridden.
    /// </summary>
    public static bool IsRuntimeStateFormId(uint formId)
    {
        return FormIds.Contains(formId);
    }
}

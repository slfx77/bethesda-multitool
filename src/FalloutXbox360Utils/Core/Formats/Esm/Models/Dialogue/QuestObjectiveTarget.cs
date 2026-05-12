using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;

/// <summary>
///     Quest objective target from QSTA + per-target CTDA* subrecords. Each objective can
///     declare multiple targets (e.g., "kill Benny" + "or kill any Khan" as alternative
///     completion paths); the engine evaluates conditions per target.
///     QSTA layout (8 bytes): FormID Target(0) + uint8 Flags(4) + 3 padding.
/// </summary>
public record QuestObjectiveTarget
{
    /// <summary>FormID of the target reference (REFR, NPC_, etc.) from QSTA.</summary>
    public uint TargetFormId { get; init; }

    /// <summary>Target flags from QSTA (e.g., "Ignores Locks" bit).</summary>
    public byte Flags { get; init; }

    /// <summary>Per-target conditions (CTDA* + optional CIS1/CIS2 after QSTA).</summary>
    public List<DialogueCondition> Conditions { get; init; } = [];
}

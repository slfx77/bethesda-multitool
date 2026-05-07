namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Compact script-reference details for an NPC base record. Built from script
///     referenced-object lists so NPC reports can show reverse script usage.
/// </summary>
internal sealed record NpcScriptReferenceInfo(
    uint ScriptFormId,
    string? ScriptEditorId,
    string ScriptType,
    uint? OwnerQuestFormId);

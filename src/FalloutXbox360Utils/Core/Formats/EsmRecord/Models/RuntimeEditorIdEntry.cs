namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Runtime Editor ID entry extracted from the game's EditorID hash table.
///     Includes FormID association obtained by following TESForm pointers.
/// </summary>
public record RuntimeEditorIdEntry
{
    /// <summary>The Editor ID string.</summary>
    public required string EditorId { get; init; }

    /// <summary>Associated FormID from TESForm object.</summary>
    public uint FormId { get; init; }

    /// <summary>Form type code (record type) from TESForm object.</summary>
    public byte FormType { get; init; }

    /// <summary>File offset where the Editor ID string was found.</summary>
    public long StringOffset { get; init; }

    /// <summary>File offset of the TESForm object (if pointer was followed).</summary>
    public long? TesFormOffset { get; init; }

    /// <summary>Virtual address of the TESForm pointer (for debugging).</summary>
    public long? TesFormPointer { get; init; }

    /// <summary>Display name from TESFullName.cFullName (e.g., "Boone's Beret").</summary>
    public string? DisplayName { get; init; }

    /// <summary>Dialogue prompt from TESTopicInfo.cPrompt (INFO records only, FormType auto-detected).</summary>
    public string? DialogueLine { get; set; }
}

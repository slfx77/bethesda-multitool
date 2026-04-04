namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

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

    /// <summary>
    ///     Form type code (record type) from TESForm object. May be remapped from
    ///     early-build ENUM_FORM_ID values to final-build codes during drift detection.
    /// </summary>
    public byte FormType { get; set; }

    /// <summary>
    ///     Original FormType byte from the raw dump buffer, before drift correction.
    ///     Set only when drift remapping changes FormType; null when no drift applies.
    ///     Used by runtime struct readers to validate buffer[4] in early builds where
    ///     the raw byte differs from the final-build code.
    /// </summary>
    public byte? OriginalFormType { get; set; }

    /// <summary>File offset where the Editor ID string was found.</summary>
    public long StringOffset { get; init; }

    /// <summary>File offset of the TESForm object (if pointer was followed).</summary>
    public long? TesFormOffset { get; init; }

    /// <summary>Virtual address of the TESForm pointer (for debugging).</summary>
    public long? TesFormPointer { get; init; }

    /// <summary>Display name from TESFullName.cFullName (e.g., "Boone's Beret").</summary>
    public string? DisplayName { get; init; }

    /// <summary>File offset of the display name string in the dump (for string ownership claims).</summary>
    public long? DisplayNameStringOffset { get; init; }

    /// <summary>Dialogue prompt from TESTopicInfo.cPrompt (INFO records only, FormType auto-detected).</summary>
    public string? DialogueLine { get; set; }

    /// <summary>File offset of the dialogue line string in the dump (for string ownership claims).</summary>
    public long? DialogueLineStringOffset { get; set; }
}

using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Models;

/// <summary>
///     Result of applying the standalone SCPT source-correspondence policy to a runtime
///     Script object's captured SCTX. Raw runtime data remains available for diagnostics.
///     AcceptedSourceOnly permits standalone source recovery but is not declaration evidence
///     for conditions attached to other records.
/// </summary>
public enum ScriptSourceCorrespondenceStatus
{
    Unverified,
    Accepted,
    AcceptedSourceOnly,
    Rejected
}

/// <summary>
///     Runtime script data read from a Script C++ struct in Xbox 360 memory.
///     Fields informed by PDB Script class layout (84 bytes PDB, 100 bytes runtime).
/// </summary>
public record RuntimeScriptData
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }

    // SCRIPT_HEADER (20 bytes at runtime offset 40)
    // Loaded Xbox scripts commonly zero variableCount while retaining a complete,
    // explicitly indexed listVariables chain. Preserve that raw value separately;
    // VariableCount is the effective count after the reader validates the same object's
    // listVariables metadata.
    public uint HeaderVariableCount { get; init; }
    public uint VariableCount { get; init; }
    public uint RefObjectCount { get; init; }
    public uint DataSize { get; init; }
    public uint LastVariableId { get; init; }
    public bool IsQuestScript { get; init; }
    public bool IsMagicEffectScript { get; init; }
    public bool IsCompiled { get; init; }

    // Runtime pointers followed
    public string? SourceText { get; init; }

    /// <summary>
    ///     Whether <see cref="SourceText" /> survived the same dump's standalone SCPT
    ///     correspondence gate. This is derived after runtime/fragment merging and is not
    ///     read directly from memory.
    /// </summary>
    public ScriptSourceCorrespondenceStatus SourceTextCorrespondenceStatus { get; init; }

    public byte[]? CompiledData { get; init; }
    public uint? OwnerQuestFormId { get; init; }
    public float QuestScriptDelay { get; init; }

    // From BSSimpleList walks
    public List<(uint FormId, string? EditorId)> ReferencedObjects { get; init; } = [];
    public List<ScriptVariableInfo> Variables { get; init; } = [];

    /// <summary>
    ///     Whether the referenced-object walk reached a null terminator, stayed below the
    ///     corruption guard, decoded every item, and matched <see cref="RefObjectCount" />.
    ///     A nonempty list is not, by itself, proof that a dump captured the complete chain.
    /// </summary>
    public bool ReferencedObjectsComplete { get; init; }

    /// <summary>
    ///     Whether the variable walk reached a null terminator, stayed below the corruption
    ///     guard, decoded every item, passed explicit-ID/name/type validation, and either
    ///     matched <see cref="HeaderVariableCount" /> or recovered a zeroed runtime count.
    ///     This is the stricter executable-bundle check.
    /// </summary>
    public bool VariablesComplete { get; init; }

    /// <summary>
    ///     Whether the variable-list walk itself reached a null terminator, stayed below
    ///     the corruption guard, and decoded every valid explicit uiID/type/cName item.
    ///     This can remain true for a nonzero header/list-count conflict so diagnostics can
    ///     distinguish clean conflicting metadata from a corrupt walk; VariablesComplete
    ///     remains false in that case.
    /// </summary>
    public bool VariableMetadataComplete { get; init; }

    public long DumpOffset { get; init; }
}

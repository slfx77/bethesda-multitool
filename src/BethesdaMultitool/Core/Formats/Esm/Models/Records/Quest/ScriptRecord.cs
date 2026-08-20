namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

/// <summary>
///     Provenance of the exact SCTX text carried by a captured standalone or inline script. This is
///     deliberately identity-based: runtime text is only marked when it came from the same
///     runtime Script object; when bytecode exists, its accepted executable bundle came from
///     that object too. Editor-ID matches and other dumps are never source evidence.
/// </summary>
public enum ScriptSourceTextOrigin
{
    None,
    DmpFragment,
    RuntimeSameObject
}

/// <summary>
///     Parsed Script (SCPT) record.
///     Fields informed by PDB SCRIPT_HEADER struct (20 bytes) and ESM subrecord structure.
/// </summary>
public record ScriptRecord
{
    // Identity
    /// <summary>FormID of the script record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID (EDID subrecord).</summary>
    public string? EditorId { get; init; }

    // From SCHR (PDB: SCRIPT_HEADER, 20 bytes)
    /// <summary>Number of local variables (SCRIPT_HEADER.variableCount).</summary>
    public uint VariableCount { get; init; }

    /// <summary>Number of referenced objects (SCRIPT_HEADER.refObjectCount).</summary>
    public uint RefObjectCount { get; init; }

    /// <summary>Compiled bytecode size in bytes (SCRIPT_HEADER.dataSize).</summary>
    public uint CompiledSize { get; init; }

    /// <summary>Last variable ID assigned (SCRIPT_HEADER.m_uiLastID).</summary>
    public uint LastVariableId { get; init; }

    /// <summary>Whether this is a quest script (SCRIPT_HEADER.bIsQuestScript).</summary>
    public bool IsQuestScript { get; init; }

    /// <summary>Whether this is a magic effect script (SCRIPT_HEADER.bIsMagicEffectScript).</summary>
    public bool IsMagicEffectScript { get; init; }

    /// <summary>Whether the script is compiled (SCRIPT_HEADER.bIsCompiled).</summary>
    public bool IsCompiled { get; init; }

    /// <summary>
    ///     Whether a complete serialized SCHR subrecord supplied the executable metadata.
    ///     Runtime Script objects use <see cref="ExecutableBundleFromRuntime" /> instead.
    /// </summary>
    public bool HasSerializedHeader { get; init; }

    /// <summary>Whether a serialized SCHR was present but too short or otherwise ambiguous.</summary>
    public bool HasMalformedSerializedHeader { get; init; }

    /// <summary>
    ///     Whether a serialized SLSD/SCVR/SCRO/SCRV component was short or structurally
    ///     orphaned. The table must fail closed even when SCHR happens to declare zero.
    /// </summary>
    public bool HasMalformedSerializedTable { get; init; }

    /// <summary>
    ///     Whether SCDA and its header/tables were captured atomically from the same runtime
    ///     Script object. This is stronger evidence than an editor-ID match.
    /// </summary>
    public bool ExecutableBundleFromRuntime { get; init; }

    /// <summary>
    ///     True when executable content was declared but the SCHR/SCDA/table bundle failed
    ///     structural validation. Such a script must not be emitted as an executable SCPT.
    /// </summary>
    public bool IsIncompleteExecutableBundle { get; init; }

    // From SCTX (source text)
    /// <summary>Script source text from SCTX subrecord.</summary>
    public string? SourceText { get; init; }

    /// <summary>
    ///     Where <see cref="SourceText" /> came from within the dump currently being parsed.
    ///     This is diagnostic metadata only and is never serialized into the plugin.
    /// </summary>
    public ScriptSourceTextOrigin SourceTextOrigin { get; init; }

    /// <summary>
    ///     Result of the same-dump standalone SCTX/SCDA correspondence gate. Source-only
    ///     text remains recoverable and diagnostic, but only Accepted compiled source may
    ///     prove a local declaration used by another emitted record.
    /// </summary>
    public ScriptSourceCorrespondenceStatus SourceTextCorrespondenceStatus { get; init; }

    // From SCDA (compiled bytecode)
    /// <summary>Raw compiled bytecode from SCDA subrecord.</summary>
    public byte[]? CompiledData { get; init; }

    // Decompiled output (generated from CompiledData)
    /// <summary>Decompiled bytecode text (generated from SCDA data).</summary>
    public string? DecompiledText { get; init; }

    // From SLSD + SCVR pairs (variable definitions)
    /// <summary>Script local variables from SLSD+SCVR subrecord pairs.</summary>
    public List<ScriptVariableInfo> Variables { get; init; } = [];

    // From SCRO (referenced objects)
    /// <summary>FormIDs of referenced objects from SCRO subrecords.</summary>
    public List<uint> ReferencedObjects { get; init; } = [];

    // Quest association (from runtime pOwnerQuest pointer)
    /// <summary>FormID of the owning quest (from runtime struct, may be null for ESM-only).</summary>
    public uint? OwnerQuestFormId { get; init; }

    /// <summary>Quest script delay in seconds (from runtime struct).</summary>
    public float QuestScriptDelay { get; init; }

    // Metadata
    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Whether this script was found via runtime struct reading (not ESM fragments).</summary>
    public bool FromRuntime { get; init; }

    // Computed
    /// <summary>Whether the script has source text (SCTX).</summary>
    public bool HasSource => !string.IsNullOrEmpty(SourceText);

    /// <summary>Script type description based on header flags.</summary>
    public string ScriptType
    {
        get
        {
            if (IsQuestScript)
            {
                return "Quest";
            }

            if (IsMagicEffectScript)
            {
                return "Effect";
            }

            return "Object";
        }
    }
}

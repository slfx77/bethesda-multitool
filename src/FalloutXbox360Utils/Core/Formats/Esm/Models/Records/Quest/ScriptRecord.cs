namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Fully reconstructed Script (SCPT) record from memory dump.
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

    // From SCTX (source text)
    /// <summary>Script source text from SCTX subrecord.</summary>
    public string? SourceText { get; init; }

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
            if (IsQuestScript) { return "Quest"; }
            if (IsMagicEffectScript) { return "Effect"; }
            return "Object";
        }
    }
}

namespace BethesdaMultitool.Core.Formats.Esm.Script;

/// <summary>
///     Definition of one script-command or condition-function parameter. Engine-backed entries retain
///     the numeric type id from the executable's parameter structs; explicitly attributed community
///     condition-only entries may instead map community metadata into that game's known engine enum.
///     <see cref="RawType" /> is therefore the table's game-specific numeric id, while <see cref="Type" />
///     interprets it under FNV/FO3's <see cref="ScriptParamType" /> numbering and is only meaningful for
///     those tables. TES4 and FO4 entries use their game-specific constructors and must be read through
///     <see cref="ObType" /> or <see cref="Fallout4Type" /> respectively.
/// </summary>
/// <param name="Name">Parameter name (e.g., "ObjectReferenceID", "Count").</param>
/// <param name="Type">Parameter type under FNV/FO3 numbering (raw-cast for other games).</param>
/// <param name="Optional">Whether this parameter is optional in script source.</param>
public record ScriptFunctionParamDef(string Name, ScriptParamType Type, bool Optional)
{
    /// <summary>TES4 table constructor — stores the engine's raw id; read back via <see cref="ObType" />.</summary>
    public ScriptFunctionParamDef(string name, ObScriptParamType type, bool optional)
        : this(name, (ScriptParamType)(ushort)type, optional)
    {
    }

    /// <summary>FO4 table constructor — stores the engine's raw id; read through <see cref="Fallout4Type" />.</summary>
    public ScriptFunctionParamDef(string name, Fallout4ScriptParamType type, bool optional)
        : this(name, (ScriptParamType)(ushort)type, optional)
    {
    }

    /// <summary>The table's game-specific numeric parameter-type id; see the type-level provenance note.</summary>
    public ushort RawType => (ushort)Type;

    /// <summary>This parameter's type under TES4 numbering (only meaningful for TES4 tables).</summary>
    public ObScriptParamType ObType => (ObScriptParamType)RawType;

    /// <summary>This parameter's type under FO4 numbering (only meaningful for the FO4 table).</summary>
    public Fallout4ScriptParamType Fallout4Type => (Fallout4ScriptParamType)RawType;
}

namespace BethesdaMultitool.Core.Formats.Esm.Script;

/// <summary>
///     Definition of a single parameter in a script function, extracted from the game executable's
///     parameter structs. <see cref="RawType" /> is the game's own numeric param-type id and is the
///     authoritative value; <see cref="Type" /> interprets it under FNV/FO3's
///     <see cref="ScriptParamType" /> numbering and is only meaningful for those tables — TES4
///     entries are built through the <see cref="ObScriptParamType" /> constructor and must be read
///     via <see cref="ObType" /> (the numberings diverge from id 32 on).
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

    /// <summary>The game's raw param-type id as shipped in the executable's parameter struct.</summary>
    public ushort RawType => (ushort)Type;

    /// <summary>This parameter's type under TES4 numbering (only meaningful for TES4 tables).</summary>
    public ObScriptParamType ObType => (ObScriptParamType)RawType;
}

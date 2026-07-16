namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

/// <summary>
///     Script local variable definition from SLSD+SCVR subrecord pairs.
///     Layout informed by PDB ScriptVariable (32 bytes) and SCRIPT_LOCAL (24 bytes) structs.
/// </summary>
/// <param name="Index">Variable index from SLSD (SCRIPT_LOCAL.uiID).</param>
/// <param name="Name">Variable name from SCVR subrecord.</param>
/// <param name="Type">Variable type byte from SLSD offset 16 (0 = float, non-zero = integer).</param>
public record ScriptVariableInfo(uint Index, string? Name, byte Type)
{
    /// <summary>Human-readable type name.</summary>
    public string TypeName => Type == 0 ? "float" : "int";
}

/// <summary>Canonical PDB-backed field access for the 24-byte SCRIPT_LOCAL / SLSD payload.</summary>
internal static class ScriptLocalVariableLayout
{
    internal const int IsIntegerOffset = 16;

    internal static byte ReadType(ReadOnlySpan<byte> data) =>
        data.Length > IsIntegerOffset && data[IsIntegerOffset] != 0 ? (byte)1 : (byte)0;
}

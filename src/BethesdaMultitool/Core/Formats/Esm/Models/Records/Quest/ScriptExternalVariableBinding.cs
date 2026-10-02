namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

/// <summary>Evidence for an external SCDA variable operand; numeric identity survives name resolution.</summary>
public sealed record ScriptExternalVariableBinding(
    uint OwnerFormId,
    ushort VariableIndex,
    string Status,
    uint? ScriptFormId,
    string? Name,
    IReadOnlyList<uint> CandidateScripts,
    IReadOnlyList<uint> OwnerChain)
{
    public string Summary => Status == "resolved"
        ? $"0x{OwnerFormId:X8}.var{VariableIndex} = {Name} (SCPT 0x{ScriptFormId:X8})"
        : $"0x{OwnerFormId:X8}.var{VariableIndex}: {Status}";
}

namespace FalloutXbox360Utils.Core.Json;

/// <summary>
///     Information about an SCDA (compiled script) record.
/// </summary>
public sealed class JsonScdaRecordInfo
{
    public long Offset { get; set; }
    public int BytecodeLength { get; set; }
    public string? ScriptName { get; set; }
}

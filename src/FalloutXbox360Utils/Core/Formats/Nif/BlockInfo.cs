namespace FalloutXbox360Utils.Core.Formats.Nif;

/// <summary>
///     Information about a single NIF block.
/// </summary>
public sealed class BlockInfo
{
    public int Index { get; set; }
    public ushort TypeIndex { get; set; }
    public string TypeName { get; set; } = "";
    public int Size { get; set; }
    public int DataOffset { get; set; }
}

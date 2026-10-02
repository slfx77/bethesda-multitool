namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>A located decode failure or problem.</summary>
/// <param name="BlockIndex">The block's index in the header block table.</param>
/// <param name="BlockType">The block's type name.</param>
/// <param name="Kind">The failure category.</param>
/// <param name="FieldPath">The field path from the block root (empty for block-level failures).</param>
/// <param name="Offset">The absolute file offset where the failing field starts.</param>
/// <param name="Reason">What went wrong.</param>
internal sealed record NifDecodeFailure(
    int BlockIndex,
    string BlockType,
    NifDecodeFailureKind Kind,
    string FieldPath,
    int Offset,
    string Reason)
{
    /// <summary>A one-line description naming the block, its type, the field path and the offset.</summary>
    public string Message =>
        $"NIF block {BlockIndex} ({BlockType}), field '{(FieldPath.Length == 0 ? "<block>" : FieldPath)}' at " +
        $"offset 0x{Offset:X}: {Reason}";

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{Kind}: {Message}";
    }
}

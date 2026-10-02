namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     One decoded block: its header identity, the value tree and the field spans. A <see cref="NifDecodeMode.Strict" />
///     result is always complete; a <see cref="NifDecodeMode.Tolerant" /> result may carry a <see cref="Failure" />
///     (decoding stopped there; <see cref="Root" /> holds the top-level fields decoded before it) and
///     <see cref="Problems" /> (soft errors met while decoding continued).
/// </summary>
internal sealed class NifDecodedBlock
{
    /// <summary>Creates a decoded block.</summary>
    public NifDecodedBlock(
        int index,
        string type,
        int offset,
        int size,
        NifStructValue root,
        IReadOnlyList<NifFieldSpan> spans,
        int consumedBytes,
        NifDecodeMode mode,
        NifDecodeFailure? failure,
        IReadOnlyList<NifDecodeFailure> problems)
    {
        Index = index;
        Type = type;
        Offset = offset;
        Size = size;
        Root = root;
        Spans = spans;
        ConsumedBytes = consumedBytes;
        Mode = mode;
        Failure = failure;
        Problems = problems;
    }

    /// <summary>The block's index in the header block table.</summary>
    public int Index { get; }

    /// <summary>The block's type name from the header block-type table.</summary>
    public string Type { get; }

    /// <summary>The absolute file offset of the block body.</summary>
    public int Offset { get; }

    /// <summary>The header's Block Size for the block.</summary>
    public int Size { get; }

    /// <summary>The decoded fields (inherited fields first), as a struct named after the block type.</summary>
    public NifStructValue Root { get; }

    /// <summary>Field locations for diagnostics (see <see cref="NifFieldSpan" />).</summary>
    public IReadOnlyList<NifFieldSpan> Spans { get; }

    /// <summary>The bytes the walk consumed (equal to <see cref="Size" /> for a complete decode).</summary>
    public int ConsumedBytes { get; }

    /// <summary>The mode the block was decoded in.</summary>
    public NifDecodeMode Mode { get; }

    /// <summary>The hard failure that stopped a tolerant decode, or null.</summary>
    public NifDecodeFailure? Failure { get; }

    /// <summary>Soft problems met by a tolerant decode (always empty for a strict decode).</summary>
    public IReadOnlyList<NifDecodeFailure> Problems { get; }

    /// <summary>True when the decode had neither a failure nor problems and consumed exactly the block.</summary>
    public bool IsComplete => Failure is null && Problems.Count == 0 && ConsumedBytes == Size;
}

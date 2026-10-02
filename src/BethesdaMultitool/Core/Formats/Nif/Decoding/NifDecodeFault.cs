namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     Internal control-flow exception of the block walk. It is raised where the problem is found (a cursor read, a
///     scope lookup, an expression compile) without a location, located by the innermost field that catches it,
///     and turned into a <see cref="NifDecodeException" /> (strict) or a <see cref="NifDecodeFailure" /> (tolerant)
///     by <see cref="NifBlockDecoder" />. It never escapes the decoder.
/// </summary>
internal sealed class NifDecodeFault : Exception
{
    /// <summary>Creates an unlocated fault.</summary>
    public NifDecodeFault(NifDecodeFailureKind kind, string reason)
        : base(reason)
    {
        Kind = kind;
        Reason = reason;
    }

    private NifDecodeFault(NifDecodeFailureKind kind, string reason, string fieldPath, int offset)
        : base(reason)
    {
        Kind = kind;
        Reason = reason;
        FieldPath = fieldPath;
        Offset = offset;
    }

    /// <summary>The failure category.</summary>
    public NifDecodeFailureKind Kind { get; }

    /// <summary>What went wrong.</summary>
    public string Reason { get; }

    /// <summary>The field path, once located.</summary>
    public string? FieldPath { get; }

    /// <summary>The absolute file offset of the failing field, once located.</summary>
    public int Offset { get; }

    /// <summary>True once a field has attached its path and offset.</summary>
    public bool IsLocated => FieldPath is not null;

    /// <summary>Returns a copy located at a field.</summary>
    public NifDecodeFault At(string fieldPath, int offset)
    {
        return new NifDecodeFault(Kind, Reason, fieldPath, offset);
    }
}

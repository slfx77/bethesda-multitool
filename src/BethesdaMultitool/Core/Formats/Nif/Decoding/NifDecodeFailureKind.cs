namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>What went wrong while decoding a block.</summary>
internal enum NifDecodeFailureKind
{
    /// <summary>The bytes cannot hold what the schema asks for: a read past the block end, an impossible count.</summary>
    Data,

    /// <summary>
    ///     The schema cannot be applied: an unknown or abstract type, an expression that does not compile strictly,
    ///     a name no enclosing definition declares, an unparseable version bound.
    /// </summary>
    Schema,

    /// <summary>A <c>Ref</c> or <c>Ptr</c> outside [-1, block count), or a target that does not match the template.</summary>
    Reference,

    /// <summary>A header string-table index outside [-1, string count).</summary>
    StringIndex,

    /// <summary>The walk consumed a different number of bytes than the header's Block Size.</summary>
    Size
}

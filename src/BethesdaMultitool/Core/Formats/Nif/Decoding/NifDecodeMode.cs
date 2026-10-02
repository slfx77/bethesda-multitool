namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>How <see cref="NifBlockDecoder.Decode" /> treats a block it cannot decode exactly.</summary>
internal enum NifDecodeMode
{
    /// <summary>
    ///     For block types the reader turns into typed state. Any failure throws <see cref="NifDecodeException" />
    ///     (an <see cref="InvalidDataException" />) naming the block, its type, the field path and the offset: a read
    ///     past the block, an array longer than the bytes left, an expression that does not compile or names an
    ///     undeclared field, a reference or string index out of range, a reference whose target type does not match
    ///     the field's template, or consumed bytes that differ from the header's block size.
    /// </summary>
    Strict,

    /// <summary>
    ///     For block types the reader keeps only as native state. Never throws for data or schema errors: the result
    ///     carries the top-level fields decoded before the first hard failure, that failure, and the soft problems
    ///     (reference, template and string-index errors) met while decoding continued.
    /// </summary>
    Tolerant
}

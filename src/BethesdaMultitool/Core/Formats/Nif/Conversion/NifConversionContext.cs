namespace BethesdaMultitool.Core.Formats.Nif.Conversion;

/// <summary>
///     Mutable per-block conversion state threaded through the schema-driven NIF converter. One
///     instance is created per <see cref="NifSchemaConverter.MeasureBlock" /> / <c>TryConvert</c>
///     call and is confined to a single thread for the duration of that block.
/// </summary>
internal sealed class NifConversionContext(
    byte[] buffer,
    int position,
    int end,
    int[] blockRemap,
    Dictionary<string, object> fieldValues,
    string blockType)
{
    public byte[] Buffer { get; } = buffer;
    public int Position { get; set; } = position;
    public int End { get; } = end;
    public int[] BlockRemap { get; } = blockRemap;
    public Dictionary<string, object> FieldValues { get; } = fieldValues;
    public string BlockType { get; } = blockType;

    /// <summary>
    ///     Current template type parameter (#T#) for generic structs like KeyGroup&lt;float&gt;.
    ///     This is set when processing a field with a template attribute and propagates
    ///     to nested structs.
    /// </summary>
    public string? TemplateType { get; set; }

    /// <summary>
    ///     Stack of struct type names currently being converted, outermost first. Used by
    ///     field-level special cases (e.g. <c>NiAGDDataBlock.Data</c>) that need to
    ///     identify their container without crawling the whole conversion call stack.
    /// </summary>
    public Stack<string> StructStack { get; } = new();

    /// <summary>
    ///     Measure-mode Name capture: armed when the block's own NiObjectNET.Name field is
    ///     reached, consumed at the inline <c>SizedString</c> that holds the name.
    /// </summary>
    public bool CapturingName { get; set; }

    /// <summary>
    ///     Captured NiObjectNET.Name (inline <c>SizedString</c>) during a measure pass, or null
    ///     for blocks that don't derive from NiObjectNET / have no name.
    /// </summary>
    public string? CapturedName { get; set; }
}

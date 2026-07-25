namespace BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;

/// <summary>
///     How a subrecord field that is little-endian even on big-endian Xbox 360 must be read, so a
///     big-endian schema decoder can override its default byte-swap for exactly that field. Produced by
///     <see cref="SubrecordSchemaRegistry.GetFixedLeLayout" /> — the single source of Xbox fixed-LE truth
///     (the conversion schema), consulted live rather than duplicated into the read authorities.
/// </summary>
public enum LeFieldKind
{
    /// <summary>
    ///     Read the field as little-endian for its declared width (a FormId / UInt16 / Int32 that Xbox
    ///     stores in native little-endian format).
    /// </summary>
    LittleEndian,

    /// <summary>
    ///     Read as a word-swapped (middle-endian) uint32: two big-endian uint16 words in little-endian
    ///     order, so value = (BE16 at offset+2 &lt;&lt; 16) | BE16 at offset. Mirrors
    ///     <c>SubrecordSchemaReader.ReadUInt32WordSwapped</c> (RGDL DATA DynamicBoneCount).
    /// </summary>
    WordSwapped
}

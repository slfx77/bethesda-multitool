namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>The shape of a decoded <see cref="NifValue" />.</summary>
internal enum NifValueKind
{
    /// <summary>An integer of a declared width and signedness (basic integers, bools, enums, bitflags, bitfields).</summary>
    Integer,

    /// <summary>A 32-bit IEEE float, kept as its bits.</summary>
    Float,

    /// <summary>A 16-bit IEEE float (<c>hfloat</c>), kept as its bits.</summary>
    HalfFloat,

    /// <summary>A header string-table index plus the raw bytes it resolves to.</summary>
    String,

    /// <summary>An inline length-prefixed string (<c>SizedString</c>, <c>SizedString16</c>), kept as raw bytes.</summary>
    SizedString,

    /// <summary>A block reference (<c>Ref</c>) or back pointer (<c>Ptr</c>).</summary>
    Reference,

    /// <summary>A struct or block with ordered fields.</summary>
    Struct,

    /// <summary>A list of values, or of rows for two-dimensional and jagged arrays.</summary>
    Array,

    /// <summary>A bulk array of float-component elements (float, Vector3, Color4, TexCoord and similar).</summary>
    FloatArray,

    /// <summary>A bulk array of ushort-component elements (ushort, Triangle).</summary>
    UInt16Array,

    /// <summary>A bulk array of bytes (byte, char).</summary>
    ByteArray
}

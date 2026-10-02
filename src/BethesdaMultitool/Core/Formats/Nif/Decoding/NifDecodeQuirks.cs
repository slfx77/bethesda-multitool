using BethesdaMultitool.Core.Formats.Nif.Schema;

namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     The explicit, cited table of places where the bytes Bethesda shipped do not follow nif.xml read literally in
///     the file's byte order. Everything not listed here is read exactly as nif.xml says. Each entry is pinned by a
///     synthetic test in <c>tests/.../Nif/Decoding/NifDecodeQuirksTests.cs</c>.
/// </summary>
/// <remarks>
///     None of these quirks is visible to the per-block size check: every one keeps the field's width, so a reader
///     that ignored a quirk would still consume exactly Block Size bytes and silently mis-value the field. Only the
///     value tests can falsify them.
/// </remarks>
internal static class NifDecodeQuirks
{
    /// <summary>The struct whose <see cref="AgdPayloadField" /> is shorter than nif.xml declares.</summary>
    public const string AgdDataBlockStruct = "NiAGDDataBlock";

    /// <summary>The <see cref="AgdDataBlockStruct" /> payload field.</summary>
    public const string AgdPayloadField = "Data";

    /// <summary>The <see cref="AgdDataBlockStruct" /> field that holds the payload's real byte length.</summary>
    public const string AgdBlockSizeField = "Block Size";

    /// <summary>The Havok collision filter struct whose byte order depends on where it sits.</summary>
    public const string HavokFilterStruct = "HavokFilter";

    /// <summary>
    ///     The prefix of the rigid-body construction-info structs (<c>bhkRigidBodyCInfo550_660</c>,
    ///     <c>bhkRigidBodyCInfo2010</c>, <c>bhkRigidBodyCInfo2014</c>) inside which a HavokFilter is raw little-endian.
    /// </summary>
    public const string RigidBodyCInfoPrefix = "bhkRigidBodyCInfo";

    /// <summary>
    ///     Quirk 1, BSPartFlag: stored LITTLE-endian even inside big-endian (Xbox 360) files. Bethesda's Xbox tools
    ///     wrote the flag as the PC byte pair; reading it big-endian swaps PF_EDITOR_VISIBLE (bit 0) with
    ///     PF_START_NET_BONESET (bit 8). Source: the converter's measured opt-out,
    ///     <c>NifScalarConverter.BytePackedBitflagTypes</c> (Conversion/NifScalarConverter.cs:26-36, with the
    ///     explanation at :139-149); the retail fixture layout is reproduced by
    ///     <c>tests/.../Helpers/BigEndianNifBuilder.cs:36-40</c>. The sibling <c>Body Part</c> ushort stays big-endian.
    /// </summary>
    /// <param name="enumTypeName">The enum, bitflags or bitfield type being read.</param>
    public static bool IsLittleEndianInBigEndianFiles(string enumTypeName)
    {
        return string.Equals(enumTypeName, "BSPartFlag", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Quirk 2, single-byte-field fixed-size structs (ByteColor4, ByteColor4BGRA, ByteVector4, UDecVector4 in the
    ///     embedded nif.xml): stored as ONE big-endian unit in big-endian files, so the bytes on disk are the
    ///     little-endian layout reversed (a ByteColor4 reads a, b, g, r). The decoder reads the unit in file order,
    ///     lays it out little-endian and splits it into the declared fields. In little-endian files this is the
    ///     identity. The rule mirrors the converter's exactly: a declared size of 2, 4 or 8 and every field of
    ///     schema size at most 1 (unknown sizes count as 0). Source: <c>NifScalarConverter.TryBulkSwapFixedSizeStruct</c>
    ///     (Conversion/NifScalarConverter.cs:160-202).
    /// </summary>
    public static bool IsBigEndianUnitStruct(NifSchema schema, NifStructDef structDef)
    {
        if (structDef.FixedSize is not (2 or 4 or 8))
        {
            return false;
        }

        foreach (var field in structDef.Fields)
        {
            if ((schema.GetTypeSize(field.Type) ?? 0) > 1)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Quirk 3, nif.xml erratum: <c>NiAGDDataBlock.Data</c> is declared <c>byte[Num Data][Block Size]</c>
    ///     (nif.xml:16457-16466) but holds exactly Block Size bytes; Num Data counts the per-component Data Sizes that
    ///     precede it. Measured on retail X360 <c>BSPackedAdditionalGeometryData</c> (fxfallingrocks01.nif: Block Size
    ///     5,640 = 141 x 40 with Num Data 22; pipboyarmnpc.nif: 360 = 9 x 40), recorded by the independent probe at
    ///     <c>tools/scripts/nif_feature_probe.py:1036-1041</c>. With argument 1 (the BSPacked variant) Shader Index
    ///     and Total Size follow the payload, as nif.xml says. The measurement covers the argument-1 variant; the PC
    ///     <c>NiAdditionalGeometryData</c> (argument 0, native-only in cut 1a) takes the same reading, and the per-block
    ///     size check reports it if a file disagrees.
    /// </summary>
    public static bool IsAgdPayload(string structName, string fieldName)
    {
        return string.Equals(structName, AgdDataBlockStruct, StringComparison.Ordinal) &&
               string.Equals(fieldName, AgdPayloadField, StringComparison.Ordinal);
    }

    /// <summary>True for the HavokFilter struct (see <see cref="IsHavokFilterRawLittleEndian" />).</summary>
    public static bool IsHavokFilter(string structName)
    {
        return string.Equals(structName, HavokFilterStruct, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Quirk 4, HavokFilter (byte Layer, byte Flags, ushort Group; nif.xml:7011-7042) has two conventions in
    ///     big-endian files: at NiStream sites (the bhkWorldObject filter, bhkNiTriStripsShape filters, hkSubPartData)
    ///     the whole filter is ONE big-endian uint32, while the copy inside a <see cref="RigidBodyCInfoPrefix" />
    ///     struct is raw little-endian (the console baker never swapped it). Either way the decoder reads a 4-byte unit
    ///     and splits it little-endian. Source: <c>NifValueConverter.TryConvertStructType</c>
    ///     (Conversion/NifValueConverter.cs:602-623). Only bhk blocks carry it, and the reader decodes those
    ///     tolerantly for native state only.
    /// </summary>
    /// <param name="enclosingDefinition">The struct or block that declares the HavokFilter field.</param>
    public static bool IsHavokFilterRawLittleEndian(string enclosingDefinition)
    {
        return enclosingDefinition.StartsWith(RigidBodyCInfoPrefix, StringComparison.Ordinal);
    }
}

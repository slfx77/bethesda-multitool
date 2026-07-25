using System.Text;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Hand-authored builder for a minimal BIG-ENDIAN (Xbox 360) FNV-style NIF used by the
///     Xbox→PC conversion regression tests. This is deliberately NOT a schema-generic writer:
///     it emits one fixed block vocabulary with hand-computed layouts so the tests stay
///     independent of the production schema walker they are exercising.
///     <para>
///         <b>Version identity</b> — 20.2.0.7 (0x14020007), User Version 11, BS Version 34:
///         the FO3/FNV stream, matching the retail Xbox 360 meshes the sibling Bucket-B facts
///         convert. At this version the header has an Endian byte (0 = big-endian), a
///         BSStreamHeader (BS Version + Author/Process/Export strings, no Max Filepath below
///         BS 103), a Block Types table, per-block Type Index + Block Size arrays, a String
///         table, and a Groups count.
///     </para>
///     <para>
///         <b>Header endianness</b> — mirrors what NifParser/NifOutputWriter implement for
///         retail Xbox NIFs: the header string, binary version, User Version, Num Blocks, and
///         BS Version are LITTLE-endian even in a big-endian file; everything from Num Block
///         Types on (type names, indices, sizes, strings, groups), all block bodies, and the
///         footer are BIG-endian.
///     </para>
///     <para>
///         <b>Block list</b> (fixed; indices are the <c>*BlockIndex</c> constants):
///         NiNode root → NiTriShape (properties: NiAlphaProperty + BSShaderNoLightingProperty;
///         the shader property is required or FO3+ extraction drops the shape as a
///         non-renderable helper) → NiTriShapeData (3 verts / 1 tri / normals / 1 UV set,
///         Additional Data → the NiAdditionalGeometryData block). The BSDismemberSkinInstance
///         is deliberately NOT referenced by the shape (Skin Instance = -1): the converter
///         walks every block in the block list regardless of scene-graph reachability, and
///         keeping it unreferenced keeps the render/skinning paths out of the alpha test.
///     </para>
///     <para>
///         <b>Per-field endian quirks reproduced from retail Xbox meshes</b>:
///         BSDismemberSkinInstance partition <c>PartFlag</c> values are written in PC-native
///         (little-endian) byte order — Bethesda's Xbox tools wrote them unswapped, which is
///         exactly the quirk the converter's BSPartFlag opt-out preserves — while the sibling
///         <c>BodyPart</c> ushort is normal big-endian. The NiAGDDataBlock payload is raw
///         bytes that the converter must swap in 4-byte units (Block Size is kept a multiple
///         of 4; channel semantics beyond the swap contract are not modeled).
///     </para>
/// </summary>
internal static class BigEndianNifBuilder
{
    public const int NiNodeBlockIndex = 0;
    public const int NiTriShapeBlockIndex = 1;
    public const int NiTriShapeDataBlockIndex = 2;
    public const int NiAlphaPropertyBlockIndex = 3;
    public const int ShaderPropertyBlockIndex = 4;
    public const int DismemberBlockIndex = 5;
    public const int AdditionalGeometryDataBlockIndex = 6;

    /// <summary>NiAlphaProperty: Name(4) + Num Extra Data(4) + Controller(4) precede Flags.</summary>
    public const int AlphaFlagsOffsetInBlock = 12;

    /// <summary>Threshold byte directly follows the Flags ushort.</summary>
    public const int AlphaThresholdOffsetInBlock = 14;

    /// <summary>
    ///     BSDismemberSkinInstance: Data(4) + Skin Partition(4) + Skeleton Root(4) + Num Bones(4)
    ///     + Bones[1](4) + Num Partitions(4) precede the first (PartFlag u16, BodyPart u16) pair.
    /// </summary>
    public const int DismemberPartitionsOffsetInBlock = 24;

    /// <summary>
    ///     NiAdditionalGeometryData: Num Vertices(2) + Num Block Infos(4) + NiAGDDataStream(25:
    ///     six uints + a byte-sized flags enum) + Num Blocks(4) + Has Data(1) + Block Size(4)
    ///     + Num Blocks(4) + Block Offsets[1](4) + Num Data(4) + Data Sizes[1](4) precede the payload.
    /// </summary>
    public const int AgdPayloadOffsetInBlock = 56;

    /// <summary>Blend off (bit 0), src/dst modes 6/7, test on (bit 9), function 4 (bits 10-12).</summary>
    public const ushort DefaultAlphaFlags = 0x12EC;

    public const byte DefaultAlphaThreshold = 80;

    /// <summary>
    ///     Default partitions mirror the retail Ulysses fixture semantics: a torso partition with
    ///     PF_EDITOR_VISIBLE|PF_START_NET_BONESET (0x0101, swap-invariant), a limb partition with
    ///     only PF_EDITOR_VISIBLE (0x0001), and a gore-cap-style partition with only
    ///     PF_START_NET_BONESET (0x0100). The latter two are asymmetric byte pairs, so an
    ///     erroneous ushort swap turns each into the other. BodyPart values 300/230 are
    ///     asymmetric too, proving the regular swap still applies to the neighboring field.
    /// </summary>
    public static readonly (ushort PartFlag, ushort BodyPart)[] DefaultPartitions =
        [(0x0101, 0), (0x0001, 300), (0x0100, 230)];

    /// <summary>Ascending 16-byte pattern — every 4-byte unit changes under the AGD swap.</summary>
    public static byte[] DefaultAgdPayload()
    {
        return [.. Enumerable.Range(0, 16).Select(i => (byte)i)];
    }

    /// <summary>
    ///     Build the fixture. All three knobs default to the values the conversion regression
    ///     tests assert on; <paramref name="agdPayload" /> length must be a positive multiple
    ///     of 4 (it becomes the NiAGDDataBlock's Block Size, and the converter's 4-byte-unit
    ///     swap only engages for 4-aligned block sizes).
    /// </summary>
    public static byte[] Build(
        ushort alphaFlags = DefaultAlphaFlags,
        byte alphaThreshold = DefaultAlphaThreshold,
        (ushort PartFlag, ushort BodyPart)[]? partitions = null,
        byte[]? agdPayload = null)
    {
        partitions ??= DefaultPartitions;
        agdPayload ??= DefaultAgdPayload();
        if (agdPayload.Length == 0 || agdPayload.Length % 4 != 0)
        {
            throw new ArgumentException("AGD payload length must be a positive multiple of 4.", nameof(agdPayload));
        }

        byte[][] blocks =
        [
            BuildNiNode(),
            BuildNiTriShape(),
            BuildNiTriShapeData(),
            BuildNiAlphaProperty(alphaFlags, alphaThreshold),
            BuildShaderProperty(),
            BuildDismemberSkinInstance(partitions),
            BuildAdditionalGeometryData(agdPayload)
        ];

        string[] blockTypeNames =
        [
            "NiNode", "NiTriShape", "NiTriShapeData", "NiAlphaProperty",
            "BSShaderNoLightingProperty", "BSDismemberSkinInstance", "NiAdditionalGeometryData"
        ];

        var w = new Writer();

        // ── Header (LE segment) ──
        w.Ascii("Gamebryo File Format, Version 20.2.0.7");
        w.U8(0x0A);
        w.U32Le(0x14020007); // binary version
        w.U8(0); // endian byte: 0 = big-endian
        w.U32Le(11); // user version
        w.U32Le((uint)blocks.Length);
        w.U32Le(34); // BS version (FNV)
        w.ExportString(); // Author
        w.ExportString(); // Process Script (BS < 131)
        w.ExportString(); // Export Script (no Max Filepath below BS 103)

        // ── Header (BE segment) ──
        w.U16Be((ushort)blockTypeNames.Length);
        foreach (var name in blockTypeNames)
        {
            w.SizedStringBe(name);
        }

        for (var i = 0; i < blocks.Length; i++)
        {
            w.U16Be((ushort)i); // type index (one type per block)
        }

        foreach (var block in blocks)
        {
            w.U32Be((uint)block.Length);
        }

        // String table: [0] root node name, [1] shape name.
        w.U32Be(2);
        w.U32Be(10); // max string length
        w.SizedStringBe("SynthRoot");
        w.SizedStringBe("SynthShape");
        w.U32Be(0); // num groups

        foreach (var block in blocks)
        {
            w.Bytes(block);
        }

        // ── Footer ──
        w.U32Be(1); // num roots
        w.U32Be(NiNodeBlockIndex); // root

        return w.ToArray();
    }

    // NiObjectNET header shared by NiNode / NiTriShape / the properties:
    // Name (string-table index, -1 = unnamed), Num Extra Data List = 0, Controller = -1.
    private static void ObjectNetHeader(Writer w, int nameIndex)
    {
        w.I32Be(nameIndex);
        w.U32Be(0);
        w.I32Be(-1);
    }

    // NiAVObject fields at BS > 26: Flags is a uint (0x8000E = default visible NiNode/NiTriShape
    // flags; bit 0 clear keeps the shape out of the extractor's APP_CULLED drop), then
    // Translation, Rotation (identity), Scale, Num Properties + refs, Collision Object = -1.
    private static void AvObjectFields(Writer w, params int[] propertyRefs)
    {
        w.U32Be(0x0008000E);
        for (var i = 0; i < 3; i++)
        {
            w.F32Be(0f); // translation
        }

        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                w.F32Be(row == col ? 1f : 0f); // rotation
            }
        }

        w.F32Be(1f); // scale
        w.U32Be((uint)propertyRefs.Length);
        foreach (var propertyRef in propertyRefs)
        {
            w.I32Be(propertyRef);
        }

        w.I32Be(-1); // collision object
    }

    private static byte[] BuildNiNode()
    {
        var w = new Writer();
        ObjectNetHeader(w, 0); // "SynthRoot"
        AvObjectFields(w);
        w.U32Be(1); // num children
        w.I32Be(NiTriShapeBlockIndex);
        w.U32Be(0); // num effects
        return w.ToArray();
    }

    private static byte[] BuildNiTriShape()
    {
        var w = new Writer();
        ObjectNetHeader(w, 1); // "SynthShape"
        AvObjectFields(w, NiAlphaPropertyBlockIndex, ShaderPropertyBlockIndex);

        // NiGeometry (BS 34): Data ref, Skin Instance ref, MaterialData
        // (Num Materials = 0, Active Material = -1, Material Needs Update = 0).
        w.I32Be(NiTriShapeDataBlockIndex);
        w.I32Be(-1); // skin instance: dismember block intentionally left unreferenced
        w.U32Be(0);
        w.I32Be(-1);
        w.U8(0);
        return w.ToArray();
    }

    private static byte[] BuildNiTriShapeData()
    {
        var w = new Writer();
        w.I32Be(0); // group id
        w.U16Be(3); // num vertices
        w.U8(0); // keep flags
        w.U8(0); // compress flags

        w.U8(1); // has vertices
        w.F32Be(0f);
        w.F32Be(0f);
        w.F32Be(0f);
        w.F32Be(1f);
        w.F32Be(0f);
        w.F32Be(0f);
        w.F32Be(0f);
        w.F32Be(1f);
        w.F32Be(0f);

        w.U16Be(0x0001); // BS Data Flags: Has UV, no tangents

        w.U8(1); // has normals
        for (var i = 0; i < 3; i++)
        {
            w.F32Be(0f);
            w.F32Be(0f);
            w.F32Be(1f);
        }

        // Bounding sphere: center + radius.
        w.F32Be(0f);
        w.F32Be(0f);
        w.F32Be(0f);
        w.F32Be(1.5f);

        w.U8(0); // has vertex colors

        // One UV set × 3 vertices.
        w.F32Be(0f);
        w.F32Be(0f);
        w.F32Be(1f);
        w.F32Be(0f);
        w.F32Be(0f);
        w.F32Be(1f);

        w.U16Be(0x4000); // consistency flags: CT_STATIC
        w.I32Be(AdditionalGeometryDataBlockIndex);

        // NiTriBasedGeomData + NiTriShapeData tail.
        w.U16Be(1); // num triangles
        w.U32Be(3); // num triangle points
        w.U8(1); // has triangles
        w.U16Be(0);
        w.U16Be(1);
        w.U16Be(2);
        w.U16Be(0); // num match groups
        return w.ToArray();
    }

    private static byte[] BuildNiAlphaProperty(ushort flags, byte threshold)
    {
        var w = new Writer();
        ObjectNetHeader(w, -1);
        w.U16Be(flags);
        w.U8(threshold);
        return w.ToArray();
    }

    // BSShaderNoLightingProperty (BS 34): NiShadeProperty Flags, BSShaderProperty
    // Shader Type / Flags / Flags 2 / Environment Map Scale, BSShaderLightingProperty
    // Texture Clamp Mode, then File Name + the four BS > 26 falloff floats. Present so the
    // FO3+ extraction path treats the shape as renderable (texture-source property required).
    private static byte[] BuildShaderProperty()
    {
        var w = new Writer();
        ObjectNetHeader(w, -1);
        w.U16Be(0x0001); // shade flags: SHADING_SMOOTH
        w.U32Be(0); // shader type: SHADER_DEFAULT
        w.U32Be(0x82000000); // shader flags
        w.U32Be(0x00000001); // shader flags 2
        w.F32Be(1f); // environment map scale
        w.U32Be(3); // texture clamp mode: WRAP_S_WRAP_T
        w.SizedStringBe("synth.dds");
        w.F32Be(1f);
        w.F32Be(0f);
        w.F32Be(1f);
        w.F32Be(0f); // falloff angles/opacities
        return w.ToArray();
    }

    private static byte[] BuildDismemberSkinInstance((ushort PartFlag, ushort BodyPart)[] partitions)
    {
        var w = new Writer();
        w.I32Be(-1); // NiSkinData ref
        w.I32Be(-1); // NiSkinPartition ref
        w.I32Be(NiNodeBlockIndex); // skeleton root
        w.U32Be(1); // num bones
        w.I32Be(NiNodeBlockIndex);
        w.U32Be((uint)partitions.Length);
        foreach (var (partFlag, bodyPart) in partitions)
        {
            w.U16Le(partFlag); // PC-native order even on Xbox — the BSPartFlag quirk
            w.U16Be(bodyPart);
        }

        return w.ToArray();
    }

    private static byte[] BuildAdditionalGeometryData(byte[] payload)
    {
        var w = new Writer();
        w.U16Be(3); // num vertices
        w.U32Be(1); // num block infos

        // NiAGDDataStream: Type, Unit Size, Total Size, Stride, Block Index, Block Offset
        // (uints) + Flags (byte-sized enum).
        w.U32Be(0);
        w.U32Be(4);
        w.U32Be((uint)payload.Length);
        w.U32Be(4);
        w.U32Be(0);
        w.U32Be(0);
        w.U8(2);

        w.U32Be(1); // num blocks
        w.U8(1); // NiAGDDataBlocks.Has Data

        // NiAGDDataBlock (arg 0 — no trailing Shader Index / Total Size).
        w.U32Be((uint)payload.Length); // block size
        w.U32Be(1); // num blocks
        w.U32Be(0); // block offsets[0]
        w.U32Be(1); // num data
        w.U32Be((uint)payload.Length); // data sizes[0]
        w.Bytes(payload);
        return w.ToArray();
    }

    /// <summary>Growable byte sink with explicit-endianness scalar writers.</summary>
    private sealed class Writer
    {
        private readonly List<byte> _bytes = [];

        public void U8(byte value)
        {
            _bytes.Add(value);
        }

        public void Bytes(byte[] value)
        {
            _bytes.AddRange(value);
        }

        public void Ascii(string value)
        {
            _bytes.AddRange(Encoding.ASCII.GetBytes(value));
        }

        public void U16Be(ushort value)
        {
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)value);
        }

        public void U16Le(ushort value)
        {
            _bytes.Add((byte)value);
            _bytes.Add((byte)(value >> 8));
        }

        public void U32Be(uint value)
        {
            _bytes.Add((byte)(value >> 24));
            _bytes.Add((byte)(value >> 16));
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)value);
        }

        public void U32Le(uint value)
        {
            _bytes.Add((byte)value);
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)(value >> 16));
            _bytes.Add((byte)(value >> 24));
        }

        public void I32Be(int value)
        {
            U32Be(unchecked((uint)value));
        }

        public void F32Be(float value)
        {
            U32Be(BitConverter.SingleToUInt32Bits(value));
        }

        /// <summary>BSStreamHeader ExportString: length byte (incl. terminator) + NUL.</summary>
        public void ExportString()
        {
            U8(1);
            U8(0);
        }

        /// <summary>NIF SizedString: uint32 length + ASCII chars (no terminator).</summary>
        public void SizedStringBe(string value)
        {
            U32Be((uint)value.Length);
            Ascii(value);
        }

        public byte[] ToArray()
        {
            return [.. _bytes];
        }
    }
}
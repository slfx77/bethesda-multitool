namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Hand-laid 20.2.0.7 block prefixes shared by the block-decoder tests, transcribed from nif.xml for user version
///     11 and BS 14 to 34 (no NifSchema involved). Each method names the nif.xml definition it lays out.
/// </summary>
internal static class NifTestBlockLayouts
{
    /// <summary>The identity rotation, row by row.</summary>
    public static readonly float[] Identity = [1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f];

    /// <summary>
    ///     NiObjectNET (nif.xml:9049-9076) at 20.2.0.7: Name (string = NiFixedString index), Num Extra Data List (uint),
    ///     Extra Data List (Ref[]), Controller (Ref). Shader Type is onlyT BSLightingShaderProperty; Legacy Extra Data
    ///     and Extra Data are pre-10.0 fields.
    /// </summary>
    public static void ObjectNet(NifTestBlockWriter w, int nameIndex, int[]? extraData = null, int controller = -1)
    {
        extraData ??= [];
        w.StringIndex(nameIndex);
        w.U32((uint)extraData.Length);
        foreach (var reference in extraData)
        {
            w.Ref(reference);
        }

        w.Ref(controller);
    }

    /// <summary>
    ///     NiAVObject (nif.xml:9178-9272) at 20.2.0.7: Flags (uint when BS &gt; 26, else ushort), Translation (Vector3),
    ///     Rotation (Matrix33, nine floats), Scale (float), Num Properties + Properties (BS &lt;= 34), Collision Object
    ///     (Ref). Velocity, bounding volume and the 2.3 unknowns are pre-10.0 fields.
    /// </summary>
    public static void AvObject(
        NifTestBlockWriter w,
        uint bsVersion,
        uint flags,
        (float X, float Y, float Z) translation,
        float[] rotation,
        float scale,
        int[]? properties = null,
        int collisionObject = -1)
    {
        properties ??= [];
        if (bsVersion > 26)
        {
            w.U32(flags);
        }
        else
        {
            w.U16((ushort)flags);
        }

        w.F32s(translation.X, translation.Y, translation.Z);
        w.F32s(rotation);
        w.F32(scale);
        w.U32((uint)properties.Length);
        foreach (var property in properties)
        {
            w.Ref(property);
        }

        w.Ref(collisionObject);
    }

    /// <summary>
    ///     NiNode (nif.xml:10927-10946): Num Children + Children (Ref[]), then Num Effects + Effects (BS &lt; 130).
    /// </summary>
    public static void NodeTail(NifTestBlockWriter w, int[] children, int[]? effects = null)
    {
        effects ??= [];
        w.U32((uint)children.Length);
        foreach (var child in children)
        {
            w.Ref(child);
        }

        w.U32((uint)effects.Length);
        foreach (var effect in effects)
        {
            w.Ref(effect);
        }
    }

    /// <summary>A complete NiNode body with identity transform, no properties, no extra data.</summary>
    public static void Node(NifTestBlockWriter w, uint bsVersion, int nameIndex, int[] children)
    {
        ObjectNet(w, nameIndex);
        AvObject(w, bsVersion, 0x0E, (0f, 0f, 0f), Identity, 1f);
        NodeTail(w, children);
    }

    /// <summary>
    ///     A NiGeometry block body for BS 14 to 34 (NiTriShape, NiTriStrips; nif.xml:9886-9931): NiObjectNET, NiAVObject
    ///     with an identity transform and the given Properties, then Data (Ref, #NI_BS_LT_SSE#), Skin Instance (Ref),
    ///     Material Data (Num Materials uint 0, Active Material int -1, Material Needs Update bool since 20.2.0.7). Shader
    ///     Property and Alpha Property are BS &gt; 34 only.
    /// </summary>
    public static void GeometryShape(NifTestBlockWriter w, uint bsVersion, int nameIndex, int data,
        int skinInstance = -1, int controller = -1, uint flags = 0x0E, int[]? properties = null)
    {
        ObjectNet(w, nameIndex, controller: controller);
        AvObject(w, bsVersion, flags, (0f, 0f, 0f), Identity, 1f, properties);
        w.Ref(data);
        w.Ref(skinInstance);
        w.U32(0);
        w.I32(-1);
        w.Bool(false);
    }

    /// <summary>
    ///     The BSSegmentedTriShape tail after its NiTriShape fields (nif.xml:16409-16420): Num Segments (uint), then per
    ///     BSGeometrySegmentData below BS 130 (nif.xml:16390-16407) Flags (byte), Start Index (uint), Num Primitives (uint).
    /// </summary>
    public static void SegmentedTail(NifTestBlockWriter w, (byte Flags, uint Start, uint Count)[] segments)
    {
        w.U32((uint)segments.Length);
        foreach (var (flags, start, count) in segments)
        {
            w.U8(flags).U32(start).U32(count);
        }
    }

    /// <summary>
    ///     NiGeometryData (nif.xml:9933-10042) at 20.2.0.7 with BS &gt; 0 and BS &lt;= 34: Group ID (int), Num Vertices
    ///     (ushort), Keep Flags, Compress Flags (bytes), Has Vertices (bool) + Vertices, BS Data Flags (ushort; the #BS202#
    ///     branch, which also drops "Data Flags"; no Material CRC at BS &lt;= 34), Has Normals + Normals, then Tangents and
    ///     Bitangents when Has Normals and bit 12, Bounding Sphere (center, radius), Has Vertex Colors + Color4 colors, UV
    ///     Sets ((BS Data Flags &amp; 1) rows of Num Vertices TexCoords), Consistency Flags (ushort), Additional Data (Ref).
    /// </summary>
    public static void GeometryData(NifTestBlockWriter w, NifTestGeometryStreams streams)
    {
        w.I32(0);
        w.U16(streams.VertexCount);
        w.U8(0).U8(0);
        w.Bool(streams.HasVertices);
        if (streams.HasVertices)
        {
            w.F32s(streams.Vertices);
        }

        w.U16(streams.BsDataFlags);
        w.Bool(streams.Normals is not null);
        if (streams.Normals is not null)
        {
            w.F32s(streams.Normals);
            if (streams.Tangents is not null)
            {
                w.F32s(streams.Tangents);
                w.F32s(streams.Bitangents ?? throw new ArgumentException("Tangents need bitangents.", nameof(streams)));
            }
        }

        w.F32s(streams.BoundingSphere);
        var hasColors = streams.ColorBits is not null || streams.Colors is not null;
        w.Bool(hasColors);
        if (streams.ColorBits is not null)
        {
            foreach (var bits in streams.ColorBits)
            {
                w.U32(bits);
            }
        }
        else if (streams.Colors is not null)
        {
            w.F32s(streams.Colors);
        }

        if (streams.UvBits is not null)
        {
            foreach (var bits in streams.UvBits)
            {
                w.U32(bits);
            }
        }
        else if (streams.Uvs is not null)
        {
            w.F32s(streams.Uvs);
        }

        w.U16(streams.ConsistencyFlags);
        w.Ref(streams.AdditionalData);
    }

    /// <summary>
    ///     The NiTriShapeData tail (nif.xml:10047-10052, 12564-12592): Num Triangles (ushort, NiTriBasedGeomData), Num
    ///     Triangle Points (uint), Has Triangles (bool) + Triangles (three ushorts each), Num Match Groups (ushort) + Match
    ///     Groups (each its own ushort count, then that many ushort indices).
    /// </summary>
    public static void TriShapeDataTail(NifTestBlockWriter w, ushort[] triangleIndices, bool hasTriangles = true,
        ushort[][]? matchGroups = null)
    {
        matchGroups ??= [];
        var count = (ushort)(triangleIndices.Length / 3);
        w.U16(count);
        w.U32((uint)count * 3);
        w.Bool(hasTriangles);
        if (hasTriangles)
        {
            foreach (var index in triangleIndices)
            {
                w.U16(index);
            }
        }

        w.U16((ushort)matchGroups.Length);
        foreach (var group in matchGroups)
        {
            w.U16((ushort)group.Length);
            foreach (var index in group)
            {
                w.U16(index);
            }
        }
    }

    /// <summary>
    ///     The NiTriStripsData tail (nif.xml:10047-10052, 12598-12620): Num Triangles (ushort; written as the sum of
    ///     max(0, L - 2)), Num Strips (ushort), Strip Lengths (ushort each), Has Points (bool) + Points (each strip's
    ///     ushorts, jagged by Strip Lengths).
    /// </summary>
    public static void TriStripsDataTail(NifTestBlockWriter w, ushort[][] strips, bool hasPoints = true)
    {
        w.U16((ushort)strips.Sum(strip => Math.Max(0, strip.Length - 2)));
        w.U16((ushort)strips.Length);
        foreach (var strip in strips)
        {
            w.U16((ushort)strip.Length);
        }

        w.Bool(hasPoints);
        if (!hasPoints)
        {
            return;
        }

        foreach (var strip in strips)
        {
            foreach (var point in strip)
            {
                w.U16(point);
            }
        }
    }

    /// <summary>
    ///     NiGeomMorpherController at 20.2.0.7 (nif.xml:9494-9570): NiTimeController Next Controller (Ref), Flags
    ///     (ushort), Frequency, Phase, Start Time, Stop Time (floats), Target (Ptr); then Morpher Flags (ushort), Data
    ///     (Ref), Always Update (byte), Num Interpolators (uint) and Interpolator Weights (MorphWeight: Ref + float, since
    ///     20.1.0.3). NiInterpController's Manager Controlled and the pre-20.0.0.5 arrays are absent.
    /// </summary>
    public static void GeomMorpherController(NifTestBlockWriter w, int target, int data, int next = -1,
        int interpolators = 0)
    {
        w.Ref(next);
        w.U16(0x000C);
        w.F32s(1f, 0f, 0f, 1f);
        w.Ref(target);
        w.U16(0);
        w.Ref(data);
        w.U8(0);
        w.U32((uint)interpolators);
        for (var i = 0; i < interpolators; i++)
        {
            w.Ref(-1).F32(0f);
        }
    }

    /// <summary>
    ///     NiMorphData (nif.xml:10908-10925) with Morph (nif.xml:6938-6958) at 20.2.0.7: Num Morphs (uint), Num Vertices
    ///     (uint), Relative Targets (byte), then per morph Frame Name (string index) and Num Vertices Vector3s.
    /// </summary>
    public static void MorphData(NifTestBlockWriter w, uint numVertices, byte relativeTargets,
        params (int NameIndex, float[] Vectors)[] morphs)
    {
        w.U32((uint)morphs.Length);
        w.U32(numVertices);
        w.U8(relativeTargets);
        foreach (var (nameIndex, vectors) in morphs)
        {
            w.StringIndex(nameIndex);
            w.F32s(vectors);
        }
    }

    /// <summary>
    ///     An additional-geometry block with no channels (NiAdditionalGeometryData and BSPackedAdditionalGeometryData share
    ///     this prefix, nif.xml:16474-16495): Num Vertices (ushort), Num Block Infos (uint 0), Num Blocks (uint 0).
    /// </summary>
    public static void EmptyAdditionalGeometryData(NifTestBlockWriter w, ushort numVertices)
    {
        w.U16(numVertices);
        w.U32(0);
        w.U32(0);
    }

    /// <summary>
    ///     BSPackedAdditionalGeometryData (nif.xml:16482-16496) with one data block: Num Vertices (ushort), Num Block
    ///     Infos (uint) then per NiAGDDataStream (nif.xml:16435-16455) Type, Unit Size, Total Size (= Num Vertices x Unit
    ///     Size), Stride, Block Index, Block Offset (uints) and Flags (byte); Num Blocks (uint 1); NiAGDDataBlocks Has
    ///     Data (bool 1) then the NiAGDDataBlock (nif.xml:16457-16467): Block Size (uint, the payload length), Num Blocks
    ///     (uint 1), Block Offsets (uint 0), Num Data + Data Sizes (uints), Data (Block Size bytes: the payload is Block
    ///     Size bytes, decoder quirk 3), Shader Index and Total Size (= the stride; the arg 1 fields). Every
    ///     retail block has one data block, Block Index 0 and Flags 2; the parameters let a control break each.
    /// </summary>
    public static void PackedAdditionalGeometryData(NifTestBlockWriter w, ushort numVertices,
        (uint Type, uint UnitSize, uint Offset)[] streams, uint stride, byte[] payload, uint shaderIndex,
        uint[]? dataSizes = null, uint blockIndex = 0, byte flags = 2, uint? blockSize = null,
        uint? blockTotalSize = null)
    {
        dataSizes ??= [];
        w.U16(numVertices);
        w.U32((uint)streams.Length);
        foreach (var (type, unitSize, offset) in streams)
        {
            w.U32(type).U32(unitSize).U32(numVertices * unitSize).U32(stride).U32(blockIndex).U32(offset).U8(flags);
        }

        w.U32(1);
        w.Bool(true);
        w.U32(blockSize ?? (uint)payload.Length);
        w.U32(1).U32(0);
        w.U32((uint)dataSizes.Length);
        foreach (var size in dataSizes)
        {
            w.U32(size);
        }

        w.Bytes(payload);
        w.U32(shaderIndex).U32(blockTotalSize ?? stride);
    }

    /// <summary>
    ///     The NiShadeProperty + BSShaderProperty + BSShaderLightingProperty run (nif.xml:12095-12100, 13966-13989)
    ///     for BS &lt;= 34: Flags (ShadeFlags ushort), Shader Type (uint), Shader Flags (uint), Shader Flags 2 (uint),
    ///     Environment Map Scale (float), Texture Clamp Mode (uint).
    /// </summary>
    public static void ShaderLightingPrefix(
        NifTestBlockWriter w,
        ushort shadeFlags,
        uint shaderType,
        uint shaderFlags,
        uint shaderFlags2,
        float environmentMapScale,
        uint textureClampMode)
    {
        w.U16(shadeFlags);
        w.U32(shaderType);
        w.U32(shaderFlags);
        w.U32(shaderFlags2);
        w.F32(environmentMapScale);
        w.U32(textureClampMode);
    }

    /// <summary>
    ///     NiAlphaProperty (nif.xml:10129-10139) at 20.2.0.7: NiObjectNET, Flags (AlphaFlags, ushort), Threshold (byte).
    /// </summary>
    public static void AlphaProperty(NifTestBlockWriter w, ushort flags, byte threshold, int nameIndex = -1)
    {
        ObjectNet(w, nameIndex);
        w.U16(flags).U8(threshold);
    }

    /// <summary>
    ///     AlphaFlags (nif.xml:5449-5470) from its members: Alpha Blend bit 0, Source Blend Mode bits 1-4, Destination
    ///     Blend Mode bits 5-8, Alpha Test bit 9, Test Func bits 10-12, No Sorter bit 13.
    /// </summary>
    public static ushort AlphaFlags(bool blend, int source, int destination, bool test = false, int testFunction = 4,
        bool noSorter = false)
    {
        return (ushort)((blend ? 1 : 0) | (source << 1) | (destination << 5) | (test ? 0x200 : 0) |
                        (testFunction << 10) | (noSorter ? 0x2000 : 0));
    }

    /// <summary>
    ///     NiMaterialProperty (nif.xml:10873-10906) at 20.2.0.7: NiObjectNET, Ambient and Diffuse Color below BS 26,
    ///     Specular Color, Emissive Color, Glossiness, Alpha, then Emissive Mult above BS 21.
    /// </summary>
    public static void MaterialProperty(NifTestBlockWriter w, uint bsVersion, float[] ambient, float[] diffuse,
        float[] specular, float[] emissive, float glossiness, float alpha, float emissiveMultiplier,
        int nameIndex = -1)
    {
        ObjectNet(w, nameIndex);
        if (bsVersion < 26)
        {
            w.F32s(ambient);
            w.F32s(diffuse);
        }

        w.F32s(specular);
        w.F32s(emissive);
        w.F32(glossiness).F32(alpha);
        if (bsVersion > 21)
        {
            w.F32(emissiveMultiplier);
        }
    }

    /// <summary>NiStencilProperty from 20.1.0.3 (nif.xml:12291-12325): NiObjectNET, Flags, Stencil Ref, Stencil Mask.</summary>
    public static void StencilProperty(NifTestBlockWriter w, ushort flags, uint reference, uint mask)
    {
        ObjectNet(w, -1);
        w.U16(flags).U32(reference).U32(mask);
    }

    /// <summary>
    ///     StencilFlags (nif.xml:5482-5497): Enable bit 0, Fail bits 1-3, ZFail bits 4-6, Pass bits 7-9, Draw Mode bits
    ///     10-11, Test Func from bit 12.
    /// </summary>
    public static ushort StencilFlags(bool enable, int fail, int depthFail, int pass, int drawMode, int testFunction)
    {
        return (ushort)((enable ? 1 : 0) | (fail << 1) | (depthFail << 4) | (pass << 7) | (drawMode << 10) |
                        (testFunction << 12));
    }

    /// <summary>NiZBufferProperty from 20.1.0.3 (nif.xml:12786-12795): NiObjectNET, Flags.</summary>
    public static void ZBufferProperty(NifTestBlockWriter w, ushort flags)
    {
        ObjectNet(w, -1);
        w.U16(flags);
    }

    /// <summary>NiVertexColorProperty from 20.1.0.3 (nif.xml:12743-12756): NiObjectNET, Flags.</summary>
    public static void VertexColorProperty(NifTestBlockWriter w, ushort flags)
    {
        ObjectNet(w, -1);
        w.U16(flags);
    }

    /// <summary>NiFogProperty (nif.xml:10726-10738): NiObjectNET, Flags, Fog Depth, Fog Color.</summary>
    public static void FogProperty(NifTestBlockWriter w)
    {
        ObjectNet(w, -1);
        w.U16(1).F32(1f).F32s(0.5f, 0.5f, 0.5f);
    }

    /// <summary>
    ///     BSShaderPPLightingProperty (nif.xml:14020-14046) for BS 34 and below: NiObjectNET, the shader-lighting prefix,
    ///     Texture Set, Refraction Strength and Fire Period above BS 14, Parallax Max Passes and Scale above BS 24.
    /// </summary>
    public static void PerPixelLightingProperty(NifTestBlockWriter w, uint bsVersion, uint shaderFlags,
        uint shaderFlags2, float environmentMapScale, uint textureClampMode, int textureSet)
    {
        ObjectNet(w, -1);
        ShaderLightingPrefix(w, 1, 1, shaderFlags, shaderFlags2, environmentMapScale, textureClampMode);
        w.Ref(textureSet);
        if (bsVersion > 14)
        {
            w.F32(0f).I32(0);
        }

        if (bsVersion > 24)
        {
            w.F32(4f).F32(1f);
        }
    }

    /// <summary>BSShaderTextureSet (nif.xml:14189-14205): Num Textures, then each path as a SizedString.</summary>
    public static void TextureSet(NifTestBlockWriter w, params string[] paths)
    {
        w.U32((uint)paths.Length);
        foreach (var path in paths)
        {
            w.SizedString(path);
        }
    }

    /// <summary>
    ///     BSShaderNoLightingProperty (nif.xml:13992-14018): NiObjectNET, the shader-lighting prefix, File Name, then the
    ///     four falloff floats above BS 26.
    /// </summary>
    public static void NoLightingProperty(NifTestBlockWriter w, uint bsVersion, string fileName,
        (float StartAngle, float StopAngle, float StartOpacity, float StopOpacity) falloff,
        uint shaderFlags = 0x82000000, uint shaderFlags2 = 1, uint textureClampMode = 3)
    {
        ObjectNet(w, -1);
        ShaderLightingPrefix(w, 1, 33, shaderFlags, shaderFlags2, 1f, textureClampMode);
        w.SizedString(fileName);
        if (bsVersion > 26)
        {
            w.F32s(falloff.StartAngle, falloff.StopAngle, falloff.StartOpacity, falloff.StopOpacity);
        }
    }

    /// <summary>
    ///     WaterShaderProperty (nif.xml:14207-14210): NiObjectNET, NiShadeProperty Flags and the BSShaderProperty run,
    ///     without a Texture Clamp Mode.
    /// </summary>
    public static void WaterShaderProperty(NifTestBlockWriter w)
    {
        ObjectNet(w, -1);
        w.U16(1).U32(17).U32(0x82000000).U32(1).F32(1f);
    }

    /// <summary>
    ///     TallGrassShaderProperty (nif.xml:14266-14273): NiObjectNET, NiShadeProperty Flags and the BSShaderProperty run
    ///     (Shader Type 0, SHADER_TALL_GRASS), without a Texture Clamp Mode, then the File Name.
    /// </summary>
    public static void TallGrassProperty(NifTestBlockWriter w, string fileName)
    {
        ObjectNet(w, -1);
        w.U16(1).U32(0).U32(0x82000000).U32(1).F32(1f);
        w.SizedString(fileName);
    }

    /// <summary>
    ///     NiSourceTexture (nif.xml:12218-12265) at 20.2.0.7: NiObjectNET, Use External, File Name (string index), Pixel
    ///     Data (Ref), Format Prefs (three uints), Is Static (byte), Direct Render (bool), Persist Render Data (bool).
    /// </summary>
    public static void SourceTexture(NifTestBlockWriter w, int fileNameIndex, bool external = true,
        int pixelData = -1)
    {
        ObjectNet(w, -1);
        w.U8(external ? (byte)1 : (byte)0);
        w.StringIndex(fileNameIndex);
        w.Ref(pixelData);
        w.U32(6).U32(1).U32(3);
        w.U8(1).Bool(true).Bool(false);
    }

    /// <summary>
    ///     One TexDesc from 20.1.0.3 (nif.xml:6498-6569): Source (Ref), Flags (TexturingMapFlags: texture-coordinate set
    ///     bits 0-7, filter bits 8-11, clamp from bit 12), Has Texture Transform, then the transform when present
    ///     (Translation, Scale, Rotation, Transform Method, Center).
    /// </summary>
    public static void TexDesc(NifTestBlockWriter w, int source, int uvSet = 0, int filter = 2, int clamp = 3,
        (float Tu, float Tv, float Su, float Sv, float Rotation, uint Method, float Cu, float Cv)? transform = null)
    {
        w.Ref(source);
        w.U16((ushort)(uvSet | (filter << 8) | (clamp << 12)));
        w.Bool(transform is not null);
        if (transform is { } t)
        {
            w.F32s(t.Tu, t.Tv, t.Su, t.Sv, t.Rotation);
            w.U32(t.Method);
            w.F32s(t.Cu, t.Cv);
        }
    }

    /// <summary>
    ///     NiTexturingProperty from 20.2.0.5 (nif.xml:12466-12553) with Texture Count 7: NiObjectNET, Flags (Apply Mode
    ///     bits 1-3), Texture Count, then Has/TexDesc pairs for Base, Dark, Detail, Gloss and Glow, Has Bump Map
    ///     (Count &gt; 5, written false), Has Normal (Count &gt; 6), and Num Shader Textures 0. Each map is written by its
    ///     action, or absent when null.
    /// </summary>
    public static void TexturingProperty(NifTestBlockWriter w, int applyMode, Action<NifTestBlockWriter>? baseMap,
        Action<NifTestBlockWriter>? darkMap = null, Action<NifTestBlockWriter>? detailMap = null,
        Action<NifTestBlockWriter>? normalMap = null)
    {
        ObjectNet(w, -1);
        w.U16((ushort)(applyMode << 1));
        w.U32(7);
        Map(w, baseMap);
        Map(w, darkMap);
        Map(w, detailMap);
        Map(w, null); // Gloss
        Map(w, null); // Glow
        w.Bool(false); // Has Bump Map Texture (Texture Count > 5)
        Map(w, normalMap); // Has Normal Texture (Texture Count > 6, since 20.2.0.5)
        w.U32(0); // Num Shader Textures
    }

    /// <summary>
    ///     NiTransform (nif.xml: Rotation Matrix33, Translation Vector3, Scale float): nine rotation floats row by row,
    ///     the translation, then the scale.
    /// </summary>
    public static void Transform(NifTestBlockWriter w, float[] rotation, (float X, float Y, float Z) translation,
        float scale)
    {
        w.F32s(rotation);
        w.F32s(translation.X, translation.Y, translation.Z);
        w.F32(scale);
    }

    /// <summary>
    ///     NiSkinInstance at 20.2.0.7 (nif.xml:12128-12150): Data (Ref), Skin Partition (Ref, since 10.1.0.101), Skeleton
    ///     Root (Ptr), Num Bones (uint), Bones (Ptr each).
    /// </summary>
    public static void SkinInstance(NifTestBlockWriter w, int data, int partition, int skeletonRoot, int[] bones)
    {
        w.Ref(data).Ref(partition).Ref(skeletonRoot);
        w.U32((uint)bones.Length);
        foreach (var bone in bones)
        {
            w.Ref(bone);
        }
    }

    /// <summary>
    ///     The BSDismemberSkinInstance tail after its NiSkinInstance fields (nif.xml:16084-16090, BodyPartList
    ///     7724-7731): Num Partitions (uint), then per partition Part Flag (BSPartFlag, written LITTLE-endian in every
    ///     file, the retail quirk) and Body Part (ushort, body order).
    /// </summary>
    public static void DismemberTail(NifTestBlockWriter w, (ushort PartFlag, ushort BodyPart)[] parts)
    {
        w.U32((uint)parts.Length);
        foreach (var (partFlag, bodyPart) in parts)
        {
            w.U16Le(partFlag);
            w.U16(bodyPart);
        }
    }

    /// <summary>
    ///     NiSkinData at 20.2.0.7 (nif.xml:12102-12126): the overall Skin Transform (NiTransform), Num Bones (uint), Has
    ///     Vertex Weights (bool, since 4.2.1.0; the Skin Partition link is until 10.1.0.0 only), then per bone a BoneData
    ///     (nif.xml:6986-7009): Skin Transform, Bounding Sphere (center, radius), Num Vertices (ushort), and, only when Has
    ///     Vertex Weights is set, that many (Index ushort, Weight float) pairs.
    /// </summary>
    public static void SkinData(NifTestBlockWriter w, float[] overallRotation,
        (float X, float Y, float Z) overallTranslation, float overallScale, bool hasVertexWeights,
        NifTestSkinBone[] bones)
    {
        Transform(w, overallRotation, overallTranslation, overallScale);
        w.U32((uint)bones.Length);
        w.Bool(hasVertexWeights);
        foreach (var bone in bones)
        {
            Transform(w, bone.Rotation, bone.Translation, bone.Scale);
            w.F32s(bone.BoundingSphere);
            w.U16((ushort)bone.Weights.Length);
            if (!hasVertexWeights)
            {
                continue;
            }

            foreach (var (vertex, weight) in bone.Weights)
            {
                w.U16(vertex).F32(weight);
            }
        }
    }

    /// <summary>
    ///     NiSkinPartition for BS 34 and below (nif.xml:12176-12194, SkinPartition 6681-6781): Num Partitions (uint), then
    ///     per partition Num Vertices, Num Triangles (the kept count nif.xml calculates: strip points minus two, or the
    ///     triangle count), Num Bones, Num Strips, Num Weights Per Vertex (ushorts), Bones, Has Vertex Map + Vertex Map, Has
    ///     Vertex Weights + Vertex Weights, Strip Lengths, Has Faces + Strips or Triangles, Has Bone Indices + Bone
    ///     Indices. LOD Level and Global VB are BS &gt; 34 only.
    /// </summary>
    public static void SkinPartition(NifTestBlockWriter w, NifTestSkinPartition[] partitions)
    {
        w.U32((uint)partitions.Length);
        foreach (var partition in partitions)
        {
            var strips = partition.Strips ?? Array.Empty<ushort[]>();
            var triangles = partition.Strips is null
                ? partition.Triangles ?? Array.Empty<ushort>()
                : Array.Empty<ushort>();
            var triangleCount = partition.Strips is null
                ? triangles.Length / 3
                : strips.Sum(strip => Math.Max(0, strip.Length - 2));
            w.U16(partition.VertexCount);
            w.U16((ushort)triangleCount);
            w.U16((ushort)partition.Bones.Length);
            w.U16((ushort)strips.Length);
            w.U16(partition.WeightsPerVertex);
            foreach (var bone in partition.Bones)
            {
                w.U16(bone);
            }

            w.Bool(partition.VertexMap is not null);
            foreach (var vertex in partition.VertexMap ?? Array.Empty<ushort>())
            {
                w.U16(vertex);
            }

            w.Bool(partition.Weights is not null);
            foreach (var row in partition.Weights ?? Array.Empty<float[]>())
            {
                w.F32s(row);
            }

            foreach (var strip in strips)
            {
                w.U16((ushort)strip.Length);
            }

            var hasFaces = strips.Length > 0 || triangles.Length > 0;
            w.Bool(hasFaces);
            if (hasFaces)
            {
                foreach (var point in strips.SelectMany(strip => strip))
                {
                    w.U16(point);
                }

                foreach (var index in triangles)
                {
                    w.U16(index);
                }
            }

            w.Bool(partition.BoneIndices is not null);
            foreach (var row in partition.BoneIndices ?? Array.Empty<byte[]>())
            {
                w.Bytes(row);
            }
        }
    }

    /// <summary>
    ///     A complete NiNode-subclass body: NiObjectNET, NiAVObject with the given flags and transform, the NiNode tail,
    ///     then the subclass fields <paramref name="tail" /> writes.
    /// </summary>
    public static void NodeWithTail(NifTestBlockWriter w, uint bsVersion, int nameIndex, int[] children,
        Action<NifTestBlockWriter> tail, uint flags = 0x0E, (float X, float Y, float Z)? translation = null,
        float[]? rotation = null, float scale = 1f, int controller = -1)
    {
        ObjectNet(w, nameIndex, controller: controller);
        AvObject(w, bsVersion, flags, translation ?? (0f, 0f, 0f), rotation ?? Identity, scale);
        NodeTail(w, children);
        tail(w);
    }

    /// <summary>NiSwitchNode's own fields from 10.1.0.0 (nif.xml:11021-11028): Switch Node Flags (ushort), Index (uint).</summary>
    public static void SwitchTail(NifTestBlockWriter w, ushort flags, uint index)
    {
        w.U16(flags).U32(index);
    }

    /// <summary>
    ///     NiLODNode's fields from 10.1.0.0 (nif.xml:11030-11040): the NiSwitchNode fields, then LOD Level Data (Ref);
    ///     LOD Center and LOD Levels are until 10.0.1.0 only.
    /// </summary>
    public static void LodTail(NifTestBlockWriter w, ushort flags, uint index, int lodData)
    {
        SwitchTail(w, flags, index);
        w.Ref(lodData);
    }

    /// <summary>
    ///     NiRangeLODData (nif.xml:12063-12071): LOD Center (Vector3), Num LOD Levels (uint), then per level Near Extent and
    ///     Far Extent (floats; LODRange's Unknown Ints are until 3.1).
    /// </summary>
    public static void RangeLodData(NifTestBlockWriter w, (float X, float Y, float Z) center,
        (float Near, float Far)[] levels)
    {
        w.F32s(center.X, center.Y, center.Z);
        w.U32((uint)levels.Length);
        foreach (var (near, far) in levels)
        {
            w.F32(near).F32(far);
        }
    }

    /// <summary>
    ///     NiScreenLODData (nif.xml:12073-12083): Bounding Sphere and World Bounding Sphere (NiBound: center, radius), Num
    ///     Proportions (uint), Proportion Levels (floats).
    /// </summary>
    public static void ScreenLodData(NifTestBlockWriter w, float[] proportions)
    {
        w.F32s(0f, 0f, 0f, 1f);
        w.F32s(0f, 0f, 0f, 1f);
        w.U32((uint)proportions.Length);
        w.F32s(proportions);
    }

    /// <summary>NiBillboardNode's own field from 10.1.0.0 (nif.xml:10988-10999): Billboard Mode (ushort).</summary>
    public static void BillboardTail(NifTestBlockWriter w, ushort mode)
    {
        w.U16(mode);
    }

    /// <summary>
    ///     NiDefaultAVObjectPalette (nif.xml:10660-10675): Scene (Ptr), Num Objs (uint), then per object an AVObject
    ///     (nif.xml:6165-6175): Name (SizedString) and AV Object (Ptr).
    /// </summary>
    public static void DefaultAvObjectPalette(NifTestBlockWriter w, int scene, (string Name, int Target)[] objects)
    {
        w.Ref(scene);
        w.U32((uint)objects.Length);
        foreach (var (name, target) in objects)
        {
            w.SizedString(name);
            w.Ref(target);
        }
    }

    /// <summary>
    ///     NiControllerManager at 20.2.0.7 (nif.xml:10566-10581): the NiTimeController fields (Next Controller Ref, Flags
    ///     ushort, Frequency, Phase, Start Time, Stop Time floats, Target Ptr), then Cumulative (bool), Num Controller
    ///     Sequences (uint) with no sequences, and Object Palette (Ref).
    /// </summary>
    public static void ControllerManager(NifTestBlockWriter w, int target, int objectPalette)
    {
        w.Ref(-1);
        w.U16(0x000C);
        w.F32s(1f, 0f, 0f, 0f);
        w.Ref(target);
        w.Bool(false);
        w.U32(0);
        w.Ref(objectPalette);
    }

    private static void Map(NifTestBlockWriter w, Action<NifTestBlockWriter>? map)
    {
        w.Bool(map is not null);
        map?.Invoke(w);
    }
}

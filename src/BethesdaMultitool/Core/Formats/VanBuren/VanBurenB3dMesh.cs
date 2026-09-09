using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace BethesdaMultitool.Core.Formats.VanBuren;

/// <summary>Which of the two vertex encodings a <see cref="VanBurenB3DMesh" /> was stored in.</summary>
internal enum VanBurenB3dMeshForm
{
    /// <summary>
    ///     The "converted" (pre-built) form: a packed D3D vertex buffer plus u16 index lists per
    ///     group. Read by the version-1 branch of <c>G3D_Mesh::ReadB3D</c> (<c>FUN_004dc330</c>).
    /// </summary>
    Converted,

    /// <summary>
    ///     The "source" (exporter) form: a vertex list (<c>FUN_004c9fb0</c>) and a triangle-group
    ///     list (<c>FUN_004d9c90</c>). Read by the version-0 / unflagged branch of
    ///     <c>G3D_Mesh::ReadB3D</c>, the one that logs "Version is %d but G3D_Mesh is not converted!".
    /// </summary>
    Source
}

/// <summary>One vertex: position, unit normal, RGBA colour (0..1), two UV sets.</summary>
internal readonly record struct VanBurenB3DVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector4 Colour,
    Vector2 TexCoord,
    Vector2 LightmapCoord);

/// <summary>One skinning influence: an index into <see cref="VanBurenB3DFile.Bones" /> and its weight.</summary>
internal readonly record struct VanBurenB3DBoneWeight(int Bone, float Weight);

/// <summary>
///     A scene-level named transform, read by scene token <c>0x0E</c> (<c>FUN_004d09d0</c> →
///     <c>G3D_Transform</c> reader <c>FUN_004d0b40</c>). ⚑ These are the SKELETON: a critter payload
///     carries ~27 of them named <c>Root</c>, <c>Spine_1</c>, <c>Neck</c>, <c>Jaw</c>, <c>L Clavicle</c>…,
///     and every bone index a skinned mesh stores is below its payload's count (511/511 retail).
///     Rotation is an axis + an angle in DEGREES (<c>FUN_004c98b0</c>: the angle is scaled by a
///     runtime constant / 720 and fed to sin/cos as a half angle — 2π is the only constant that
///     makes that a half angle, and it is inferred, not read: <c>DAT_00708048</c> is BSS). Measured
///     on all 13,300 retail rotations: every angle lies in [-180, 180] and every axis is unit length
///     (13,300/13,300); 180 is the mode (2,239 within 5° of it), the rest spread over small angles
///     (the 10° bin holds 2,711, the 0° bin 1,802) — 90 is NOT a cluster (617). A value of 180 in
///     radians would be 28 turns, which is what a degree reading rules out.
/// </summary>
internal sealed record VanBurenB3DBone(
    string Name,
    byte Flags,
    Vector3 Translation,
    Vector3 RotationAxis,
    float RotationDegrees,
    Vector3 Scale);

/// <summary>
///     A scene-level material (scene token <c>7</c>, <c>FUN_004d16a0</c>): a shader name such as
///     <c>BASE_2X</c>, the material name, four RGBA colours, two scalars, a blend state
///     (<c>OPAQUE</c>/<c>ALPHABLEND</c>/<c>ADD</c>) and a surface sound (<c>SILENT</c>/<c>SAND</c>…).
///     The trailing flag word gates six optional blocks that are consumed but whose meaning is not
///     established.
/// </summary>
internal sealed record VanBurenB3DMaterial(
    string Shader,
    string Name,
    IReadOnlyList<Vector4> Colours,
    float ScalarA,
    float ScalarB,
    string BlendState,
    string SurfaceSound,
    uint Flags);

/// <summary>
///     One triangle group of a mesh. <see cref="Indices" /> is a triangle LIST into the mesh's
///     vertices (every retail count is a multiple of 3: 6,944/6,944 source groups, and the
///     converted form stores <c>indexCount / 3</c> beside every one of its 1,449). <see cref="Textures" /> are
///     the <c>.tga</c> names the group references — carried as authored; the build's image payloads
///     are nameless, so nothing here resolves them.
/// </summary>
internal sealed record VanBurenB3DGroup(
    string Name,
    string Shader,
    string BlendState,
    string SurfaceSound,
    IReadOnlyList<string> Textures,
    int[] Indices)
{
    /// <summary>Triangle count.</summary>
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
///     One mesh: the <c>0x0F</c> node attribute of a B3D scene, decoded to vertices and triangle
///     groups by the two branches of <c>G3D_Mesh::ReadB3D</c>.
/// </summary>
internal sealed class VanBurenB3DMesh
{
    internal VanBurenB3DMesh(
        string nodeName,
        string attributeName,
        VanBurenB3dMeshForm form,
        IReadOnlyList<VanBurenB3DVertex> vertices,
        IReadOnlyList<VanBurenB3DGroup> groups,
        IReadOnlyList<VanBurenB3DBoneWeight[]>? boneWeights,
        Vector3? declaredMin,
        Vector3? declaredMax)
    {
        NodeName = nodeName;
        AttributeName = attributeName;
        Form = form;
        Vertices = vertices;
        Groups = groups;
        BoneWeights = boneWeights;
        DeclaredBoundsMin = declaredMin;
        DeclaredBoundsMax = declaredMax;
    }

    /// <summary>The name of the node carrying this mesh (the node after <c>Scene Root</c>).</summary>
    public string NodeName { get; }

    /// <summary>The attribute's own name string (empty on every retail mesh).</summary>
    public string AttributeName { get; }

    public VanBurenB3dMeshForm Form { get; }

    public IReadOnlyList<VanBurenB3DVertex> Vertices { get; }

    public IReadOnlyList<VanBurenB3DGroup> Groups { get; }

    /// <summary>
    ///     Per-vertex influences, or null when the mesh is not skinned. Converted meshes store four
    ///     streams of <c>(u32 bone, f32 weight)</c> with <c>0xFFFFFFFF</c> marking an unused slot
    ///     (the reader must find exactly 4 streams or logs "Bone counts different"); source meshes
    ///     store a count per vertex. Unused slots are dropped here.
    /// </summary>
    public IReadOnlyList<VanBurenB3DBoneWeight[]>? BoneWeights { get; }

    public bool Skinned => BoneWeights is not null;

    /// <summary>The bounding box a converted mesh declares (6 floats after its vertex buffer); null on the source form.</summary>
    public Vector3? DeclaredBoundsMin { get; }

    public Vector3? DeclaredBoundsMax { get; }

    public int TriangleCount => Groups.Sum(g => g.TriangleCount);

    /// <summary>Axis-aligned bounds computed from the vertices (zero when there are none).</summary>
    public (Vector3 Min, Vector3 Max) ComputeBounds()
    {
        if (Vertices.Count == 0)
        {
            return (Vector3.Zero, Vector3.Zero);
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in Vertices)
        {
            min = Vector3.Min(min, v.Position);
            max = Vector3.Max(max, v.Position);
        }

        return (min, max);
    }
}

/// <summary>
///     A Van Buren <c>B3D</c> payload decoded to geometry. Original RE 2026-09-08 from the game's own
///     loader in <c>F3.exe</c> (addresses below) plus exact tiling on the retail data; the only
///     community reference is GPL and none of it was read.
///     <para>
///         <b>The container is a TOKEN STREAM</b>, walked by <c>FUN_004de680</c> (the in-memory
///         variant of the scene reader; <c>FUN_004de120</c> is the <c>FILE*</c> twin). The 8-byte
///         signature <c>"B3D 1.1 "</c> is compared against the string at <c>DAT_006fb360</c>
///         ("Not a B3D" otherwise), then one byte at a time selects:
///         <c>01</c> EOF · <c>03</c>/<c>04</c> a float · <c>05</c> a string · <c>06</c> three floats ·
///         <c>07</c> a material (<c>FUN_004d16a0</c>) · <c>08</c>/<c>09</c> a light header
///         (<c>FUN_004d1180</c>) · <c>0A</c> a node (<c>G3D_Node</c> vtable slot 3 = <c>FUN_004dabd0</c>) ·
///         <c>0E</c> a named transform = a BONE · <c>16</c> a dword · <c>17</c> water · <c>1C</c> one
///         flag byte. Anything else is "invalid token" in the engine too.
///         Every string is a u16 length + bytes (<c>FUN_004d0510</c>). Retail uses only
///         <c>01 03 04 05 07 08 0A 0E 16 1C</c> at scene level (one each of <c>03 04 05 16 0A</c> per
///         payload; 8,393 materials; 4,392 token-<c>08</c> light headers; 13,740 bones; never
///         <c>06</c>, <c>09</c> or <c>17</c>).
///     </para>
///     <para>
///         <b>A node</b> (<c>FUN_004dabd0</c>) loops on: <c>0A</c> child node · <c>0B</c> end · <c>0C</c>
///         name · <c>0D</c> transform · <c>0F</c> MESH (<c>G3D_Mesh</c> vtable slot 3 =
///         <c>FUN_004dc330</c> = <c>ReadB3D</c>) · <c>10</c>–<c>14</c> lights/camera/emitter ·
///         <c>15</c> lightmap surface · <c>17</c> water · <c>18</c> line system · <c>1A</c> water tile ·
///         <c>1B</c> occlusion zones — the classes were read off each constructor's RTTI. ⚠ The
///         readers for <c>11</c> (80 bytes after the name), <c>13</c> (12), <c>14</c> (one string),
///         <c>15</c> (two strings), <c>17</c> (two strings + 28 bytes) and <c>1A</c> (a mesh + 16 bytes)
///         are sized from the decompile alone — no retail payload carries any of them.
///         Retail uses only <c>0A 0B 0C 0F 1B</c> inside nodes (88 occlusion-zone lists, no
///         <c>0D</c> transform anywhere): every payload is exactly <c>Scene Root</c> → one child node
///         holding one mesh (3,912/3,912).
///     </para>
///     <para>
///         ⚑⚑ <b>Two mesh encodings, chosen by the scene's <c>1C</c> flag.</b> <c>ReadB3D</c> tests
///         <c>**(owner+0x104)</c>: when set it reads a VERSION byte — <c>1</c> = the converted form
///         (packed vertex buffer + u16 indices), <c>0</c> = the source form after logging "Version is
///         %d but G3D_Mesh is not converted!" — and when clear it reads the source form with NO version
///         byte at all. On the retail data the <c>1C</c> token is present (value 1) on exactly the
///         3,808 payloads whose meshes carry a version byte (881 read 1, 2,927 read 0) and absent on
///         the 104 (Props 89, Effects 13, Items 2) that do not; forcing either reading onto the other
///         population tiles 0 of them.
///         ⚠ The mesh's first byte alone is NOT the discriminator: <c>Props.grp</c> entry 29
///         (<c>PS_Mines1{23}Rocks_Medium2</c>, unflagged) has a source-form vertex count of 513 =
///         <c>0x201</c>, whose first byte <c>0x01</c> reads as "version 1" — a first-byte rule takes
///         the converted branch there and fails to tile, which is how the flag rule was found.
///     </para>
///     <para>
///         <b>Converted vertex</b>: <c>vertexCount</c>, <c>vertexSize</c>, then the buffer. Sizes
///         accepted by <c>Gfx_G3D_Mesh::ExportGfxModelMesh</c> (<c>FUN_005255c0</c>) are 0x24 → FVF
///         0x152 (XYZ|NORMAL|DIFFUSE|TEX1) and 0x2C → FVF 0x252 (XYZ|NORMAL|DIFFUSE|TEX2): position 3f,
///         normal 3f, D3DCOLOR, uv0 2f[, uv1 2f]. Retail is 44 bytes on 881/881. Then 6 floats
///         (bounds), a skin flag, on set a stream count that must be 4 and 4 × count × 8 bytes, then
///         the group table, per group a name string after all groups, <c>count</c> u32 index counts,
///         <c>count</c> u32 triangle counts, and the u16 index lists.
///     </para>
///     <para>
///         <b>Source vertex</b> (<c>FUN_004c9fb0</c>): position 3f, normal 3f, colour 4f, u32 uvCount ×
///         2f, u32 weightCount × (u32 bone, f32 weight). <b>Source group</b> (<c>FUN_004d9c90</c> →
///         <c>FUN_004d9a80</c>): u32 id, name, u32 n × string (the textures), u32 m × string (m = 0 on
///         all 6,944 retail groups), one byte (stored as <c>byte − 0xC9</c>; 201 on all 6,944), u32
///         count × u32 vertex indices, count × bytes. Every source group's name matches a scene
///         material's (6,944/6,944), which is where its shader/blend/sound come from.
///     </para>
///     <para>
///         ⚑ <b>Tiling</b>: 3,912 of the 3,915 retail payloads are consumed to the byte with every
///         count satisfied. ⛔ <b>Three are REFUSED</b> — <c>Critters.grp</c> entries 1322 (<c>CR_Badger</c>),
///         1325 (<c>CR_Bear</c>) and 1327 (<c>CR_Sheep</c>): they carry the same <c>"B3D 1.1 "</c>
///         signature but a RENUMBERED token table — they open <c>35 01</c> where the others open
///         <c>1C 01</c>, write the dword token as <c>2F</c> (not <c>16</c>), the material as <c>0F</c>
///         (not <c>07</c>), end <c>14 14 01</c> (not <c>0B 0B 01</c>), and their material block differs
///         too (it names its <c>.tga</c> twice, which the retail layout never does). <c>FUN_004de680</c>
///         has no case <c>0x35</c>, so this build of <c>F3.exe</c> would throw "invalid token" on them
///         exactly as this reader does; their table was NOT inferred from three files. They are
///         reported as refused by <c>classic mesh info</c>, never silently dropped. Census: 881
///         converted (Critters 453, Items 129, Props 298, Tiles 1) + 3,031 source (Tiles 2,927,
///         Props 89, Effects 13, Items 2); 1,275,078 vertices, 816,372 triangles, 8,393 groups.
///         Normals are unit length on 1,275,078/1,275,078 vertices, lightmap UVs are inside [0,1] on
///         327,402/327,402 (uv0 is not: 1,009,034/1,255,465 — tiling textures), bone weights sum to
///         1 on 195,529/195,529 weighted vertices (186,811 in the 511 skinned converted meshes,
///         8,718 in the 6 skinned source meshes) and every stored bone index is below its payload's
///         bone count (248,126/248,126), and every converted declared bounding box equals the box
///         computed from its vertices (881/881).
///         ⚠ An earlier draft of these figures (1,274,565 / 816,098 / 8,392 / "13,887 source
///         groups") came from a walk that chose the form by the mesh's first byte — it refused
///         Props #29 and double-counted source groups in its discrimination pass.
///     </para>
///     <para>
///         ⚑ Up axis: Y. Converted meshes have a Y minimum ≥ 0 on 715/881 (they stand on the floor)
///         against 10/881 for X and 82/881 for Z.
///         ⚑⚑ <b>The frame is LEFT-handed, declared by the file and read by the engine</b>: the token
///         <c>05</c> string is <c>LEFT_HANDED</c> on 3,912/3,912, and <c>FUN_00529490</c> compares that
///         string (scene +0xA4) against <c>"LEFT_HANDED"</c> at <c>0x671f54</c>, negating the node
///         translation's third component and the rotation axis only when it does NOT match. The
///         geometry agrees: vertices weighted to <c>L …</c> bones lie at −X on 135/135 skinned meshes
///         that have both sides, the 15 bodies whose head is ≥ 0.5 units from the pelvis along Z all
///         face +Z (bipeds are stacked and give no reading), and <c>cross(b−a, c−a) · n</c> is &gt; 0
///         on 814,750 of 816,372 triangles (1,526 &lt; 0, 96 degenerate; the majority on 3,912/3,912
///         meshes) — clockwise from outside in that left-handed frame, Direct3D's default front.
///         Vertices are exposed exactly as stored; <see cref="VanBurenB3DGlbExporter" /> does the
///         mirror. Node transforms never occur on retail meshes (0 <c>0D</c> tokens).
///     </para>
///     <para>
///         ⛔ <b>B3D is not the build's only geometry</b>: 547 payloads are Granny 2 files, decoded
///         by <see cref="VanBurenGrannyFile" /> / <see cref="VanBurenGrannyCatalog" /> — the build's
///         animation clips and skeletons; the visible character/prop/tile geometry is what this
///         reader decodes.
///     </para>
/// </summary>
internal sealed class VanBurenB3DFile
{
    /// <summary>The 8-byte signature.</summary>
    public const string Signature = "B3D 1.1 ";

    private const int MaxNodeDepth = 64;

    private VanBurenB3DFile(
        string name,
        bool versioned,
        IReadOnlyList<string> nodeNames,
        IReadOnlyList<VanBurenB3DMaterial> materials,
        IReadOnlyList<VanBurenB3DBone> bones,
        IReadOnlyList<VanBurenB3DMesh> meshes,
        int lightCount,
        float headerFloatA,
        float headerFloatB,
        string headerString,
        uint headerDword)
    {
        Name = name;
        Versioned = versioned;
        NodeNames = nodeNames;
        Materials = materials;
        Bones = bones;
        Meshes = meshes;
        LightCount = lightCount;
        HeaderFloatA = headerFloatA;
        HeaderFloatB = headerFloatB;
        HeaderString = headerString;
        HeaderDword = headerDword;
    }

    /// <summary>Source label, for messages.</summary>
    public string Name { get; }

    /// <summary>True when the scene carried the <c>0x1C</c> flag, i.e. its meshes carry a version byte.</summary>
    public bool Versioned { get; }

    /// <summary>Every node name in stream order; <c>Scene Root</c> then the mesh node on retail.</summary>
    public IReadOnlyList<string> NodeNames { get; }

    public IReadOnlyList<VanBurenB3DMaterial> Materials { get; }

    /// <summary>The skeleton (scene token <c>0x0E</c> entries), in index order.</summary>
    public IReadOnlyList<VanBurenB3DBone> Bones { get; }

    public IReadOnlyList<VanBurenB3DMesh> Meshes { get; }

    /// <summary>Scene-level light headers (tokens 8/9), counted only.</summary>
    public int LightCount { get; }

    /// <summary>Token <c>03</c> (stored at scene +0x80); 1.0 on 3,912/3,912 retail payloads, meaning not established.</summary>
    public float HeaderFloatA { get; }

    /// <summary>Token <c>04</c> (stored at scene +0x88, engine default 1024, reciprocal cached); 1024.0 on 3,912/3,912 retail payloads, meaning not established.</summary>
    public float HeaderFloatB { get; }

    /// <summary>
    ///     Token <c>05</c> (stored at scene +0xA4): the scene's handedness declaration, <c>LEFT_HANDED</c>
    ///     on every retail payload (3,912/3,912). <c>FUN_00529490</c> compares it against
    ///     <c>"LEFT_HANDED"</c> and applies a sign correction to node transforms only when it differs.
    /// </summary>
    public string HeaderString { get; }

    /// <summary>Token <c>16</c> (stored at scene +0xC0): <c>0xFFFFFFFF</c> on 3,480 retail payloads, 7 on the 104 unflagged ones, 12 other small values; meaning not established.</summary>
    public uint HeaderDword { get; }

    /// <summary>The mesh's authored name: the node after <c>Scene Root</c>, or null when the shape differs.</summary>
    public string? MeshName =>
        NodeNames.Count == 2 && string.Equals(NodeNames[0], VanBurenMesh.SceneRoot, StringComparison.Ordinal)
            ? NodeNames[1]
            : null;

    /// <summary>Content probe: the signature.</summary>
    public static bool IsB3d(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length > Signature.Length &&
               bytes[..Signature.Length].SequenceEqual(Encoding.ASCII.GetBytes(Signature));
    }

    /// <summary>Decodes a payload, throwing when it is not a B3D or does not tile.</summary>
    public static VanBurenB3DFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var file, out var error))
        {
            throw new InvalidDataException(error);
        }

        return file;
    }

    /// <summary>Decodes a payload, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, string name, out VanBurenB3DFile file, out string error)
    {
        file = null!;
        if (!IsB3d(bytes))
        {
            error = $"{name}: does not open with the '{Signature}' signature.";
            return false;
        }

        try
        {
            file = Read(bytes, name);
            error = string.Empty;
            return true;
        }
        catch (InvalidDataException e)
        {
            error = e.Message;
            return false;
        }
    }

    private static VanBurenB3DFile Read(ReadOnlySpan<byte> bytes, string name)
    {
        var cursor = new Cursor(bytes, Signature.Length, name);
        var scene = new SceneState();

        // FUN_004de680: the scene loop. A first pass over the retail payloads showed the 0x1C flag
        // always precedes the node, so the flag is known before any mesh is read — as the engine's
        // owner+0x104 is.
        while (true)
        {
            var token = cursor.U8();
            switch (token)
            {
                case 0x01:
                    if (cursor.Remaining != 0)
                    {
                        throw cursor.Error($"{cursor.Remaining} bytes after the EOF token");
                    }

                    return new VanBurenB3DFile(
                        name, scene.Versioned, scene.NodeNames, scene.Materials, scene.Bones, scene.Meshes,
                        scene.LightCount, scene.FloatA, scene.FloatB, scene.String, scene.Dword);
                case 0x03:
                    scene.FloatA = cursor.F32();
                    break;
                case 0x04:
                    scene.FloatB = cursor.F32();
                    break;
                case 0x05:
                    scene.String = cursor.Str();
                    break;
                case 0x06:
                    cursor.Skip(12); // FUN_004c8970: three floats
                    break;
                case 0x07:
                    scene.Materials.Add(ReadMaterial(ref cursor));
                    break;
                case 0x08:
                case 0x09:
                    cursor.U8(); // FUN_004d1180: flag bits
                    cursor.Str(); // name (FUN_004d0690)
                    cursor.Str();
                    cursor.U32();
                    cursor.U32();
                    scene.LightCount++;
                    break;
                case 0x0A:
                    ReadNode(ref cursor, scene, 0);
                    break;
                case 0x0E:
                    scene.Bones.Add(ReadBone(ref cursor));
                    break;
                case 0x16:
                    scene.Dword = cursor.U32();
                    break;
                case 0x17:
                    SkipWater(ref cursor);
                    break;
                case 0x1C:
                    scene.Versioned = cursor.U8() != 0;
                    break;
                default:
                    throw cursor.Error($"invalid scene token 0x{token:X2}");
            }
        }
    }

    /// <summary>Scene token <c>0x0E</c>: a name (<c>FUN_004d09d0</c>) then a <c>G3D_Transform</c> (<c>FUN_004d0b40</c>).</summary>
    private static VanBurenB3DBone ReadBone(ref Cursor cursor)
    {
        var name = cursor.Str();
        var (flags, translation, axis, degrees, scale) = ReadTransform(ref cursor);
        return new VanBurenB3DBone(name, flags, translation, axis, degrees, scale);
    }

    /// <summary>
    ///     <c>FUN_004d0b40</c>: a name, a flag byte, then bit 0 → translation (3f), bit 1 → rotation
    ///     (<c>FUN_004c98b0</c>: axis 3f + angle in degrees), bit 2 → scale (3f). Absent parts keep
    ///     the identity the constructor (<c>FUN_004cb190</c>) sets.
    /// </summary>
    private static (byte Flags, Vector3 Translation, Vector3 Axis, float Degrees, Vector3 Scale) ReadTransform(
        ref Cursor cursor)
    {
        cursor.Str(); // FUN_004d0690: the transform's own name
        var flags = cursor.U8();
        var translation = Vector3.Zero;
        var axis = Vector3.UnitX;
        var degrees = 0f;
        var scale = Vector3.One;
        if ((flags & 1) != 0)
        {
            translation = cursor.V3();
        }

        if ((flags & 2) != 0)
        {
            axis = cursor.V3();
            degrees = cursor.F32();
        }

        if ((flags & 4) != 0)
        {
            scale = cursor.V3();
        }

        return (flags, translation, axis, degrees, scale);
    }

    /// <summary><c>FUN_004d16a0</c>.</summary>
    private static VanBurenB3DMaterial ReadMaterial(ref Cursor cursor)
    {
        var shader = cursor.Str();
        var name = cursor.Str();
        var colours = new Vector4[4];
        for (var i = 0; i < 4; i++)
        {
            colours[i] = cursor.V4(); // FUN_004c8a30: 3f + 1f
        }

        var scalarA = cursor.F32();
        var scalarB = cursor.F32();
        var blend = cursor.Str();
        var sound = cursor.Str();
        var flags = cursor.U32();
        if ((flags & 0x01) != 0)
        {
            cursor.Skip(8);
        }

        if ((flags & 0x02) != 0)
        {
            cursor.Skip(8);
        }

        if ((flags & 0x04) != 0)
        {
            cursor.Skip(16);
        }

        if ((flags & 0x08) != 0)
        {
            cursor.Skip(16);
        }

        if ((flags & 0x10) != 0)
        {
            cursor.Skip(24);
        }

        if ((flags & 0x20) != 0)
        {
            cursor.Skip(20);
        }

        cursor.U32(); // six render-state bits, unpacked to +0x90..+0x96
        return new VanBurenB3DMaterial(shader, name, colours, scalarA, scalarB, blend, sound, flags);
    }

    /// <summary>
    ///     <c>G3D_Water</c> reader <c>FUN_004d26d0</c>: base name (<c>FUN_004d0750</c>), two strings
    ///     (+0x38, +0x58), a 16-byte block (+0x28..+0x34), then THREE dwords (+0x54, +0x74, +0x78) —
    ///     28 bytes, not 32. ⚠ Read off the decompile only: no retail payload carries token
    ///     <c>0x17</c>, so nothing tiles this.
    /// </summary>
    private static void SkipWater(ref Cursor cursor)
    {
        cursor.Str();
        cursor.Str();
        cursor.Str();
        cursor.Skip(16 + 12);
    }

    /// <summary><c>FUN_004dabd0</c>, the <c>G3D_Node</c> token loop.</summary>
    private static void ReadNode(ref Cursor cursor, SceneState scene, int depth)
    {
        if (depth > MaxNodeDepth)
        {
            throw cursor.Error("nodes nest deeper than 64");
        }

        var nodeName = string.Empty;
        while (true)
        {
            var token = cursor.U8();
            switch (token)
            {
                case 0x01:
                    throw cursor.Error("eof reached before end of node");
                case 0x0A:
                    ReadNode(ref cursor, scene, depth + 1);
                    break;
                case 0x0B:
                    return;
                case 0x0C:
                    nodeName = cursor.Str();
                    scene.NodeNames.Add(nodeName);
                    break;
                case 0x0D:
                    ReadTransform(ref cursor); // never on retail; consumed, not kept
                    break;
                case 0x0F:
                    scene.Meshes.Add(ReadMesh(ref cursor, scene, nodeName));
                    break;
                case 0x11:
                    SkipSpotLight(ref cursor);
                    break;
                case 0x13:
                    cursor.Str(); // G3D_Camera FUN_004d2200: base name + 3 floats
                    cursor.Skip(12);
                    break;
                case 0x14:
                    cursor.Str(); // G3D_ParticleEmitter FUN_004d23e0: ONE string (its name), then two globals
                    break;
                case 0x15:
                    cursor.Str(); // G3D_LightmapSurfaceAttribute FUN_004d0f30: two strings (+0x24, +0x40)
                    cursor.Str();
                    break;
                case 0x17:
                    SkipWater(ref cursor);
                    break;
                case 0x1A:
                    scene.Meshes.Add(ReadMesh(ref cursor, scene,
                        nodeName)); // G3D_WaterTile FUN_004dd120: a mesh + 4 dwords
                    cursor.Skip(16);
                    break;
                case 0x1B:
                    var zones = cursor.Count(1, "occlusion zone"); // FUN_004d9830: count × string
                    for (var i = 0; i < zones; i++)
                    {
                        cursor.Str();
                    }

                    break;
                case 0x10:
                case 0x12:
                case 0x18:
                    // G3D_PointLight / G3D_DirectionalLight / G3D_LineSystem readers were not in the
                    // decompile dump and never occur on retail; refusing is more honest than guessing.
                    throw cursor.Error($"node token 0x{token:X2} is not established (never seen on retail)");
                default:
                    throw cursor.Error($"invalid node token 0x{token:X2}");
            }
        }
    }

    /// <summary>
    ///     <c>G3D_SpotLight</c> reader <c>FUN_004d1ff0</c>: base name, 3 colours, 5 floats + a skipped dword, 3 floats, 2
    ///     floats.
    /// </summary>
    private static void SkipSpotLight(ref Cursor cursor)
    {
        cursor.Str();
        cursor.Skip(36 + 4 + 4 + 4 + 12 + 12 + 8);
    }

    /// <summary><c>G3D_Mesh::ReadB3D</c>, <c>FUN_004dc330</c>.</summary>
    private static VanBurenB3DMesh ReadMesh(ref Cursor cursor, SceneState scene, string nodeName)
    {
        var attributeName = cursor.Str(); // FUN_004d0750: the attribute's name
        if (!scene.Versioned)
        {
            return ReadSourceMesh(ref cursor, scene, nodeName, attributeName);
        }

        var version = cursor.U8();
        return version switch
        {
            0 => ReadSourceMesh(ref cursor, scene, nodeName, attributeName),
            1 => ReadConvertedMesh(ref cursor, nodeName, attributeName),
            _ => throw cursor.Error($"mesh version {version} (only 0 = source and 1 = converted exist)")
        };
    }

    private static VanBurenB3DMesh ReadConvertedMesh(ref Cursor cursor, string nodeName, string attributeName)
    {
        var vertexCount = cursor.Count(1, "vertex");
        var vertexSize = cursor.U32();
        if (vertexSize is not (36 or 44))
        {
            throw cursor.Error($"vertex size {vertexSize} (ExportGfxModelMesh accepts 36 or 44)");
        }

        var stride = (int)vertexSize;
        var vertices = new VanBurenB3DVertex[vertexCount];
        var buffer = cursor.Bytes(checked(vertexCount * stride));
        for (var i = 0; i < vertexCount; i++)
        {
            var v = buffer.Slice(i * stride, stride);
            var position = ReadVector3(v);
            var normal = ReadVector3(v[12..]);
            var colour = BinaryPrimitives.ReadUInt32LittleEndian(v[24..]);
            var uv0 = ReadVector2(v[28..]);
            var uv1 = stride == 44 ? ReadVector2(v[36..]) : Vector2.Zero;
            vertices[i] = new VanBurenB3DVertex(position, normal, D3dColourToRgba(colour), uv0, uv1);
        }

        var boundsMin = cursor.V3();
        var boundsMax = cursor.V3();

        VanBurenB3DBoneWeight[][]? weights = null;
        if (cursor.U8() != 0)
        {
            var streams = cursor.U32();
            if (streams != 4)
            {
                throw cursor.Error($"{streams} bone streams (the engine requires 4: \"Bone counts different\")");
            }

            var perStream = new (uint Bone, float Weight)[4][];
            for (var s = 0; s < 4; s++)
            {
                var stream = cursor.Bytes(checked(vertexCount * 8));
                perStream[s] = new (uint, float)[vertexCount];
                for (var i = 0; i < vertexCount; i++)
                {
                    perStream[s][i] = (
                        BinaryPrimitives.ReadUInt32LittleEndian(stream[(i * 8)..]),
                        BinaryPrimitives.ReadSingleLittleEndian(stream[(i * 8 + 4)..]));
                }
            }

            weights = new VanBurenB3DBoneWeight[vertexCount][];
            for (var i = 0; i < vertexCount; i++)
            {
                var used = new List<VanBurenB3DBoneWeight>(4);
                for (var s = 0; s < 4; s++)
                {
                    var (bone, weight) = perStream[s][i];
                    if (bone != uint.MaxValue)
                    {
                        used.Add(new VanBurenB3DBoneWeight((int)bone, weight));
                    }
                }

                weights[i] = [.. used];
            }
        }

        var groupCount = cursor.Count(1, "group");
        var names = new string[groupCount];
        var shaders = new string[groupCount];
        var blends = new string[groupCount];
        var sounds = new string[groupCount];
        var textures = new string[groupCount][];
        for (var g = 0; g < groupCount; g++)
        {
            names[g] = cursor.Str(); // group struct +0x00
            shaders[g] = cursor.Str(); // +0x1C
            cursor.Skip(16 * 4); // +0x38..+0x74: four RGBA colours
            cursor.F32(); // +0x78
            blends[g] = cursor.Str(); // +0x7C
            sounds[g] = cursor.Str(); // +0x98
            cursor.Skip(60); // +0xB4..+0xFC: 8 bytes + 13 dwords of render state
            var textureCount = cursor.U32(); // +0x100
            var list = new List<string>(2);
            if (textureCount != 0)
            {
                list.Add(cursor.Str());
                if (textureCount > 1)
                {
                    list.Add(cursor.Str());
                }
            }

            textures[g] = [.. list];
            var words = cursor.Count(1, "group word"); // +0x13C: the name split into words
            for (var w = 0; w < words; w++)
            {
                cursor.Str();
            }
        }

        for (var g = 0; g < groupCount; g++)
        {
            cursor.Str(); // one more string per group (+0x8C array)
        }

        var indexCounts = new int[groupCount];
        for (var g = 0; g < groupCount; g++)
        {
            indexCounts[g] = cursor.Count(2, "index");
        }

        for (var g = 0; g < groupCount; g++)
        {
            var triangles = cursor.U32(); // +0x94: index count / 3
            if (triangles * 3 != (uint)indexCounts[g])
            {
                throw cursor.Error($"group {g} declares {triangles} triangles for {indexCounts[g]} indices");
            }
        }

        var groups = new VanBurenB3DGroup[groupCount];
        for (var g = 0; g < groupCount; g++)
        {
            var raw = cursor.Bytes(indexCounts[g] * 2);
            var indices = new int[indexCounts[g]];
            for (var i = 0; i < indices.Length; i++)
            {
                indices[i] = BinaryPrimitives.ReadUInt16LittleEndian(raw[(i * 2)..]);
                if (indices[i] >= vertexCount)
                {
                    throw cursor.Error($"group {g} index {indices[i]} exceeds {vertexCount} vertices");
                }
            }

            groups[g] = new VanBurenB3DGroup(names[g], shaders[g], blends[g], sounds[g], textures[g], indices);
        }

        return new VanBurenB3DMesh(nodeName, attributeName, VanBurenB3dMeshForm.Converted, vertices, groups, weights,
            boundsMin, boundsMax);
    }

    private static VanBurenB3DMesh ReadSourceMesh(ref Cursor cursor, SceneState scene, string nodeName,
        string attributeName)
    {
        var vertexCount = cursor.Count(48, "vertex");
        var vertices = new VanBurenB3DVertex[vertexCount];
        var weights = new VanBurenB3DBoneWeight[vertexCount][];
        var skinned = false;
        for (var i = 0; i < vertexCount; i++)
        {
            var position = cursor.V3();
            var normal = cursor.V3();
            var colour = cursor.V4();
            var uvCount = cursor.Count(8, "uv");
            var uv0 = Vector2.Zero;
            var uv1 = Vector2.Zero;
            for (var u = 0; u < uvCount; u++)
            {
                var uv = cursor.V2();
                if (u == 0)
                {
                    uv0 = uv;
                }
                else if (u == 1)
                {
                    uv1 = uv;
                }
            }

            var weightCount = cursor.Count(8, "bone weight");
            weights[i] = new VanBurenB3DBoneWeight[weightCount];
            for (var w = 0; w < weightCount; w++)
            {
                weights[i][w] = new VanBurenB3DBoneWeight((int)cursor.U32(), cursor.F32());
            }

            skinned |= weightCount > 0;
            vertices[i] = new VanBurenB3DVertex(position, normal, colour, uv0, uv1);
        }

        var groupCount = cursor.Count(16, "group");
        var groups = new VanBurenB3DGroup[groupCount];
        for (var g = 0; g < groupCount; g++)
        {
            cursor.U32(); // FUN_004d9a80: group id
            var name = cursor.Str();
            var textureCount = cursor.Count(2, "texture");
            var textures = new string[textureCount];
            for (var t = 0; t < textureCount; t++)
            {
                textures[t] = cursor.Str();
            }

            var extra = cursor.Count(2, "group string"); // always 0 on retail
            for (var t = 0; t < extra; t++)
            {
                cursor.Str();
            }

            cursor.U8(); // FUN_004d9c90: stored as byte - 0xC9
            var indexCount = cursor.Count(5, "index");
            var indices = new int[indexCount];
            var raw = cursor.Bytes(indexCount * 4);
            for (var i = 0; i < indexCount; i++)
            {
                var index = BinaryPrimitives.ReadUInt32LittleEndian(raw[(i * 4)..]);
                if (index >= (uint)vertexCount)
                {
                    throw cursor.Error($"group {g} index {index} exceeds {vertexCount} vertices");
                }

                indices[i] = (int)index;
            }

            cursor.Skip(indexCount); // one flag byte per index
            var material = scene.Materials.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));
            groups[g] = new VanBurenB3DGroup(
                name,
                material?.Shader ?? string.Empty,
                material?.BlendState ?? string.Empty,
                material?.SurfaceSound ?? string.Empty,
                textures,
                indices);
        }

        return new VanBurenB3DMesh(
            nodeName, attributeName, VanBurenB3dMeshForm.Source, vertices, groups, skinned ? weights : null, null,
            null);
    }

    private static Vector4 D3dColourToRgba(uint argb)
    {
        return new Vector4(
            ((argb >> 16) & 0xFF) / 255f,
            ((argb >> 8) & 0xFF) / 255f,
            (argb & 0xFF) / 255f,
            (argb >> 24) / 255f);
    }

    private static Vector3 ReadVector3(ReadOnlySpan<byte> b)
    {
        return new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(b),
            BinaryPrimitives.ReadSingleLittleEndian(b[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(b[8..]));
    }

    private static Vector2 ReadVector2(ReadOnlySpan<byte> b)
    {
        return new Vector2(BinaryPrimitives.ReadSingleLittleEndian(b), BinaryPrimitives.ReadSingleLittleEndian(b[4..]));
    }

    /// <summary>Mutable accumulator for one scene walk.</summary>
    private sealed class SceneState
    {
        public bool Versioned { get; set; }
        public List<string> NodeNames { get; } = [];
        public List<VanBurenB3DMaterial> Materials { get; } = [];
        public List<VanBurenB3DBone> Bones { get; } = [];
        public List<VanBurenB3DMesh> Meshes { get; } = [];
        public int LightCount { get; set; }
        public float FloatA { get; set; }
        public float FloatB { get; set; }
        public string String { get; set; } = string.Empty;
        public uint Dword { get; set; }
    }

    /// <summary>A bounds-checked little-endian cursor; every overrun names the offset.</summary>
    private ref struct Cursor
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private readonly string _name;

        public Cursor(ReadOnlySpan<byte> bytes, int position, string name)
        {
            _bytes = bytes;
            _name = name;
            Position = position;
        }

        public int Position { get; private set; }

        public int Remaining => _bytes.Length - Position;

        public InvalidDataException Error(string message)
        {
            return new InvalidDataException($"{_name}: {message} at offset {Position} of {_bytes.Length}.");
        }

        public byte U8()
        {
            Need(1);
            return _bytes[Position++];
        }

        public uint U32()
        {
            Need(4);
            var v = BinaryPrimitives.ReadUInt32LittleEndian(_bytes[Position..]);
            Position += 4;
            return v;
        }

        public float F32()
        {
            Need(4);
            var v = BinaryPrimitives.ReadSingleLittleEndian(_bytes[Position..]);
            Position += 4;
            return v;
        }

        /// <summary>A count that must fit: <c>count × minBytesPerItem</c> may not exceed what is left.</summary>
        public int Count(int minBytesPerItem, string what)
        {
            var count = U32();
            if (count > int.MaxValue / Math.Max(minBytesPerItem, 1) || count * minBytesPerItem > Remaining)
            {
                throw Error($"{what} count {count} cannot fit in the {Remaining} bytes left");
            }

            return (int)count;
        }

        public Vector2 V2()
        {
            return new Vector2(F32(), F32());
        }

        public Vector3 V3()
        {
            return new Vector3(F32(), F32(), F32());
        }

        public Vector4 V4()
        {
            return new Vector4(F32(), F32(), F32(), F32());
        }

        public string Str()
        {
            Need(2);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(_bytes[Position..]);
            Position += 2;
            Need(length);
            var s = Encoding.Latin1.GetString(_bytes.Slice(Position, length));
            Position += length;
            return s;
        }

        public ReadOnlySpan<byte> Bytes(int length)
        {
            Need(length);
            var s = _bytes.Slice(Position, length);
            Position += length;
            return s;
        }

        public void Skip(int length)
        {
            Need(length);
            Position += length;
        }

        private void Need(int length)
        {
            if (length < 0 || length > Remaining)
            {
                throw Error($"needs {length} bytes but {Remaining} remain");
            }
        }
    }
}

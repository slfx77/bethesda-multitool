using System.Buffers.Binary;
using System.Numerics;

namespace BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;

/// <summary>One decoded vertex of a <see cref="BosXboxMesh" />, in the game's own axes and units.</summary>
/// <param name="Position">
///     The <c>D3DVSDT_SHORT3</c> position, unscaled. The shader multiplies it by a constant this
///     reader does not know, so the numbers are the authored shorts, not metres.
/// </param>
/// <param name="Normal">
///     The <c>D3DVSDT_NORMSHORT3</c> normal, divided by 32767 (unit length on 143,433 of 143,439
///     vertices).
/// </param>
/// <param name="TexCoord">
///     The <c>D3DVSDT_NORMSHORT2</c> texture coordinate, divided by 32767 (measured range exactly [0,
///     1]).
/// </param>
/// <param name="BoneA">
///     First bone: the vertex's first <c>FLOAT4</c> lane divided by 4 — a slot in
///     <see cref="BosXboxMesh.BonePalette" />.
/// </param>
/// <param name="WeightA">First bone's weight (the second lane).</param>
/// <param name="BoneB">Second bone, from the third lane divided by 4.</param>
/// <param name="WeightB">Second bone's weight (the fourth lane); <c>WeightA + WeightB == 1</c> on every shipped vertex.</param>
/// <param name="SmoothNormal">
///     The extra <c>NORMSHORT3</c> the 38-byte halo layout carries after the weights — a
///     per-POSITION averaged normal, not a tangent. Unit length on 59,750 of 59,848; zero on the
///     32- and 16-byte layouts. See <see cref="BosXboxMesh" /> for the four measurements and the
///     register it is declared in.
/// </param>
internal readonly record struct BosXboxMeshVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector2 TexCoord,
    int BoneA,
    float WeightA,
    int BoneB,
    float WeightB,
    Vector3 SmoothNormal);

/// <summary>
///     A type-1 (mesh) section of an Xbox <see cref="BosClumpFile" /> from Fallout: Brotherhood of
///     Steel (2004, Xbox). Original RE 2026-09-07/08 against the shipped disc and the game's own
///     loader; the PS2 release stores VIF packets instead and no reference exists for either.
///     <para>
///         ⚑⚑ <b>The layout, as the loader reads it</b> (<c>default.xbe</c> VA <c>0x00076AD0</c>,
///         reached from the asset-type dispatch <c>0x000399A0</c> where 1 = mesh, 2 = texture,
///         3 = sound, 4 = anim, 5 = level, 8 = font). Everything is LITTLE-endian.
///         <c>+0x10</c> a RENDER-FLAGS byte whose bit <c>0x20</c> selects the 38-byte halo stride
///         (see below) · <c>+0x12</c> a byte selecting a row of the LOD table at <c>+0xB0</c>
///         (8-byte rows; the row's first dword is the INDEX COUNT) · <c>+0x18</c> the vertex-buffer
///         offset, where a <c>u32</c> byte length is followed by the bytes handed to
///         <c>CreateVertexBuffer</c> (<c>0x000A9D70</c>) · <c>+0x1C</c> the index-buffer offset,
///         <c>u16</c> indices for <c>CreateIndexBuffer</c> (<c>0x000A9D30</c>) · <c>+0x20</c> the
///         VERTEX COUNT · <c>+0x24</c> and <c>+0x64</c> two 64-ENTRY BONE TABLES (see below) ·
///         <c>+0xA7</c> a byte slot for an optional shadow mesh, whose header sits at
///         <c>slot × 16</c> as <c>(u32 a, u32 b, u32 runtime)</c> with <c>a + b</c> ten-byte
///         vertices from <c>+12</c>. The fixed header is <c>0xF0</c> bytes
///         (<see cref="HeaderLength" />), ⚠ but the LOD table does NOT necessarily stop there:
///         the loader reads the selected row at <c>+0xB0 + lod × 8</c> with NO upper bound
///         (<c>0x00076AD0</c>, <c>*(int *)(mesh + 0xB0 + *(char *)(mesh + 0x12) * 8)</c>), and
///         <b>3 of the 313</b> shipped meshes select a row AT or PAST <c>0xF0</c> — BAR
///         <c>#2DB0C18B</c> (lod 8, row at <c>0xF0</c>), BAR <c>#C64CCFBE</c> (lod 11,
///         <c>0x108</c>) and cain <c>#8CAB3206</c> (lod 16, <c>0x130</c>) — inside the bytes that
///         precede the index buffer. <see cref="TryParse" /> therefore bounds the row by the
///         SECTION length, as the loader effectively does; a walk that bounds it by <c>0xF0</c>
///         tiles 310, not 313 (measured 2026-09-08).
///     </para>
///     <para>
///         ⚑⚑ <b>THE GATE — the section must tile exactly</b>: the index buffer ends precisely where
///         the vertex buffer begins (<c>ibo + 2 × indexCount == vbo</c>), the vertex buffer ends
///         precisely at the end of the section (<c>vbo + 4 + vbLength == size</c>), and
///         <c>vbLength == vertexCount × stride</c> for one of the three declared strides. Measured
///         over the Xbox disc: <b>313 of 313</b> mesh sections tile (armor 92 of 184 sections,
///         global 153 of 153, BAR 62 of 236, cain 2, robtur 2, sfx 2 of 3,170). ⛔ The control is
///         the PS2 twins of the same clumps — <b>0 of 3,888</b> sections tile (ARMOR 0/184,
///         BAR 0/232, GLOBAL 0/294, SFX 0/3,178), so the walk cannot simply be accepting anything.
///     </para>
///     <para>
///         ⚑ <b>The stride is DERIVED, not searched.</b> <c>+0x20</c> is the vertex count on
///         313/313 (it also equals <c>maxIndex + 1</c> on 313/313), so the stride is
///         <c>vbLength / vertexCount</c> and lands on exactly one of the strides the INLINE
///         <c>D3DVSD</c> declarations in the shader loader <c>0x0008F690</c> declare: 32 for
///         skin/flat (SHORT3 position, NORMSHORT3 normal, NORMSHORT2 uv, FLOAT4 skinning), 38 for
///         halo (a further NORMSHORT3), 16 for world (no skinning). Retail: 175 at 32 and 138 at 38;
///         ⚠ NO shipped section in the sampled clumps uses the 16-byte world layout, so that path is
///         declared-but-unexercised.
///     </para>
///     <para>
///         ⚑⚑ <b>…and the DRAW PATH states the same stride outright, which this reader now checks.</b>
///         ⛔ Until 2026-09-08 this file called the words before <c>+0x12</c> undecoded. They are
///         not: the byte at <c>+0x10</c> is a render-flags byte, and the draw function
///         <c>FUN_00077EA0</c> (VA <c>0x00077EA0</c>) computes the stream stride straight out of it —
///         <c>FUN_000A7FD0(0, *(u32 *)(mesh + 0x18), (-((*(byte *)(mesh + 0x10) &amp; 0x20) != 0) &amp; 6U) + 0x20)</c>,
///         i.e. <c>stride = 0x20 + (bit 0x20 ? 6 : 0)</c>. <c>FUN_000A7FD0(stream, buffer, stride)</c>
///         is <c>SetStreamSource</c>: it stores its third argument into
///         <c>(&amp;DAT_000B82F0)[stream × 3]</c> and its second into <c>(&amp;DAT_000B82F8)[stream × 3]</c>.
///         The same bit gates the extra halo pass at <c>FUN_000779D0</c>. Measured over the disc, the
///         flag and the derived stride agree <b>313 of 313</b> times, with a perfectly separated
///         cross-tab — <c>{(32, bit clear): 175, (38, bit set): 138}</c>, no exception in either
///         direction — so <see cref="TryParse" /> now REQUIRES the agreement and gains a second,
///         independent gate for free. It could have failed on any one of the 313 and did not.
///         ⚠ The check is applied only to strides 32 and 38, because the expression above can never
///         yield 16: a 16-byte world section, if one is ever found, is drawn by some other function
///         and the flag's meaning there is unknown. The other bits of <c>+0x10</c> gate render state
///         in the same function (<c>0x10</c> a second texture stage, <c>0x88</c> alpha blending) and
///         are NOT decoded further; retail carries 17 distinct values, 0 … 0x35.
///     </para>
///     <para>
///         ⚑⚑ <b>The field ORDER and the vertex REGISTERS come from the declarations themselves</b>,
///         decoded token by token out of <c>default.xbe</c> — the tokens are built inline as
///         <c>mov dword ptr […], imm32</c> immediately after the <c>push</c> of the shader's name
///         string, so each stream can be read off next to the shader it belongs to. A
///         <c>D3DVSD_REG</c> token is <c>(2 &lt;&lt; 29) | (type &lt;&lt; 16) | register</c>:
///         <c>skin</c> (name pushed at VA <c>0x0008F7E1</c>, tokens from <c>0x0008F7EA</c>) declares
///         reg0 <c>SHORT3</c> · reg2 <c>NORMSHORT3</c> · reg9 <c>NORMSHORT2</c> · reg1
///         <c>FLOAT4</c> = <b>32</b> bytes; <c>halo</c> (<c>0x0008F881</c> / <c>0x0008F88A</c>) the
///         same plus reg3 <c>NORMSHORT3</c> = <b>38</b>; <c>flat</c> (<c>0x0008F931</c>) 32;
///         <c>world</c> (<c>0x0008F9D8</c>) reg0/reg2/reg9 only = <b>16</b>; <c>shadow</c>
///         (<c>0x0008FA77</c>) reg0 <c>SHORT3</c> + reg1 <c>SHORT2</c> = <b>10</b> — which is
///         independently where the shadow block's ten-byte vertex comes from; <c>particle</c>
///         (<c>0x0008FC3C</c>) reg0 + reg1 <c>SHORT4</c> = 16. In the Xbox register names that is
///         POSITION, NORMAL, TEXCOORD0, BLENDWEIGHT and — for halo's extra vector — DIFFUSE.
///     </para>
///     <para>
///         ⛔⛔ <b>The halo layout's extra <c>NORMSHORT3</c> is NOT a tangent</b>; it was named one
///         until 2026-09-08 and the shipped data refutes that outright. Over the 59,750 halo
///         vertices that carry both vectors, <c>dot(normal, extra)</c> has mean <b>0.836</b> and
///         median <b>0.9963</b>: <b>30,407 (50.9%) exceed 0.99</b> — parallel to the normal — while
///         only <b>198 (0.33%)</b> are within 0.05 of perpendicular, and a tangent is perpendicular
///         to its normal by construction. What it behaves like is a per-POSITION SMOOTHED normal:
///         at the 11,408 positions where two or more vertices share a <c>SHORT3</c> position but
///         carry DIFFERENT hard normals (a smoothing split or a UV seam), the extra vector is
///         byte-identical across the whole group on <b>11,399 (99.9%)</b>, and it sits at a median
///         dot of <b>0.9923</b> from the normalised mean of those split normals. That test could
///         have failed: a tangent is split exactly where the UVs are, so it would have differed
///         across the same groups; and the agreement is not trivial, since only 1 of the 138 halo
///         meshes uses a single extra value throughout (the rest carry 16 … 321 distinct ones).
///         ⚠ Its USE is still not established — it is declared into register 3 (D3DVSDE_DIFFUSE)
///         and consumed by <c>halo.xvu</c>, which is on the disc (164 bytes = ten NV2A vertex
///         instructions) and NOT decoded here.
///     </para>
///     <para>
///         ⚑⚑
///         <b>
///             The <c>FLOAT4</c> is (bone × 4, weight, bone × 4, weight), and <c>+0x24</c> is the
///             bone palette it indexes.
///         </b>
///         On all 143,439 shipped vertices lanes 0 and 2 are
///         non-negative multiples of 4 and lanes 1 and 3 sum to exactly 1 (a shader indexes a bone
///         matrix by four constant registers, which is where the ×4 comes from). Dividing lane 0 by
///         4 gives a palette slot, and
///         <b>
///             maxSlot + 1 equals the run of non-<c>0xFF</c> bytes at
///             <c>+0x24</c> on 313 of 313 meshes
///         </b>
///         — the coincidence that settles the table. Reading
///         the four floats as four weights instead makes that impossible: their sums would be
///         1, 5, 9, 13 … (measured), not 1. ⛔ The CONTROL that the offset is right: the same test
///         against the second table at <c>+0x64</c> matches <b>0 of 313</b>.
///     </para>
///     <para>
///         ⛔⛔
///         <b>
///             CORRECTION 2026-09-08 — the bone table is 64 entries, not 128, and <c>0xFF</c> is a
///             per-entry SKIP, not a terminator.
///         </b>
///         This file used to say "<c>+0x24 … +0xA3</c>, a
///         128-byte palette terminated by <c>0xFF</c>". The draw path refutes both halves. In
///         <c>FUN_00077EA0</c> the walk is
///         <c>
///             pcVar10 = mesh + 0x24; local_a0 = 0x40; do { if (*pcVar10 != -1) { FUN_000767C0(…, *pcVar10 × 0x30 + 4 +
///             skeleton, …); FUN_000A7E00(); } pcVar10++; } while (--local_a0 != 0);
///         </c>
///         — exactly <c>0x40</c> = 64 iterations, each <c>0xFF</c> entry skipped and the loop
///         carrying on. <c>+0x64 … +0xA3</c> is therefore a SECOND 64-entry table, not the tail of
///         the first: a later block in the same function walks <c>mesh + 100</c> (<c>0x64</c>) for
///         another <c>0x40</c> iterations and rebinds an entry only when it differs from
///         <c>pcVar10[-0x40]</c> — the matching slot of the first table.
///         ⚑ The disc PROVES the skip semantics independently of the code, and refutes the tidier
///         story that the second table is merely 0xFF padding: <b>5 of the 313</b> meshes carry a
///         populated second table (cain <c>#8CAB3206</c>, BAR <c>#15C54C4E</c>, <c>#98545F16</c>,
///         <c>#158280B5</c>, <c>#FE6439F1</c>), and in <b>5 of those 5</b> its live entries are
///         NON-CONTIGUOUS — live, <c>0xFF</c>, then live again (e.g. cain <c>#8CAB3206</c>: slots
///         0-6, then 29, 30, 32, 43). A terminator reading loses those. All five bind the same 44
///         slots to a different set of bones in the low slots and the identical bone in the high
///         ones (7 … 11 of 11 … 17 live entries differ), which is what the second draw pass is for.
///         ⚠ The FIRST table never exercises the skip: <b>0 of 313</b> meshes have a non-<c>0xFF</c>
///         byte after the first <c>0xFF</c> within <c>+0x24 … +0x63</c>, so retail behaviour is
///         unchanged by the fix — every authored first run is 1 … 44. What the old 128 would have
///         allowed is a section running its palette 64 bytes past the real table.
///     </para>
///     <para>
///         ⚑⚑ <b>The indices are TRIANGLE STRIPS, and the game says so at the hardware level.</b>
///         Every draw in <c>FUN_00077EA0</c> goes through <c>FUN_000A9870(6, count, indexPtr)</c>,
///         which builds an NV2A push buffer: it writes method <c>0x417FC</c> —
///         <c>NV097_SET_BEGIN_END</c> with a one-dword payload — and stores that first argument as
///         the payload, then streams the indices as <c>NV097_ARRAY_ELEMENT16</c> (<c>0x1800</c>)
///         batches. In the NV2A <c>BEGIN_END</c> mode enum <b>6 is TRIANGLE_STRIP</b>, and the
///         literal is <b>6 at all 8 call sites</b> in the image. ⚠ Read the mode as a PC Direct3D 8
///         <c>D3DPRIMITIVETYPE</c> and you get TRIANGLEFAN — the Xbox enum inserts LINELOOP at 3 and
///         shifts everything after it up by one, so the two disagree by exactly one on this value.
///         This is stronger than the arithmetic that was the only evidence before: a triangle list
///         would need the count divisible by three, and 217 of the 313 counts are not (96 ≡ 0,
///         137 ≡ 1, 80 ≡ 2 mod 3). ⛔ An earlier note claiming "icount ≡ 1 or 2 mod 3" is refuted by
///         those 96. Stitching leaves 115,633 degenerate corners in 267,943 strip positions —
///         152,310 real triangles.
///     </para>
///     <para>
///         ⚠ NOT decoded, and returned raw rather than guessed: <c>+0x11</c> and <c>+0x13</c>, the
///         non-<c>0x20</c> bits of the <c>+0x10</c> flags, the second dword of each LOD row, the
///         bytes between the header and the index buffer (the other LOD levels' index data, plus the
///         shadow block when there is one), and the winding convention — the exporter marks its
///         material double-sided rather than pick one. ⚠ The LOD table's internal shape is open too:
///         the value at <c>+0xB0 + lod × 8</c> is certainly the index count (the tiling gate is an
///         exact equality on 313/313), but the draw loop walks the run of dwords from <c>+0xAC</c>
///         in what Ghidra renders as a 4-byte step, drawing <c>entry[i + 1] − entry[i]</c> indices
///         at a time, so the rows may be sub-ranges of one array rather than 8-byte records.
///     </para>
/// </summary>
internal sealed class BosXboxMesh
{
    /// <summary>
    ///     Bytes of header: the LOD table at <c>+0xB0</c> runs to here, and a shadow block may start at exactly this
    ///     offset.
    /// </summary>
    public const int HeaderLength = 0xF0;

    /// <summary>
    ///     Offset of the render-flags byte. <c>FUN_00077EA0</c> reads it for the stream stride
    ///     (<see cref="HaloStrideFlag" />), a second texture stage (<c>0x10</c>) and alpha blending
    ///     (<c>0x88</c>).
    /// </summary>
    public const int RenderFlagsOffset = 0x10;

    /// <summary>
    ///     The bit of <see cref="RenderFlagsOffset" /> that selects the 38-byte halo stride:
    ///     <c>SetStreamSource</c> is called with <c>0x20 + (flag ? 6 : 0)</c>. Agrees with the
    ///     derived stride on 313 of 313 shipped meshes.
    /// </summary>
    public const byte HaloStrideFlag = 0x20;

    /// <summary>Offset of the LOD-selecting byte.</summary>
    public const int LodByteOffset = 0x12;

    /// <summary>Offset of the vertex-buffer offset.</summary>
    public const int VertexBufferPointerOffset = 0x18;

    /// <summary>Offset of the index-buffer offset.</summary>
    public const int IndexBufferPointerOffset = 0x1C;

    /// <summary>Offset of the vertex count.</summary>
    public const int VertexCountOffset = 0x20;

    /// <summary>Offset of the first bone table, the one the skinning lanes index.</summary>
    public const int BonePaletteOffset = 0x24;

    /// <summary>
    ///     Offset of the SECOND bone table, walked by the second draw pass in <c>FUN_00077EA0</c>,
    ///     which rebinds only the slots whose entry differs from the first table's.
    /// </summary>
    public const int SecondBonePaletteOffset = 0x64;

    /// <summary>
    ///     Entries per bone table — <b>64</b>, the literal iteration count of both palette walks in
    ///     <c>FUN_00077EA0</c>. ⛔ It was <c>0x80</c> until 2026-09-08, which merged the two tables.
    /// </summary>
    public const int BonePaletteLength = 0x40;

    /// <summary>Offset of the shadow-mesh slot byte.</summary>
    public const int ShadowSlotOffset = 0xA7;

    /// <summary>Offset of the LOD table.</summary>
    public const int LodTableOffset = 0xB0;

    /// <summary>Bytes per LOD row.</summary>
    public const int LodRowLength = 8;

    /// <summary>Bytes per shadow vertex (<c>SHORT3</c> position + <c>SHORT2</c>).</summary>
    public const int ShadowVertexLength = 10;

    /// <summary>
    ///     The bone-table entry the draw loop SKIPS. ⛔ Not a terminator — the loop runs all
    ///     <see cref="BonePaletteLength" /> entries regardless, and 5 shipped meshes carry live
    ///     entries after one of these in their second table.
    /// </summary>
    public const byte BonePaletteUnused = 0xFF;

    /// <summary>The three strides the inline <c>D3DVSD</c> declarations at <c>0x0008F690</c> declare.</summary>
    public static readonly int[] DeclaredStrides = [16, 32, 38];

    private BosXboxMesh(
        string name,
        int stride,
        byte renderFlags,
        int lodIndex,
        int indexBufferOffset,
        int vertexBufferOffset,
        IReadOnlyList<BosXboxMeshVertex> vertices,
        IReadOnlyList<ushort> stripIndices,
        IReadOnlyList<int> triangleIndices,
        IReadOnlyList<byte> bonePalette,
        IReadOnlyList<byte> bonePaletteEntries,
        IReadOnlyList<byte> secondBonePaletteEntries,
        int shadowSlot,
        int shadowVertexCount)
    {
        Name = name;
        Stride = stride;
        RenderFlags = renderFlags;
        LodIndex = lodIndex;
        IndexBufferOffset = indexBufferOffset;
        VertexBufferOffset = vertexBufferOffset;
        Vertices = vertices;
        StripIndices = stripIndices;
        TriangleIndices = triangleIndices;
        BonePalette = bonePalette;
        BonePaletteEntries = bonePaletteEntries;
        SecondBonePaletteEntries = secondBonePaletteEntries;
        ShadowSlot = shadowSlot;
        ShadowVertexCount = shadowVertexCount;
    }

    /// <summary>Source name, for messages.</summary>
    public string Name { get; }

    /// <summary>Bytes per vertex — 16, 32 or 38, derived as <c>vbLength / vertexCount</c>.</summary>
    public int Stride { get; }

    /// <summary>True when the layout carries the <c>FLOAT4</c> skinning lanes (stride 32 and 38).</summary>
    public bool IsSkinned => Stride >= 32;

    /// <summary>True for the 38-byte halo layout, which appends a second <c>NORMSHORT3</c> in vertex register 3.</summary>
    public bool HasSmoothNormal => Stride == 38;

    /// <summary>
    ///     The render-flags byte at <c>+0x10</c>. Bit <see cref="HaloStrideFlag" /> is the stride
    ///     selector <c>FUN_00077EA0</c> hands to <c>SetStreamSource</c>; the rest gate render state
    ///     and are not decoded.
    /// </summary>
    public byte RenderFlags { get; }

    /// <summary>
    ///     True when <see cref="RenderFlags" /> asks for the 38-byte stream — equal to <see cref="HasSmoothNormal" /> on
    ///     313/313.
    /// </summary>
    public bool DeclaresHaloStride => (RenderFlags & HaloStrideFlag) != 0;

    /// <summary>The byte at <c>+0x12</c>: which LOD row supplied the index count.</summary>
    public int LodIndex { get; }

    /// <summary>Byte offset of the index buffer inside the section.</summary>
    public int IndexBufferOffset { get; }

    /// <summary>Byte offset of the vertex buffer's <c>u32</c> length inside the section.</summary>
    public int VertexBufferOffset { get; }

    /// <summary>The decoded vertices, in buffer order.</summary>
    public IReadOnlyList<BosXboxMeshVertex> Vertices { get; }

    /// <summary>The raw triangle-strip indices, as stored.</summary>
    public IReadOnlyList<ushort> StripIndices { get; }

    /// <summary>The strip expanded to a triangle list, degenerate corners dropped and winding alternated.</summary>
    public IReadOnlyList<int> TriangleIndices { get; }

    /// <summary>Triangles in <see cref="TriangleIndices" />.</summary>
    public int TriangleCount => TriangleIndices.Count / 3;

    /// <summary>
    ///     The leading run of used entries in the first bone table — the skeleton bone each vertex
    ///     slot refers to. This is the run the skinning lanes index: <c>maxSlot + 1</c> equals its
    ///     length on 313 of 313 shipped meshes. See <see cref="BonePaletteEntries" /> for the whole
    ///     table.
    /// </summary>
    public IReadOnlyList<byte> BonePalette { get; }

    /// <summary>
    ///     All <see cref="BonePaletteLength" /> entries of the first bone table, <c>0xFF</c>
    ///     included — the draw loop skips those rather than stopping at them.
    /// </summary>
    public IReadOnlyList<byte> BonePaletteEntries { get; }

    /// <summary>
    ///     All <see cref="BonePaletteLength" /> entries of the SECOND bone table at
    ///     <c>+0x64</c>, used by the second draw pass. Entirely <c>0xFF</c> on 308 of the 313
    ///     shipped meshes; the other five bind the same slots to a different set of bones.
    /// </summary>
    public IReadOnlyList<byte> SecondBonePaletteEntries { get; }

    /// <summary>The byte at <c>+0xA7</c>: 0 when the mesh has no shadow volume, else its header sits at <c>slot × 16</c>.</summary>
    public int ShadowSlot { get; }

    /// <summary>Vertices in the shadow volume (<c>a + b</c> of its header), or 0.</summary>
    public int ShadowVertexCount { get; }

    /// <summary>Content probe: does this section walk and tile as a mesh?</summary>
    public static bool IsMesh(ReadOnlySpan<byte> section)
    {
        return TryParse(section, "probe", out _, out _);
    }

    /// <summary>Parses a mesh section, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static BosXboxMesh Parse(ReadOnlySpan<byte> section, string name)
    {
        if (!TryParse(section, name, out var mesh, out var error))
        {
            throw new InvalidDataException(error);
        }

        return mesh;
    }

    /// <summary>Parses a mesh section, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> section, string name, out BosXboxMesh mesh, out string error)
    {
        mesh = null!;
        if (section.Length < HeaderLength)
        {
            error = $"{name}: {section.Length} bytes is shorter than the {HeaderLength}-byte mesh header.";
            return false;
        }

        var vertexBufferOffset = BinaryPrimitives.ReadInt32LittleEndian(section[VertexBufferPointerOffset..]);
        var indexBufferOffset = BinaryPrimitives.ReadInt32LittleEndian(section[IndexBufferPointerOffset..]);
        var vertexCount = BinaryPrimitives.ReadInt32LittleEndian(section[VertexCountOffset..]);
        var lodIndex = section[LodByteOffset];

        // Written as a subtraction so a hostile offset near int.MaxValue cannot overflow past the check.
        if (vertexBufferOffset < HeaderLength || vertexBufferOffset > section.Length - 4)
        {
            error =
                $"{name}: the vertex-buffer offset {vertexBufferOffset} is not inside the {section.Length}-byte section.";
            return false;
        }

        if (indexBufferOffset < HeaderLength || indexBufferOffset >= vertexBufferOffset)
        {
            error =
                $"{name}: the index-buffer offset {indexBufferOffset} does not precede the vertex buffer at {vertexBufferOffset}.";
            return false;
        }

        var lodRow = LodTableOffset + lodIndex * LodRowLength;
        if (lodRow + LodRowLength > section.Length)
        {
            error = $"{name}: LOD row {lodIndex} would start at {lodRow}, past the {section.Length}-byte section.";
            return false;
        }

        var indexCount = BinaryPrimitives.ReadInt32LittleEndian(section[lodRow..]);
        if (indexCount < 3 || indexCount > (section.Length - indexBufferOffset) / sizeof(ushort))
        {
            error =
                $"{name}: LOD row {lodIndex} declares {indexCount} indices, which do not fit from {indexBufferOffset}.";
            return false;
        }

        // ⚑ THE TILING GATE. Both halves are exact equalities, and the PS2 sections fail them
        // 3,888 times out of 3,888 — the walk cannot be accepting arbitrary bytes.
        if (indexBufferOffset + indexCount * sizeof(ushort) != vertexBufferOffset)
        {
            error = $"{name}: {indexCount} indices from {indexBufferOffset} end at "
                    + $"{indexBufferOffset + indexCount * sizeof(ushort)} rather than at the vertex buffer ({vertexBufferOffset}).";
            return false;
        }

        var vertexBufferLength = BinaryPrimitives.ReadInt32LittleEndian(section[vertexBufferOffset..]);
        if (vertexBufferLength < 0 || vertexBufferOffset + 4 + vertexBufferLength != section.Length)
        {
            error = $"{name}: the {vertexBufferLength}-byte vertex buffer at {vertexBufferOffset} does not end at the "
                    + $"{section.Length}-byte section end.";
            return false;
        }

        // ⚑ The stride is DERIVED from +0x20, never searched: vbLength / vertexCount must land on
        // one of the three declared strides, exactly.
        if (vertexCount <= 0 || vertexBufferLength % vertexCount != 0)
        {
            error =
                $"{name}: {vertexBufferLength} bytes of vertex buffer do not divide by the declared {vertexCount} vertices.";
            return false;
        }

        var stride = vertexBufferLength / vertexCount;
        if (Array.IndexOf(DeclaredStrides, stride) < 0)
        {
            error = $"{name}: {vertexBufferLength} bytes over {vertexCount} vertices is a stride of {stride}, "
                    + "which no D3DVSD declaration in the shader loader uses.";
            return false;
        }

        // ⚑ The SECOND, independent gate: the draw path computes its stream stride as
        // 0x20 + (flags & 0x20 ? 6 : 0), so the flag must agree with the stride the buffer lengths
        // force. 313/313 on the disc, cross-tab {(32, clear): 175, (38, set): 138}. The 16-byte
        // world layout is exempt because that expression can never produce 16 — such a section, if
        // one is ever found, is drawn somewhere else.
        var renderFlags = section[RenderFlagsOffset];
        var declaresHalo = (renderFlags & HaloStrideFlag) != 0;
        if (stride is 32 or 38 && declaresHalo != (stride == 38))
        {
            error = $"{name}: the render flags 0x{renderFlags:X2} at +0x10 ask SetStreamSource for a stride of "
                    + $"{(declaresHalo ? 38 : 32)}, but {vertexBufferLength} bytes over {vertexCount} vertices is {stride}.";
            return false;
        }

        var strip = new ushort[indexCount];
        var maxIndex = -1;
        for (var i = 0; i < indexCount; i++)
        {
            var index = BinaryPrimitives.ReadUInt16LittleEndian(section[(indexBufferOffset + i * sizeof(ushort))..]);
            strip[i] = index;
            maxIndex = Math.Max(maxIndex, index);
        }

        if (maxIndex >= vertexCount)
        {
            error = $"{name}: index {maxIndex} is past the declared {vertexCount} vertices.";
            return false;
        }

        var palette = ReadBonePalette(section);
        var vertices = new BosXboxMeshVertex[vertexCount];
        var vertexBase = vertexBufferOffset + 4;
        for (var i = 0; i < vertexCount; i++)
        {
            if (!TryReadVertex(section, vertexBase + i * stride, stride, palette.Length, out vertices[i], out var why))
            {
                error = $"{name}: vertex {i} {why}";
                return false;
            }
        }

        var shadowSlot = section[ShadowSlotOffset];
        var shadowVertexCount = 0;
        if (shadowSlot != 0)
        {
            var at = shadowSlot * 16;
            if (at + 12 > indexBufferOffset)
            {
                error =
                    $"{name}: the shadow header at {at} does not fit before the index buffer at {indexBufferOffset}.";
                return false;
            }

            var a = BinaryPrimitives.ReadInt32LittleEndian(section[at..]);
            var b = BinaryPrimitives.ReadInt32LittleEndian(section[(at + 4)..]);
            if (a < 0 || b < 0 || at + 12 + (long)(a + b) * ShadowVertexLength > indexBufferOffset)
            {
                error =
                    $"{name}: the shadow mesh's {a} + {b} vertices do not fit between {at + 12} and the index buffer at {indexBufferOffset}.";
                return false;
            }

            shadowVertexCount = a + b;
        }

        mesh = new BosXboxMesh(
            name,
            stride,
            renderFlags,
            lodIndex,
            indexBufferOffset,
            vertexBufferOffset,
            vertices,
            strip,
            ToTriangleList(strip),
            palette,
            section.Slice(BonePaletteOffset, BonePaletteLength).ToArray(),
            section.Slice(SecondBonePaletteOffset, BonePaletteLength).ToArray(),
            shadowSlot,
            shadowVertexCount);
        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     Expands a triangle strip to a triangle list. Degenerate corners — the stitches that join
    ///     the sub-strips — are dropped, and the winding of every odd triangle is reversed, which is
    ///     what a strip means everywhere it is drawn.
    /// </summary>
    public static int[] ToTriangleList(IReadOnlyList<ushort> strip)
    {
        ArgumentNullException.ThrowIfNull(strip);

        var triangles = new List<int>(Math.Max(0, (strip.Count - 2) * 3));
        for (var i = 0; i + 2 < strip.Count; i++)
        {
            int a = strip[i];
            int b = strip[i + 1];
            int c = strip[i + 2];
            if (a == b || b == c || a == c)
            {
                continue;
            }

            triangles.Add(a);
            if ((i & 1) == 0)
            {
                triangles.Add(b);
                triangles.Add(c);
            }
            else
            {
                triangles.Add(c);
                triangles.Add(b);
            }
        }

        return [.. triangles];
    }

    /// <summary>
    ///     The run of used slots at the head of the first bone table. ⚠ Deliberately the LEADING RUN
    ///     rather than every non-<c>0xFF</c> entry of the 64: the vertices' <c>maxSlot + 1</c> equals
    ///     this length on 313 of 313 shipped meshes, which is what makes the skinning-lane check a
    ///     tight gate, and no shipped mesh has a used entry after an unused one in this table
    ///     (0 of 313). The game itself would honour a hole; if one ever turns up, this is the line
    ///     that has to widen, and the <see cref="BonePaletteEntries" /> table beside it already
    ///     carries the bytes.
    /// </summary>
    private static byte[] ReadBonePalette(ReadOnlySpan<byte> section)
    {
        var length = 0;
        while (length < BonePaletteLength && section[BonePaletteOffset + length] != BonePaletteUnused)
        {
            length++;
        }

        return section.Slice(BonePaletteOffset, length).ToArray();
    }

    private static bool TryReadVertex(
        ReadOnlySpan<byte> section,
        int at,
        int stride,
        int paletteLength,
        out BosXboxMeshVertex vertex,
        out string why)
    {
        vertex = default;
        why = string.Empty;

        var position = new Vector3(
            BinaryPrimitives.ReadInt16LittleEndian(section[at..]),
            BinaryPrimitives.ReadInt16LittleEndian(section[(at + 2)..]),
            BinaryPrimitives.ReadInt16LittleEndian(section[(at + 4)..]));
        var normal = ReadNormShort3(section, at + 6);
        var uv = new Vector2(
            BinaryPrimitives.ReadInt16LittleEndian(section[(at + 12)..]) / 32767f,
            BinaryPrimitives.ReadInt16LittleEndian(section[(at + 14)..]) / 32767f);

        var boneA = 0;
        var boneB = 0;
        var weightA = 1f;
        var weightB = 0f;
        var smoothNormal = Vector3.Zero;

        if (stride >= 32)
        {
            var laneA = BinaryPrimitives.ReadSingleLittleEndian(section[(at + 16)..]);
            weightA = BinaryPrimitives.ReadSingleLittleEndian(section[(at + 20)..]);
            var laneB = BinaryPrimitives.ReadSingleLittleEndian(section[(at + 24)..]);
            weightB = BinaryPrimitives.ReadSingleLittleEndian(section[(at + 28)..]);

            // ⚑ Four constant registers per bone matrix, so a bone index arrives multiplied by 4.
            // 143,439 of 143,439 shipped vertices satisfy both halves of this.
            if (!TryBoneSlot(laneA, paletteLength, out boneA) || !TryBoneSlot(laneB, paletteLength, out boneB))
            {
                why =
                    $"carries skinning lanes {laneA} / {laneB}, which are not multiples of 4 inside the {paletteLength}-slot bone palette.";
                return false;
            }

            if (Math.Abs(weightA + weightB - 1f) > 1e-4f)
            {
                why = $"carries weights {weightA} + {weightB}, which do not sum to 1.";
                return false;
            }

            if (stride == 38)
            {
                smoothNormal = ReadNormShort3(section, at + 32);
            }
        }

        vertex = new BosXboxMeshVertex(position, normal, uv, boneA, weightA, boneB, weightB, smoothNormal);
        return true;
    }

    private static bool TryBoneSlot(float lane, int paletteLength, out int slot)
    {
        slot = 0;
        // An absent palette permits only slot zero. Bound the float before its integer conversion.
        if (lane < 0 || lane >= Math.Max(paletteLength, 1) * 4)
        {
            return false;
        }

#pragma warning disable S1244 // Bone lanes encode exact integer register offsets, in multiples of four.
        if (lane != MathF.Floor(lane) || lane % 4 != 0)
#pragma warning restore S1244
        {
            return false;
        }

        slot = (int)lane / 4;
        return true;
    }

    private static Vector3 ReadNormShort3(ReadOnlySpan<byte> section, int at)
    {
        return new Vector3(
            BinaryPrimitives.ReadInt16LittleEndian(section[at..]) / 32767f,
            BinaryPrimitives.ReadInt16LittleEndian(section[(at + 2)..]) / 32767f,
            BinaryPrimitives.ReadInt16LittleEndian(section[(at + 4)..]) / 32767f);
    }
}

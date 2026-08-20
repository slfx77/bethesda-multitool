using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Materials;

/// <summary>
///     Pins Starfield's compiled material database (<c>BETH</c>/<c>BSComponentDB2</c>) decode.
///     <para>
///         The fixture is synthetic but mirrors the exact shape of the retail file: a
///         <c>STRT</c> table whose entries are referenced by BYTE OFFSET, a <c>CLAS</c> definition, an
///         <c>ObjectInfo</c> list, a <c>ComponentInfo</c> list, an <c>EdgeInfo</c> list linking a
///         texture-set object under a material object, and <c>DIFF</c> component payloads.
///     </para>
/// </summary>
public class StarfieldMaterialDatabaseTests
{
    /// <summary>
    ///     The extension half of a BSResourceID is NOT hashed — it is up to four lowercase ASCII bytes
    ///     packed little-endian. Hashing it produces a stable, plausible value that matches nothing, so
    ///     every lookup silently returns "not found". Retail stores <c>.mat</c> as 0x0074616D.
    /// </summary>
    [Fact]
    public void ComputeResourceId_PacksExtensionAsAsciiNotHash()
    {
        var id = StarfieldMaterialDatabase.ComputeResourceId(@"materials\Terrain\Default006Base.mat");

        Assert.Equal(0x0074616Du, id.Ext);
    }

    /// <summary>Directory and base name ARE hashed, and the hash is case- and separator-insensitive.</summary>
    [Fact]
    public void ComputeResourceId_IsCaseAndSeparatorInsensitive()
    {
        var a = StarfieldMaterialDatabase.ComputeResourceId(@"materials\Terrain\Default006Base.mat");
        var b = StarfieldMaterialDatabase.ComputeResourceId("MATERIALS/terrain/DEFAULT006BASE.MAT");

        Assert.Equal(a, b);
    }

    /// <summary>A Data-rooted reference (how .mat Import/Parent links are written) keys identically.</summary>
    [Fact]
    public void ComputeResourceId_StripsDataRoot()
    {
        var bare = StarfieldMaterialDatabase.ComputeResourceId(@"materials\Terrain\Default006Base.mat");
        var rooted = StarfieldMaterialDatabase.ComputeResourceId(@"Data\Materials\Terrain\Default006Base.mat");

        Assert.Equal(bare, rooted);
    }

    [Fact]
    public void Parse_RejectsNonReflectionStream()
    {
        Assert.Null(StarfieldMaterialDatabase.Parse(Encoding.ASCII.GetBytes("not a cdb at all........")));
    }

    [Fact]
    public void Parse_ResolvesTextureThroughTheObjectGraph()
    {
        var db = StarfieldMaterialDatabase.Parse(BuildDatabase());

        Assert.NotNull(db);
        Assert.Equal(2, db!.ObjectCount);
        Assert.Equal(@"Data\Textures\Ground\Dirt_color.dds", db.ResolveDiffuse(@"materials\test\mat.mat"));
        Assert.Equal(@"Data\Textures\Ground\Dirt_normal.dds", db.ResolveNormal(@"materials\test\mat.mat"));
        Assert.Null(db.ResolveDiffuse(@"materials\test\absent.mat"));
    }

    /// <summary>
    ///     Component payloads pair with the component table POSITIONALLY, so anything that miscounts
    ///     object chunks shifts every later material onto a neighbour's textures. Here an extra
    ///     component chunk is emitted before the table exists — the decoder must not let it consume a
    ///     slot.
    /// </summary>
    [Fact]
    public void Parse_IgnoresComponentChunksPrecedingTheComponentTable()
    {
        var db = StarfieldMaterialDatabase.Parse(BuildDatabase(true));

        Assert.NotNull(db);
        Assert.Equal(@"Data\Textures\Ground\Dirt_color.dds", db!.ResolveDiffuse(@"materials\test\mat.mat"));
    }

    /// <summary>
    ///     The ObjectInfo stride is announced by the CLAS: 4 fields ⇒ 21 bytes (launch-era), 5 fields
    ///     ⇒ 33 (current retail's appended parent BSResourceID). Both must decode — walking a table at
    ///     the wrong stride does not fail, it silently shears every record after the first into
    ///     garbage resource IDs.
    /// </summary>
    [Fact]
    public void Parse_ResolvesThroughTheWideObjectInfoStride()
    {
        var db = StarfieldMaterialDatabase.Parse(BuildDatabase(wideObjectInfo: true));

        Assert.NotNull(db);
        Assert.Equal(2, db!.ObjectCount);
        Assert.Equal(@"Data\Textures\Ground\Dirt_color.dds", db.ResolveDiffuse(@"materials\test\mat.mat"));
    }

    /// <summary>
    ///     A material owns both LAYERS and BLENDERS, and a blender's texture — the mask that mixes two
    ///     layers — is stored at index 0, exactly where a texture set keeps its albedo. Resolving by
    ///     "first index-0 texture in the object graph" therefore returns a <c>_mask</c> that really does
    ///     belong to the right material, which is why it reads as correct and is not: a single-channel
    ///     mask sampled as RGB renders PURE RED. Measured on retail, that hit 4,657 of the 21,204
    ///     textured shapes drawn across three worldspaces.
    /// </summary>
    [Fact]
    public void ResolveDiffuse_TakesTheLayerAlbedoNotTheBlenderMask()
    {
        var db = StarfieldMaterialDatabase.Parse(BuildLayeredDatabase());

        Assert.NotNull(db);
        Assert.Equal(@"Data\Textures\Base_color.dds", db!.ResolveDiffuse(@"materials\test\layered.mat"));
    }

    /// <summary>
    ///     Even with no layer stack to descend, a blender's mask must never stand in for an albedo.
    /// </summary>
    [Fact]
    public void ResolveDiffuse_ExcludesBlenderTexturesFromTheFallbackDescent()
    {
        var db = StarfieldMaterialDatabase.Parse(BuildLayeredDatabase(true));

        Assert.NotNull(db);
        Assert.Equal(@"Data\Textures\Base_color.dds", db!.ResolveDiffuse(@"materials\test\layered.mat"));
    }

    /// <summary>
    ///     A slot with no image is not necessarily empty: an enabled <c>BSMaterial::TextureReplacement</c>
    ///     declares a flat colour instead (plain plastics, painted trim). Retail draws 3,129 such shapes
    ///     in three worldspaces, and reporting them as "no diffuse" is what left them white.
    /// </summary>
    [Fact]
    public void ResolveDiffuseSlot_ReturnsTheFlatReplacementColourWhenNoTextureIsAuthored()
    {
        var db = StarfieldMaterialDatabase.Parse(BuildLayeredDatabase(replacementInsteadOfTexture: true));

        Assert.NotNull(db);
        var slot = db!.ResolveDiffuseSlot(@"materials\test\layered.mat");

        Assert.Null(slot.TexturePath);
        // 0.8, 0.8, 0.8, 1.0 packed R8G8B8A8 with R in the low byte.
        Assert.Equal(0xFFCCCCCCu, slot.ReplacementRgba);
        Assert.True(slot.IsResolved);
    }

    /// <summary>
    ///     Reference semantics flatten a base object's components into the derived object before local
    ///     overrides (<c>copyBaseObject</c>), so a derived material that locally declares ONLY a decal
    ///     layer (index 1) still inherits the base's layer 0 as its base surface. Taking the first
    ///     base-chain level with any layers (the old behavior) resolved the decal's texture as the
    ///     albedo.
    /// </summary>
    [Fact]
    public void ResolveDiffuse_MergesInheritedLayersAcrossTheBaseChain()
    {
        var db = StarfieldMaterialDatabase.Parse(BuildInheritedLayerDatabase());

        Assert.NotNull(db);
        Assert.Equal(@"Data\Textures\Base_color.dds", db!.ResolveDiffuse(@"materials\test\derived.mat"));
    }

    /// <summary>
    ///     <c>Contains</c> separates "material exists but resolves no albedo" (occluders, normal-only
    ///     decals — their shapes are SKIPPED rather than drawn white) from "material missing" (broken
    ///     content, kept loudly visible on the white fallback).
    /// </summary>
    [Fact]
    public void Contains_DistinguishesPresentFromMissing()
    {
        var db = StarfieldMaterialDatabase.Parse(
            BuildLayeredDatabase(replacementInsteadOfTexture: true, replacementEnabled: false));

        Assert.NotNull(db);
        Assert.True(db!.Contains(@"materials\test\layered.mat"));
        Assert.False(db.ResolveDiffuseSlot(@"materials\test\layered.mat").IsResolved);
        Assert.False(db.Contains(@"materials\test\absent.mat"));
    }

    /// <summary>A replacement that is present but DISABLED is not a colour — the slot stays unresolved.</summary>
    [Fact]
    public void ResolveDiffuseSlot_IgnoresADisabledReplacement()
    {
        var db = StarfieldMaterialDatabase.Parse(
            BuildLayeredDatabase(replacementInsteadOfTexture: true, replacementEnabled: false));

        Assert.NotNull(db);
        var slot = db!.ResolveDiffuseSlot(@"materials\test\layered.mat");

        Assert.False(slot.IsResolved);
    }

    /// <summary>
    ///     Builds a CE2-shaped material: <c>material → layer → material → texture set</c>, plus a
    ///     blender carrying a mask at the same index the texture set uses for albedo.
    /// </summary>
    private static byte[] BuildLayeredDatabase(
        bool omitLayerStack = false,
        bool replacementInsteadOfTexture = false,
        bool replacementEnabled = true)
    {
        var strings = new List<string>
        {
            "BSComponentDB2::DBFileIndex::ObjectInfo",
            "BSComponentDB2::DBFileIndex::ComponentInfo",
            "BSComponentDB2::DBFileIndex::EdgeInfo",
            "BSMaterial::MRTextureFile",
            "BSMaterial::LayerID",
            "BSMaterial::BlenderID",
            "BSMaterial::MaterialID",
            "BSMaterial::TextureSetID",
            "BSMaterial::TextureReplacement"
        };

        var offsets = new Dictionary<string, uint>();
        var strt = new List<byte>();
        foreach (var s in strings)
        {
            offsets[s] = (uint)strt.Count;
            strt.AddRange(Encoding.ASCII.GetBytes(s));
            strt.Add(0);
        }

        const uint materialId = 1, layerId = 2, layerMaterialId = 3, textureSetId = 4;
        const uint blenderId = 5;
        var matResource = StarfieldMaterialDatabase.ComputeResourceId(@"materials\test\layered.mat");

        var chunks = new List<byte[]>();
        chunks.Add(Chunk("CLAS", Concat(
            U32(offsets["BSComponentDB2::DBFileIndex::ObjectInfo"]), U32(1), U16(0), U16(4))));

        var objects = new List<byte>();
        objects.AddRange(U32(offsets["BSComponentDB2::DBFileIndex::ObjectInfo"]));
        objects.AddRange(U32(5));
        objects.AddRange(ObjectRecord(matResource.File, matResource.Ext, matResource.Dir, materialId));
        objects.AddRange(ObjectRecord(0, 0, 0, layerId));
        objects.AddRange(ObjectRecord(0, 0, 0, layerMaterialId));
        objects.AddRange(ObjectRecord(0, 0, 0, textureSetId));
        objects.AddRange(ObjectRecord(0, 0, 0, blenderId));
        chunks.Add(Chunk("LIST", [.. objects]));

        // Component table — one entry per component chunk, in the same order.
        var owners = new List<(uint Owner, uint Slot)>();
        if (!omitLayerStack)
        {
            owners.Add((materialId, 0)); // LayerID
            owners.Add((layerId, 0)); // MaterialID
            owners.Add((layerMaterialId, 0)); // TextureSetID
        }

        owners.Add((materialId, 0)); // BlenderID
        owners.Add((textureSetId, 0)); // albedo (texture or replacement)
        owners.Add((blenderId, 0)); // the blender's mask

        var components = new List<byte>();
        components.AddRange(U32(offsets["BSComponentDB2::DBFileIndex::ComponentInfo"]));
        components.AddRange(U32((uint)owners.Count));
        foreach (var (owner, slot) in owners)
        {
            components.AddRange(Concat(U32(owner), U32(slot)));
        }

        chunks.Add(Chunk("LIST", [.. components]));

        // Edges (child → parent) so the fallback descent has a graph to walk.
        var edges = new List<byte>();
        edges.AddRange(U32(offsets["BSComponentDB2::DBFileIndex::EdgeInfo"]));
        edges.AddRange(U32(4));
        edges.AddRange(Concat(U32(layerId), U32(materialId), U32(0)));
        edges.AddRange(Concat(U32(blenderId), U32(materialId), U32(0)));
        edges.AddRange(Concat(U32(layerMaterialId), U32(layerId), U32(0)));
        edges.AddRange(Concat(U32(textureSetId), U32(materialId), U32(0)));
        chunks.Add(Chunk("LIST", [.. edges]));

        // Component payloads, OBJT form (fields packed sequentially after the class ref).
        if (!omitLayerStack)
        {
            chunks.Add(Chunk("OBJT", Concat(U32(offsets["BSMaterial::LayerID"]), U32(layerId))));
            chunks.Add(Chunk("OBJT", Concat(U32(offsets["BSMaterial::MaterialID"]), U32(layerMaterialId))));
            chunks.Add(Chunk("OBJT", Concat(U32(offsets["BSMaterial::TextureSetID"]), U32(textureSetId))));
        }

        chunks.Add(Chunk("OBJT", Concat(U32(offsets["BSMaterial::BlenderID"]), U32(blenderId))));

        var replacementEnabledByte = replacementEnabled ? (byte)1 : (byte)0;
        chunks.Add(replacementInsteadOfTexture
            ? Chunk("OBJT", Concat(
                U32(offsets["BSMaterial::TextureReplacement"]),
                [replacementEnabledByte],
                F32(0.8f), F32(0.8f), F32(0.8f), F32(1f)))
            : Chunk("OBJT", Concat(
                U32(offsets["BSMaterial::MRTextureFile"]), Str(@"Data\Textures\Base_color.dds"))));

        chunks.Add(Chunk("OBJT", Concat(
            U32(offsets["BSMaterial::MRTextureFile"]), Str(@"Data\Textures\Blend_mask.dds"))));

        var file = new List<byte>();
        file.AddRange(Encoding.ASCII.GetBytes("BETH"));
        file.AddRange(U32(8));
        file.AddRange(U32(4));
        file.AddRange(U32((uint)chunks.Count + 2));
        file.AddRange(Encoding.ASCII.GetBytes("STRT"));
        file.AddRange(U32((uint)strt.Count));
        file.AddRange(strt);
        foreach (var c in chunks) file.AddRange(c);
        return [.. file];
    }

    private static byte[] F32(float v)
    {
        return BitConverter.GetBytes(v);
    }

    /// <summary>
    ///     A base material with a full layer-0 stack (→ Base_color) and a DERIVED material
    ///     (<c>baseObject</c> = the base) that locally declares only a decal layer at index 1
    ///     (→ Decal_color). Correct resolution merges the chain's layer maps and takes index 0 from
    ///     the base; first-level-with-layers resolution returns the decal.
    /// </summary>
    private static byte[] BuildInheritedLayerDatabase()
    {
        var strings = new List<string>
        {
            "BSComponentDB2::DBFileIndex::ObjectInfo",
            "BSComponentDB2::DBFileIndex::ComponentInfo",
            "BSComponentDB2::DBFileIndex::EdgeInfo",
            "BSMaterial::MRTextureFile",
            "BSMaterial::LayerID",
            "BSMaterial::MaterialID",
            "BSMaterial::TextureSetID"
        };

        var offsets = new Dictionary<string, uint>();
        var strt = new List<byte>();
        foreach (var s in strings)
        {
            offsets[s] = (uint)strt.Count;
            strt.AddRange(Encoding.ASCII.GetBytes(s));
            strt.Add(0);
        }

        const uint baseMatId = 1, derivedMatId = 2;
        const uint baseLayerId = 3, baseLayerMatId = 4, baseTexSetId = 5;
        const uint decalLayerId = 6, decalLayerMatId = 7, decalTexSetId = 8;
        var baseResource = StarfieldMaterialDatabase.ComputeResourceId(@"materials\test\base.mat");
        var derivedResource = StarfieldMaterialDatabase.ComputeResourceId(@"materials\test\derived.mat");

        var chunks = new List<byte[]>();
        chunks.Add(Chunk("CLAS", Concat(
            U32(offsets["BSComponentDB2::DBFileIndex::ObjectInfo"]), U32(1), U16(0), U16(4))));

        var objects = new List<byte>();
        objects.AddRange(U32(offsets["BSComponentDB2::DBFileIndex::ObjectInfo"]));
        objects.AddRange(U32(8));
        objects.AddRange(ObjectRecord(baseResource.File, baseResource.Ext, baseResource.Dir, baseMatId));
        objects.AddRange(ObjectRecord(derivedResource.File, derivedResource.Ext, derivedResource.Dir,
            derivedMatId, baseMatId));
        objects.AddRange(ObjectRecord(0, 0, 0, baseLayerId));
        objects.AddRange(ObjectRecord(0, 0, 0, baseLayerMatId));
        objects.AddRange(ObjectRecord(0, 0, 0, baseTexSetId));
        objects.AddRange(ObjectRecord(0, 0, 0, decalLayerId));
        objects.AddRange(ObjectRecord(0, 0, 0, decalLayerMatId));
        objects.AddRange(ObjectRecord(0, 0, 0, decalTexSetId));
        chunks.Add(Chunk("LIST", [.. objects]));

        // Component table + payload chunks in the same (positional) order. The derived material's
        // LayerID sits at component slot 1 — a DECAL layer — which is the whole point.
        var owners = new (uint Owner, uint Slot)[]
        {
            (baseMatId, 0), // LayerID → baseLayer          (layer index 0)
            (baseLayerId, 0), // MaterialID → baseLayerMat
            (baseLayerMatId, 0), // TextureSetID → baseTexSet
            (baseTexSetId, 0), // MRTextureFile Base_color
            (derivedMatId, 1), // LayerID → decalLayer         (layer index 1)
            (decalLayerId, 0), // MaterialID → decalLayerMat
            (decalLayerMatId, 0), // TextureSetID → decalTexSet
            (decalTexSetId, 0) // MRTextureFile Decal_color
        };

        var components = new List<byte>();
        components.AddRange(U32(offsets["BSComponentDB2::DBFileIndex::ComponentInfo"]));
        components.AddRange(U32((uint)owners.Length));
        foreach (var (owner, slot) in owners)
        {
            components.AddRange(Concat(U32(owner), U32(slot)));
        }

        chunks.Add(Chunk("LIST", [.. components]));

        chunks.Add(Chunk("OBJT", Concat(U32(offsets["BSMaterial::LayerID"]), U32(baseLayerId))));
        chunks.Add(Chunk("OBJT", Concat(U32(offsets["BSMaterial::MaterialID"]), U32(baseLayerMatId))));
        chunks.Add(Chunk("OBJT", Concat(U32(offsets["BSMaterial::TextureSetID"]), U32(baseTexSetId))));
        chunks.Add(Chunk("OBJT", Concat(
            U32(offsets["BSMaterial::MRTextureFile"]), Str(@"Data\Textures\Base_color.dds"))));
        chunks.Add(Chunk("OBJT", Concat(U32(offsets["BSMaterial::LayerID"]), U32(decalLayerId))));
        chunks.Add(Chunk("OBJT", Concat(U32(offsets["BSMaterial::MaterialID"]), U32(decalLayerMatId))));
        chunks.Add(Chunk("OBJT", Concat(U32(offsets["BSMaterial::TextureSetID"]), U32(decalTexSetId))));
        chunks.Add(Chunk("OBJT", Concat(
            U32(offsets["BSMaterial::MRTextureFile"]), Str(@"Data\Textures\Decal_color.dds"))));

        var file = new List<byte>();
        file.AddRange(Encoding.ASCII.GetBytes("BETH"));
        file.AddRange(U32(8));
        file.AddRange(U32(4));
        file.AddRange(U32((uint)chunks.Count + 2));
        file.AddRange(Encoding.ASCII.GetBytes("STRT"));
        file.AddRange(U32((uint)strt.Count));
        file.AddRange(strt);
        foreach (var c in chunks) file.AddRange(c);
        return [.. file];
    }

    private static byte[] BuildDatabase(bool leadingStrayComponent = false, bool wideObjectInfo = false)
    {
        // STRT payload: every string the chunks reference, by byte offset.
        var strings = new List<string>
        {
            "BSComponentDB2::DBFileIndex::ObjectInfo", // 0
            "BSComponentDB2::DBFileIndex::ComponentInfo",
            "BSComponentDB2::DBFileIndex::EdgeInfo",
            "BSMaterial::MRTextureFile"
        };

        var offsets = new Dictionary<string, uint>();
        var strt = new List<byte>();
        foreach (var s in strings)
        {
            offsets[s] = (uint)strt.Count;
            strt.AddRange(Encoding.ASCII.GetBytes(s));
            strt.Add(0);
        }

        const uint materialDbId = 1;
        const uint textureSetDbId = 2;
        var matId = StarfieldMaterialDatabase.ComputeResourceId(@"materials\test\mat.mat");

        var chunks = new List<byte[]>();

        if (leadingStrayComponent)
        {
            chunks.Add(Chunk("OBJT", Concat(U32(offsets["BSMaterial::MRTextureFile"]), Str("stray.dds"))));
        }

        // CLAS for ObjectInfo: 4 fields ⇒ the launch-era 21-byte stride, 5 ⇒ retail's 33.
        chunks.Add(Chunk("CLAS", Concat(
            U32(offsets["BSComponentDB2::DBFileIndex::ObjectInfo"]), U32(1), U16(0),
            U16((ushort)(wideObjectInfo ? 5 : 4)))));

        // Two objects: the material file itself, and an (unkeyed) texture-set child.
        var objects = new List<byte>();
        objects.AddRange(U32(offsets["BSComponentDB2::DBFileIndex::ObjectInfo"]));
        objects.AddRange(U32(2));
        objects.AddRange(wideObjectInfo
            ? ObjectRecordWide(matId.File, matId.Ext, matId.Dir, materialDbId)
            : ObjectRecord(matId.File, matId.Ext, matId.Dir, materialDbId));
        objects.AddRange(wideObjectInfo
            ? ObjectRecordWide(0, 0, 0, textureSetDbId)
            : ObjectRecord(0, 0, 0, textureSetDbId));
        chunks.Add(Chunk("LIST", [.. objects]));

        // Both texture components belong to the texture set; slots 0 and 1.
        var components = new List<byte>();
        components.AddRange(U32(offsets["BSComponentDB2::DBFileIndex::ComponentInfo"]));
        components.AddRange(U32(2));
        components.AddRange(Concat(U32(textureSetDbId), U32(0)));
        components.AddRange(Concat(U32(textureSetDbId), U32(1)));
        chunks.Add(Chunk("LIST", [.. components]));

        // Edge: source (child) = texture set, target (parent) = material.
        var edges = new List<byte>();
        edges.AddRange(U32(offsets["BSComponentDB2::DBFileIndex::EdgeInfo"]));
        edges.AddRange(U32(1));
        edges.AddRange(Concat(U32(textureSetDbId), U32(materialDbId), U32(0)));
        chunks.Add(Chunk("LIST", [.. edges]));

        // DIFF payloads: class ref, u16 field index 0, then the length-prefixed file name.
        foreach (var name in new[] { @"Data\Textures\Ground\Dirt_color.dds", @"Data\Textures\Ground\Dirt_normal.dds" })
        {
            chunks.Add(Chunk("DIFF", Concat(
                U32(offsets["BSMaterial::MRTextureFile"]), U16(0), Str(name))));
        }

        var file = new List<byte>();
        file.AddRange(Encoding.ASCII.GetBytes("BETH"));
        file.AddRange(U32(8));
        file.AddRange(U32(4)); // version
        file.AddRange(U32((uint)chunks.Count + 2)); // total chunks, incl. BETH + STRT
        file.AddRange(Encoding.ASCII.GetBytes("STRT"));
        file.AddRange(U32((uint)strt.Count));
        file.AddRange(strt);
        foreach (var c in chunks) file.AddRange(c);
        return [.. file];
    }

    /// <summary>
    ///     One 21-byte ObjectInfo record (the launch-era layout the reference defaults to):
    ///     file/ext/dir/dbID/baseObject then the <c>hasData</c> flag as the last byte of the stride.
    ///     The stride is what the reader walks by, so a wrong length here silently shears every
    ///     subsequent record — which is exactly what the old 28-byte assumption did.
    /// </summary>
    private static byte[] ObjectRecord(uint file, uint ext, uint dir, uint dbId, uint baseId = 0)
    {
        return Concat(U32(file), U32(ext), U32(dir), U32(dbId), U32(baseId), [1]);
    }

    /// <summary>
    ///     One 33-byte ObjectInfo record — the layout current retail announces via a 5-field CLAS
    ///     (build ≥ 1.11.33 appends a 12-byte parent BSResourceID before <c>hasData</c>).
    /// </summary>
    private static byte[] ObjectRecordWide(uint file, uint ext, uint dir, uint dbId, uint baseId = 0)
    {
        return Concat(U32(file), U32(ext), U32(dir), U32(dbId), U32(baseId), new byte[12], [1]);
    }

    private static byte[] Chunk(string tag, byte[] body)
    {
        return Concat(Encoding.ASCII.GetBytes(tag), U32((uint)body.Length), body);
    }

    private static byte[] Str(string value)
    {
        return Concat(U16((ushort)value.Length), Encoding.ASCII.GetBytes(value));
    }

    private static byte[] U32(uint v)
    {
        return BitConverter.GetBytes(v);
    }

    private static byte[] U16(ushort v)
    {
        return BitConverter.GetBytes(v);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new List<byte>();
        foreach (var p in parts) result.AddRange(p);
        return [.. result];
    }
}
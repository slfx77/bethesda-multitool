using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Nif.Materials;

/// <summary>
///     Reader for Starfield's compiled material database (<c>materials\materialsbeta.cdb</c>), a
///     <c>BETH</c>/<c>BSComponentDB2</c> reflection stream.
///     <para>
///         Starfield has no per-file materials: both halves of rendering funnel through here. A mesh's
///         shader names a <c>.mat</c> path (the same "Name is a material path" route Fallout 76 uses
///         for <c>.bgsm</c>), and a landscape texture's <c>LTEX.BNAM</c> names one too. Neither can
///         resolve a single texture without this database.
///     </para>
///     <para>
///         The stream is self-describing: <c>TYPE</c> introduces <c>CLAS</c> chunks whose field lists
///         are what make the object payloads walkable at all. Chunks are length-prefixed, so this
///         reader decodes only the classes it needs and skips the rest wholesale — but it still
///         COUNTS every object chunk, because component payloads are paired with the component table
///         positionally rather than by any embedded key.
///     </para>
///     Derived from the MIT-licensed <c>libfo76utils</c> (<c>bsrefl</c>/<c>bsmatcdb</c>) vendored under
///     <c>Sample/Reference_Code/nifskope/lib/</c>.
/// </summary>
internal sealed class StarfieldMaterialDatabase
{
    private const uint ChunkBeth = 0x48544542; // 'BETH'
    private const uint ChunkStrt = 0x54525453;
    private const uint ChunkType = 0x45505954;
    private const uint ChunkClas = 0x53414C43;
    private const uint ChunkList = 0x5453494C;
    private const uint ChunkMapc = 0x4350414D;
    private const uint ChunkObjt = 0x544A424F;
    private const uint ChunkDiff = 0x46464944;

    private const uint SupportedVersion = 4;
    private const int HeaderSize = 24;

    /// <summary>Diffuse/albedo is texture slot 0; normal is slot 1 (confirmed against authored .mat JSON).</summary>
    private const int DiffuseSlot = 0;

    private const int NormalSlot = 1;

    /// <summary>dbID → the texture files that object's own components declare, by slot index.</summary>
    private readonly Dictionary<uint, Dictionary<int, string>> _texturesByObject = [];

    /// <summary>
    ///     Material dbID → its layer objects by layer index. A CE2 material is a STACK of layers, and
    ///     layer 0 is the base surface whose albedo the shape actually shows.
    /// </summary>
    private readonly Dictionary<uint, Dictionary<int, uint>> _layersByObject = [];

    /// <summary>Layer dbID → the material object it uses.</summary>
    private readonly Dictionary<uint, uint> _materialByLayer = [];

    /// <summary>Material dbID → the texture-set object it uses.</summary>
    private readonly Dictionary<uint, uint> _textureSetByMaterial = [];

    /// <summary>
    ///     Texture-set dbID → per-slot flat colour standing in for an absent texture
    ///     (<c>BSMaterial::TextureReplacement</c>). Enabled replacements are how a material declares
    ///     "this slot is a solid colour, not an image" — plain plastic, painted metal, and so on.
    /// </summary>
    private readonly Dictionary<uint, Dictionary<int, (bool Enabled, uint Rgba)>> _replacementsByObject = [];

    /// <summary>
    ///     Objects reached through a <c>BSMaterial::BlenderID</c>. A blender's own texture is the MASK
    ///     that mixes two layers, and it sits at index 0 exactly like a texture set's albedo — so
    ///     without this set a generic descent happily returns a <c>_mask</c> as the albedo. That is a
    ///     single-channel image, which renders PURE RED when sampled as RGB.
    /// </summary>
    private readonly HashSet<uint> _blenderObjects = [];

    /// <summary>parent dbID → its child dbIDs, from the edge table.</summary>
    private readonly Dictionary<uint, List<uint>> _childrenByObject = [];

    /// <summary>dbID → the object it inherits from (shader-model templates like LayeredMaterials.mat).</summary>
    private readonly Dictionary<uint, uint> _baseByObject = [];

    /// <summary>Resource ID (dir, file, ext CRCs) → dbID, for resolving a <c>.mat</c> path.</summary>
    private readonly Dictionary<(uint Dir, uint File, uint Ext), uint> _objectByResourceId = [];

    /// <summary>Objects whose components were successfully decoded (diagnostics).</summary>
    public int TextureObjectCount => _texturesByObject.Count;

    /// <summary>Objects the file index declared (diagnostics).</summary>
    public int ObjectCount { get; private set; }

    /// <summary>ComponentInfo entries seen (diagnostics).</summary>
    public int ComponentTableCount { get; private set; }

    /// <summary>OBJT/DIFF chunks seen (diagnostics). Must equal ComponentTableCount.</summary>
    public int ComponentChunkCount { get; private set; }

    /// <summary>Per-object texture slots. Exposed for format diagnostics.</summary>
    internal IReadOnlyDictionary<uint, Dictionary<int, string>> TexturesByObject => _texturesByObject;

    /// <summary>File-backed objects keyed by resource ID. Exposed for format diagnostics.</summary>
    internal IReadOnlyDictionary<(uint Dir, uint File, uint Ext), uint> ResourceIds => _objectByResourceId;

    /// <summary>Reflected CRC-32 of an ASCII string, lowercased. Exposed for format diagnostics.</summary>
    internal static uint DiagnosticCrc(string value) => Crc32Lower(value);

    /// <summary>
    ///     Parses one <c>.cdb</c>. Returns null rather than throwing when the buffer is not a version-4
    ///     reflection stream or is truncated — a missing/む unreadable material database must degrade to
    ///     untextured rendering, never take the viewer down.
    /// </summary>
    public static StarfieldMaterialDatabase? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(data) != ChunkBeth ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 8 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != SupportedVersion)
        {
            return null;
        }

        var chunksRemaining = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        if (chunksRemaining < 2 || BinaryPrimitives.ReadUInt32LittleEndian(data[16..]) != ChunkStrt)
        {
            return null;
        }

        var strtSize = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        if (HeaderSize + (long)strtSize > data.Length)
        {
            return null;
        }

        var db = new StarfieldMaterialDatabase();
        var strings = BuildStringTable(data.Slice(HeaderSize, (int)strtSize));
        chunksRemaining -= 2;

        var pos = HeaderSize + (int)strtSize;
        db.ReadChunks(data, ref pos, chunksRemaining, strings);
        return db;
    }

    /// <summary>
    ///     Resolves a <c>materials\...\*.mat</c> path to its diffuse texture path, or null when the
    ///     material is absent or declares none.
    /// </summary>
    public string? ResolveDiffuse(string materialPath) => ResolveSlot(materialPath, DiffuseSlot).TexturePath;

    /// <summary>Resolves a material path to its normal-map texture path, or null.</summary>
    public string? ResolveNormal(string materialPath) => ResolveSlot(materialPath, NormalSlot).TexturePath;

    /// <summary>Resolves the albedo slot, which may be a texture path OR a flat replacement colour.</summary>
    internal StarfieldMaterialSlot ResolveDiffuseSlot(string materialPath) =>
        ResolveSlot(materialPath, DiffuseSlot);

    /// <summary>
    ///     True when the database has an object for <paramref name="materialPath" /> at all —
    ///     distinguishes "material exists but resolves no albedo" (occluders, normal-only decals:
    ///     legitimately drawn without a diffuse, or not at all) from "material missing" (broken
    ///     content, worth surfacing loudly).
    /// </summary>
    internal bool Contains(string materialPath) =>
        !string.IsNullOrWhiteSpace(materialPath) &&
        _objectByResourceId.ContainsKey(ComputeResourceId(materialPath));

    /// <summary>Resolves the normal slot, which may be a texture path OR a flat replacement colour.</summary>
    internal StarfieldMaterialSlot ResolveNormalSlot(string materialPath) =>
        ResolveSlot(materialPath, NormalSlot);

    /// <summary>
    ///     Resolves one texture slot of a material, following the CE2 object model:
    ///     <c>material → layer[0] → material → texture set → slot</c>.
    ///     <para>
    ///         ⚠ Descending the edge graph generically and taking the first slot-N texture found is
    ///         WRONG, even though it returns a real path belonging to the right material. A material
    ///         owns both LAYERS and BLENDERS; a blender's texture is the mask that mixes two layers and
    ///         is stored at index 0, the same index a texture set uses for albedo. Measured on retail:
    ///         22% of drawn shapes resolved a <c>_mask</c> that way, and a single-channel mask sampled
    ///         as RGB renders pure red. Going through layer 0 is what distinguishes them.
    ///     </para>
    ///     <para>
    ///         A slot with no texture is not necessarily empty: an enabled
    ///         <c>BSMaterial::TextureReplacement</c> declares a flat colour instead of an image (plain
    ///         plastics, painted trim). Those are 26% of drawn shapes, and treating them as "no diffuse"
    ///         is what leaves them untextured.
    ///     </para>
    /// </summary>
    internal StarfieldMaterialSlot ResolveSlot(string materialPath, int slot)
    {
        if (string.IsNullOrWhiteSpace(materialPath))
        {
            return default;
        }

        var id = ComputeResourceId(materialPath);
        if (!_objectByResourceId.TryGetValue(id, out var root))
        {
            return default;
        }

        // Layer 0 is the base surface. Later layers are decals/wear composited over it, so they are
        // deliberately not consulted for the base albedo.
        if (LowestLayer(root) is { } layer)
        {
            var material = Inherited(_materialByLayer, layer);
            var textureSet = material != 0 ? Inherited(_textureSetByMaterial, material) : 0;
            if (textureSet != 0 && SlotFromTextureSet(textureSet, slot) is { IsResolved: true } fromLayer)
            {
                return fromLayer;
            }
        }

        // Fallback for materials that declare a texture set without the full layer stack. Still a
        // descent, but blender objects are excluded so it cannot return a mask as albedo.
        return FindInSubtree(root, slot);
    }

    /// <summary>
    ///     The layer object at the lowest declared index across the WHOLE inheritance chain. 0 when
    ///     none. Reference semantics (<c>bsmatcdb.cpp copyBaseObject</c>): the effective layer map is
    ///     the union of every base level with the nearest declaration winning per index — a derived
    ///     material that locally overrides only a decal layer (index ≥ 1) still inherits the base's
    ///     layer 0 as its base surface. Returning at the FIRST level that declares any layers (the
    ///     old behavior) resolved that decal's texture as the base albedo.
    /// </summary>
    private uint? LowestLayer(uint root)
    {
        var visited = new HashSet<uint>();
        var seenIndices = new HashSet<int>();
        var lowest = int.MaxValue;
        uint pick = 0;
        for (var current = root; current != 0 && visited.Add(current);)
        {
            if (_layersByObject.TryGetValue(current, out var layers))
            {
                foreach (var (index, dbId) in layers)
                {
                    // Nearest level wins per index: a base's declaration only counts for indices the
                    // derived chain has not already declared.
                    if (dbId == 0 || !seenIndices.Add(index))
                    {
                        continue;
                    }

                    if (index < lowest)
                    {
                        lowest = index;
                        pick = dbId;
                    }
                }
            }

            current = _baseByObject.GetValueOrDefault(current);
        }

        return pick != 0 ? pick : null;
    }

    /// <summary>Reads <paramref name="map" /> at <paramref name="obj" />, then up its base chain.</summary>
    private uint Inherited(Dictionary<uint, uint> map, uint obj)
    {
        var visited = new HashSet<uint>();
        for (var current = obj; current != 0 && visited.Add(current);)
        {
            if (map.TryGetValue(current, out var value) && value != 0)
            {
                return value;
            }

            current = _baseByObject.GetValueOrDefault(current);
        }

        return 0;
    }

    /// <summary>
    ///     A texture set's slot: its own texture, else its flat-colour replacement, else whatever it
    ///     inherits. Checked in that order at EACH level so a derived set's own replacement beats the
    ///     base set's image (that is exactly how a recoloured variant is authored).
    /// </summary>
    private StarfieldMaterialSlot SlotFromTextureSet(uint textureSet, int slot)
    {
        var visited = new HashSet<uint>();
        for (var current = textureSet; current != 0 && visited.Add(current);)
        {
            if (_texturesByObject.TryGetValue(current, out var textures) &&
                textures.TryGetValue(slot, out var path) && path.Length > 0)
            {
                return new StarfieldMaterialSlot(path, null);
            }

            if (_replacementsByObject.TryGetValue(current, out var replacements) &&
                replacements.TryGetValue(slot, out var replacement) && replacement.Enabled)
            {
                return new StarfieldMaterialSlot(null, replacement.Rgba);
            }

            current = _baseByObject.GetValueOrDefault(current);
        }

        return default;
    }

    /// <summary>
    ///     Finds slot <paramref name="slot" /> on <paramref name="root" /> or the nearest object
    ///     beneath it, by breadth-first descent through the edge table (material → layer → material →
    ///     texture set). Breadth-first matters: it takes the shallowest match, so a material's own
    ///     texture set wins over one belonging to a deeper LOD or blender sub-object.
    ///     <para>
    ///         An earlier version instead scanned every textured object and asked "is root an
    ///         ancestor?". That looked equivalent and was not — it returned whichever textured object
    ///         the dictionary happened to enumerate first, which produced confidently wrong answers
    ///         (a Leaning-Tower-of-Pisa texture for a New Atlantis lodge). Descending is the only form
    ///         that is actually anchored to the material being asked about.
    ///     </para>
    /// </summary>
    private StarfieldMaterialSlot FindInSubtree(uint root, int slot)
    {
        var queue = new Queue<uint>();
        var seen = new HashSet<uint>();
        queue.Enqueue(root);
        seen.Add(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            // Never let a blender's mask stand in for an albedo — see ResolveSlot.
            if (!_blenderObjects.Contains(current))
            {
                if (_texturesByObject.TryGetValue(current, out var slots) &&
                    slots.TryGetValue(slot, out var path) && path.Length > 0)
                {
                    return new StarfieldMaterialSlot(path, null);
                }

                if (_replacementsByObject.TryGetValue(current, out var replacements) &&
                    replacements.TryGetValue(slot, out var replacement) && replacement.Enabled)
                {
                    return new StarfieldMaterialSlot(null, replacement.Rgba);
                }
            }

            if (!_childrenByObject.TryGetValue(current, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (seen.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return default;
    }

    private static Dictionary<uint, string> BuildStringTable(ReadOnlySpan<byte> payload)
    {
        // String references are BYTE OFFSETS into this payload, not indices.
        var table = new Dictionary<uint, string>();
        var start = 0;
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload[i] != 0)
            {
                continue;
            }

            table[(uint)start] = Encoding.ASCII.GetString(payload[start..i]);
            start = i + 1;
        }

        return table;
    }

    private void ReadChunks(
        ReadOnlySpan<byte> data, ref int pos, uint chunksRemaining, Dictionary<uint, string> strings)
    {
        var componentOwners = new List<(uint Owner, int Slot)>();
        var componentIndex = 0;
        // 21 = file 4 + ext 4 + dir 4 + dbID 4 + baseObject 4 + hasData 1, matching the reference
        // (bsmatcdb.cpp: objectInfoSize starts at 21, 33 only when the CLAS reports > 4 fields). There
        // is NO 28-byte layout: a 28 default walks any launch-era (pre-1.11.33) cdb at the wrong
        // stride and silently decodes the whole object table as garbage. Current retail always
        // announces 5 fields, so this default only matters for old files.
        var objectInfoSize = 21;


        while (chunksRemaining-- > 0 && TryReadChunk(data, ref pos, out var type, out var body))
        {
            if (type == ChunkType)
            {
                // A TYPE chunk only announces how many CLAS chunks follow; they are siblings, not nested.
                continue;
            }

            if (body.Length < 4)
            {
                continue;
            }

            var className = strings.GetValueOrDefault(BinaryPrimitives.ReadUInt32LittleEndian(body), string.Empty);

            if (type == ChunkClas)
            {
                ReadClassDefinition(body, className, ref objectInfoSize);
            }
            else if (type == ChunkList || type == ChunkMapc)
            {
                ReadList(body, type, className, componentOwners, objectInfoSize);
            }
            else if (type == ChunkObjt || type == ChunkDiff)
            {
                // Positional pairing: chunk N belongs to component N, whatever its class.
                ComponentChunkCount++;

                // Object chunks that appear BEFORE the component table has been read describe the
                // file index itself, not components, and must not consume a table slot. Counting them
                // shifts every later pairing by the same amount — which does not fail loudly, it just
                // returns a neighbouring material's texture (measured: a 2-chunk lead-in shifted the
                // whole database and produced same-folder-wrong-file results).
                if (componentOwners.Count == 0)
                {
                    continue;
                }

                var entry = componentIndex < componentOwners.Count
                    ? componentOwners[componentIndex]
                    : default;
                componentIndex++;
                if (entry.Owner != 0)
                {
                    ReadComponent(body, className, entry.Owner, entry.Slot, type == ChunkDiff);
                }
            }
        }

        ComponentTableCount = componentOwners.Count;
    }

    /// <summary>
    ///     Dispatches one component payload by class. Only the classes that contribute to resolving a
    ///     surface are decoded; every other component is skipped wholesale (the chunk is
    ///     length-prefixed, so skipping costs nothing and unknown classes cannot desynchronise the
    ///     positional pairing).
    /// </summary>
    private void ReadComponent(ReadOnlySpan<byte> body, string className, uint owner, int slot, bool isDiff)
    {
        switch (className)
        {
            case "BSMaterial::MRTextureFile":
            case "BSMaterial::TextureFile":
                ReadTextureFile(body, owner, slot, isDiff);
                break;

            // Each of these wraps a single BSComponentDB2::ID (a 4-byte dbID reference).
            case "BSMaterial::LayerID":
                if (TryReadIdComponent(body, isDiff, out var layerId) && layerId != 0)
                {
                    if (!_layersByObject.TryGetValue(owner, out var layers))
                    {
                        _layersByObject[owner] = layers = [];
                    }

                    layers[slot] = layerId;
                }

                break;

            case "BSMaterial::BlenderID":
                if (TryReadIdComponent(body, isDiff, out var blenderId) && blenderId != 0)
                {
                    _blenderObjects.Add(blenderId);
                }

                break;

            case "BSMaterial::MaterialID":
                if (TryReadIdComponent(body, isDiff, out var materialId) && materialId != 0)
                {
                    _materialByLayer[owner] = materialId;
                }

                break;

            case "BSMaterial::TextureSetID":
                if (TryReadIdComponent(body, isDiff, out var textureSetId) && textureSetId != 0)
                {
                    _textureSetByMaterial[owner] = textureSetId;
                }

                break;

            case "BSMaterial::TextureReplacement":
                ReadTextureReplacement(body, owner, slot, isDiff);
                break;
        }
    }

    /// <summary>
    ///     Reads the dbID out of a component whose sole field is a <c>BSComponentDB2::ID</c>.
    ///     <para>
    ///         <c>OBJT</c> packs fields sequentially, so the ID is simply the dword after the class
    ///         reference. <c>DIFF</c> prefixes every field with a u16 index and closes with 0xFFFF, and
    ///         because <c>BSComponentDB2::ID</c> is itself a class its single field is nested the same
    ///         way — hence TWO index words before the value.
    ///     </para>
    /// </summary>
    private static bool TryReadIdComponent(ReadOnlySpan<byte> body, bool isDiff, out uint dbId)
    {
        dbId = 0;
        var pos = 4; // past the class-name dword

        if (isDiff)
        {
            // Outer field 0 (ID), then the nested BSComponentDB2::ID's own field 0 (Value).
            if (pos + 4 > body.Length ||
                BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]) != 0 ||
                BinaryPrimitives.ReadUInt16LittleEndian(body[(pos + 2)..]) != 0)
            {
                return false;
            }

            pos += 4;
        }

        if (pos + 4 > body.Length)
        {
            return false;
        }

        dbId = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);
        return true;
    }

    /// <summary>
    ///     Reads a <c>BSMaterial::TextureReplacement</c>: an <c>Enabled</c> bool plus a
    ///     <c>BSMaterial::Color</c> (an XMFLOAT4). When enabled, this slot is a flat colour rather than
    ///     an image — the material genuinely has no texture there and rendering must use the colour,
    ///     not fall back to white.
    /// </summary>
    private void ReadTextureReplacement(ReadOnlySpan<byte> body, uint owner, int slot, bool isDiff)
    {
        var enabled = false;
        var rgba = 0xFFFFFFFFu;
        var sawColor = false;

        if (isDiff)
        {
            // Indexed fields until the 0xFFFF terminator: 0 = Enabled (bool), 1 = Color (nested).
            var pos = 4;
            while (pos + 2 <= body.Length)
            {
                var field = BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]);
                pos += 2;
                if (field == 0xFFFF)
                {
                    break;
                }

                if (field == 0)
                {
                    if (pos >= body.Length) break;
                    enabled = body[pos] != 0;
                    pos++;
                }
                else if (field == 1)
                {
                    // Nested BSMaterial::Color → its field 0 is the XMFLOAT4, whose four floats are
                    // themselves indexed. Read them, then consume both closing terminators.
                    if (pos + 2 > body.Length) break;
                    pos += 2; // Color's field 0 (Value)
                    if (TryReadIndexedFloat4(body, ref pos, out rgba))
                    {
                        sawColor = true;
                    }

                    break;
                }
                else
                {
                    break;
                }
            }
        }
        else
        {
            // Sequential: class ref, Enabled byte, then four floats.
            if (body.Length < 5 + 16)
            {
                return;
            }

            enabled = body[4] != 0;
            rgba = PackColor(
                BitConverter.ToSingle(body.Slice(5, 4)),
                BitConverter.ToSingle(body.Slice(9, 4)),
                BitConverter.ToSingle(body.Slice(13, 4)),
                BitConverter.ToSingle(body.Slice(17, 4)));
            sawColor = true;
        }

        if (!sawColor && !enabled)
        {
            return;
        }

        if (!_replacementsByObject.TryGetValue(owner, out var slots))
        {
            _replacementsByObject[owner] = slots = [];
        }

        // An Enabled-only chunk and a Color-only chunk can arrive separately for the same slot.
        var existing = slots.GetValueOrDefault(slot, (Enabled: false, Rgba: 0xFFFFFFFFu));
        slots[slot] = (enabled || existing.Enabled, sawColor ? rgba : existing.Rgba);
    }

    /// <summary>Reads an indexed XMFLOAT4 (fields 0..3) and packs it to RGBA8.</summary>
    private static bool TryReadIndexedFloat4(ReadOnlySpan<byte> body, ref int pos, out uint rgba)
    {
        Span<float> channels = [0f, 0f, 0f, 1f];
        var any = false;
        while (pos + 2 <= body.Length)
        {
            var field = BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]);
            pos += 2;
            if (field == 0xFFFF)
            {
                break;
            }

            if (field > 3 || pos + 4 > body.Length)
            {
                break;
            }

            channels[field] = BitConverter.ToSingle(body.Slice(pos, 4));
            pos += 4;
            any = true;
        }

        rgba = PackColor(channels[0], channels[1], channels[2], channels[3]);
        return any;
    }

    private static uint PackColor(float r, float g, float b, float a)
    {
        static uint Channel(float v) => (uint)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        return Channel(r) | (Channel(g) << 8) | (Channel(b) << 16) | (Channel(a) << 24);
    }

    private static bool TryReadChunk(
        ReadOnlySpan<byte> data, ref int pos, out uint type, out ReadOnlySpan<byte> body)
    {
        type = 0;
        body = default;
        if (pos + 8 > data.Length)
        {
            return false;
        }

        type = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
        if (size > (uint)(data.Length - pos - 8))
        {
            return false;
        }

        body = data.Slice(pos + 8, (int)size);
        pos += 8 + (int)size;
        return true;
    }

    private static void ReadClassDefinition(ReadOnlySpan<byte> body, string className, ref int objectInfoSize)
    {
        if (body.Length < 12)
        {
            return;
        }

        var fieldCount = BinaryPrimitives.ReadUInt16LittleEndian(body[10..]);

        // The object table's stride is version-dependent and announced ONLY here: builds >= 1.11.33.0
        // append a parent BSResourceID, which shows up as this class gaining a fifth field. Guessing
        // 28 against a 33-byte table shears every subsequent record.
        if (className == "BSComponentDB2::DBFileIndex::ObjectInfo" && fieldCount > 4)
        {
            objectInfoSize = 33;
        }

    }

    private void ReadList(
        ReadOnlySpan<byte> body,
        uint chunkType,
        string className,
        List<(uint Owner, int Slot)> componentOwners,
        int objectInfoSize)
    {
        // MAPC names two classes (key + value); its count follows the second.
        var pos = chunkType == ChunkMapc ? 8 : 4;
        if (pos + 4 > body.Length)
        {
            return;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);
        pos += 4;

        switch (className)
        {
            case "BSComponentDB2::DBFileIndex::ObjectInfo":
                ReadObjectInfo(body, pos, count, objectInfoSize);
                break;

            case "BSComponentDB2::DBFileIndex::ComponentInfo":
                for (var i = 0u; i < count && pos + 8 <= body.Length; i++, pos += 8)
                {
                    // key = (classNameStringId << 16) | index; the low half is the component's SLOT,
                    // which is what orders a texture set's colour/normal/opacity entries.
                    var key = BinaryPrimitives.ReadUInt32LittleEndian(body[(pos + 4)..]);
                    componentOwners.Add((BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]), (int)(key & 0xFFFF)));
                }

                break;

            case "BSComponentDB2::DBFileIndex::EdgeInfo":
                for (var i = 0u; i < count && pos + 12 <= body.Length; i++, pos += 12)
                {
                    var source = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);
                    var target = BinaryPrimitives.ReadUInt32LittleEndian(body[(pos + 4)..]);
                    if (source != 0 && target != 0)
                    {
                        // source is the CHILD, target its parent — so index the reverse direction,
                        // which is what a material-to-textures walk actually needs.
                        if (!_childrenByObject.TryGetValue(target, out var kids))
                        {
                            _childrenByObject[target] = kids = [];
                        }

                        kids.Add(source);
                    }
                }

                break;
        }
    }

    private void ReadObjectInfo(ReadOnlySpan<byte> body, int pos, uint count, int objectInfoSize)
    {
        for (var i = 0u; i < count && pos + objectInfoSize <= body.Length; i++, pos += objectInfoSize)
        {
            var record = body.Slice(pos, objectInfoSize);
            var file = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var ext = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            var dir = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
            var dbId = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            var baseId = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
            if (dbId == 0)
            {
                continue;
            }

            ObjectCount++;
            if (baseId != 0)
            {
                _baseByObject[dbId] = baseId;
            }

            // Only file-backed objects carry a real path hash; synthetic sub-objects have none, and
            // first-wins matches the reference's duplicate handling.
            if ((dir | file | ext) != 0)
            {
                _objectByResourceId.TryAdd((dir, file, ext), dbId);
            }
        }
    }

    /// <summary>
    ///     Reads a texture-file component's <c>FileName</c> and files it under
    ///     <paramref name="slot" />.
    ///     <para>
    ///         Both <c>BSMaterial::MRTextureFile</c> and <c>BSMaterial::TextureFile</c> declare exactly
    ///         one field, <c>FileName</c>, holding a length-prefixed string — verified against the
    ///         file's own CLAS definitions. Their field TYPE cannot be checked here: primitive types
    ///         live in the format's built-in string table, not the file's STRT, so they do not resolve
    ///         and an earlier version of this method bailed on every single component because of it.
    ///     </para>
    ///     A <c>DIFF</c> chunk prefixes the field with its u16 index; an <c>OBJT</c> chunk does not.
    /// </summary>
    private void ReadTextureFile(ReadOnlySpan<byte> body, uint owner, int slot, bool isDiff)
    {
        var pos = 4; // past the class-name dword
        if (isDiff)
        {
            if (pos + 2 > body.Length || BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]) != 0)
            {
                return; // only field 0 (FileName) is meaningful on these classes
            }

            pos += 2;
        }

        if (pos + 2 > body.Length)
        {
            return;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]);
        pos += 2;
        if (length == 0 || pos + length > body.Length)
        {
            return;
        }

        // The stored length INCLUDES the null terminator, so the raw decode carries a trailing NUL.
        // Left in, it survives every later normalise/compare and the archive lookup misses a texture
        // that is right there — a failure that reads as "material has no diffuse".
        var value = Encoding.ASCII.GetString(body.Slice(pos, length)).TrimEnd('\0', ' ');
        if (!_texturesByObject.TryGetValue(owner, out var slots))
        {
            slots = [];
            _texturesByObject[owner] = slots;
        }

        slots.TryAdd(slot, value);
    }

    /// <summary>
    ///     Computes a path's <c>BSResourceID</c>: CRC-32 of the lowercased directory and base name
    ///     (<c>/</c> normalised to <c>\</c>), plus the extension packed as raw ASCII — see
    ///     <see cref="PackExtension" /> for why that last one is not a hash.
    /// </summary>
    internal static (uint Dir, uint File, uint Ext) ComputeResourceId(string path)
    {
        var normalized = path.Trim().Replace('/', '\\').TrimStart('\\');

        // Material references appear both Data-relative ("materials\...", from an LTEX BNAM or a
        // shader Name) and Data-rooted ("Data\Materials\...", how .mat Import/Parent links are
        // written). The database keys on the former, so peel the root.
        if (normalized.StartsWith("data\\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[5..];
        }

        var lastSeparator = normalized.LastIndexOf('\\');
        var lastDot = normalized.LastIndexOf('.');
        if (lastDot < lastSeparator)
        {
            lastDot = -1;
        }

        var dir = lastSeparator > 0 ? normalized[..lastSeparator] : string.Empty;
        var nameStart = lastSeparator + 1;
        var nameEnd = lastDot >= 0 ? lastDot : normalized.Length;
        var name = normalized[nameStart..nameEnd];
        var ext = lastDot >= 0 && lastDot + 1 < normalized.Length ? normalized[(lastDot + 1)..] : string.Empty;

        return (Crc32Lower(dir), Crc32Lower(name), PackExtension(ext));
    }

    /// <summary>
    ///     Packs a file extension the way the database stores it: up to four lowercase ASCII bytes in
    ///     LITTLE-ENDIAN order, zero-padded — NOT a CRC.
    ///     <para>
    ///         This is the one field that breaks the pattern, and it is invisible until you compare
    ///         against real data: <c>.mat</c> stores as <c>0x0074616D</c> ("mat\0"). Hashing it like the
    ///         directory and base name yields a value that is stable and plausible and matches nothing,
    ///         so every lookup silently returns "material not found".
    ///     </para>
    /// </summary>
    private static uint PackExtension(string extension)
    {
        var packed = 0u;
        var count = Math.Min(extension.Length, 4);
        for (var i = 0; i < count; i++)
        {
            var c = extension[i];
            packed |= (uint)(byte)(c is >= 'A' and <= 'Z' ? c | 0x20 : c) << (i * 8);
        }

        return packed;
    }

    /// <summary>
    ///     Reflected CRC-32 (polynomial 0xEDB88320) over the ASCII lowercase of the input, with
    ///     <c>init = 0</c> and NO final inversion — the variant the engine's resource IDs use, which is
    ///     not the same as a standard CRC-32 checksum.
    /// </summary>
    private static uint Crc32Lower(string value)
    {
        var crc = 0u;
        foreach (var ch in value)
        {
            var c = (byte)(ch is >= 'A' and <= 'Z' ? ch | 0x20 : ch);
            crc = (crc >> 8) ^ Crc32Table[(crc ^ c) & 0xFF];
        }

        return crc;
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (var i = 0u; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}

using System.Buffers.Binary;
using System.Numerics;
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

    /// <summary>CE2 texture-set slot 2 is the red-channel opacity map.</summary>
    private const int OpacitySlot = 2;

    /// <summary>CE2 scalar PBR maps occupy slots 3..5 and are sampled from their red channels.</summary>
    private const int RoughnessSlot = 3;
    private const int MetalnessSlot = 4;
    private const int AmbientOcclusionSlot = 5;

    // CE2 derives every material object's runtime type from the file-backed root of its base chain.
    // The exact roots and type numbers are the switch in libfo76utils/material.cpp.
    private const uint LayeredRootDirectoryCrc = 0x1D95562F; // materials\layered\root
    private const uint MaterialExtension = 0x0074616D; // mat\0
    private const uint LayeredMaterialsRootFileCrc = 0x7EA3660C; // layeredmaterials (type 1)
    private const uint BlendersRootFileCrc = 0x8EBE84FF; // blenders (type 2)
    private const uint LayersRootFileCrc = 0x574A4CF3; // layers (type 3)
    private const uint MaterialsRootFileCrc = 0x7D1E021B; // materials (type 4)
    private const uint TextureSetsRootFileCrc = 0x06F52154; // texturesets (type 5)
    private const uint UvStreamsRootFileCrc = 0x4298BB09; // uvstreams

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    /// <summary>dbID → the object it inherits from (shader-model templates like LayeredMaterials.mat).</summary>
    private readonly Dictionary<uint, uint> _baseByObject = [];

    /// <summary>Every non-zero dbID declared by ObjectInfo, including synthetic material sub-objects.</summary>
    private readonly HashSet<uint> _objectIds = [];

    /// <summary>dbID → file-backed resource ID, when ObjectInfo gives the object one.</summary>
    private readonly Dictionary<uint, (uint Dir, uint File, uint Ext)> _resourceIdByObject = [];

    /// <summary>
    ///     dbID → wide-ObjectInfo parent resource. Retail writes this alongside the numeric base ID;
    ///     the reference reader consults it only when the numeric lookup fails and <c>hasData</c> is set.
    /// </summary>
    private readonly Dictionary<uint, (uint Dir, uint File, uint Ext)> _parentResourceIdByObject = [];

    /// <summary>
    ///     Objects reached through a <c>BSMaterial::BlenderID</c>. A blender's own texture is the MASK
    ///     that mixes two layers, and it sits at index 0 exactly like a texture set's albedo — so
    ///     without this set a generic descent happily returns a <c>_mask</c> as the albedo. That is a
    ///     single-channel image, which renders PURE RED when sampled as RGB.
    /// </summary>
    private readonly HashSet<uint> _blenderObjects = [];

    /// <summary>Material dbID → its blender objects by blender index.</summary>
    private readonly Dictionary<uint, Dictionary<int, uint>> _blendersByObject = [];

    /// <summary>
    ///     Blender dbID → the vertex-colour channel its layer mask consumes. This does NOT mean
    ///     the base material uses vertex colour as an RGB tint; that is a separate ParamBool on the
    ///     layer-material object.
    /// </summary>
    private readonly Dictionary<uint, StarfieldMaterialColorChannel> _colorChannelByBlender = [];

    /// <summary>parent dbID → its child dbIDs, from the edge table.</summary>
    private readonly Dictionary<uint, List<uint>> _childrenByObject = [];

    /// <summary>
    ///     Material dbID → its layer objects by layer index. A CE2 material is a STACK of layers, and
    ///     layer 0 is the base surface whose albedo the shape actually shows.
    /// </summary>
    private readonly Dictionary<uint, Dictionary<int, uint>> _layersByObject = [];

    /// <summary>Layer/blender dbID → the UV-stream object selected by <c>UVStreamID</c>.</summary>
    private readonly Dictionary<uint, uint> _uvStreamByObject = [];

    // UVStream fields inherit independently. DIFF may override only X or Y inside the nested
    // XMFLOAT2, so storing a Vector2 per object would incorrectly erase the untouched base member.
    private readonly Dictionary<uint, float> _uvScaleXByObject = [];
    private readonly Dictionary<uint, float> _uvScaleYByObject = [];
    private readonly Dictionary<uint, float> _uvOffsetXByObject = [];
    private readonly Dictionary<uint, float> _uvOffsetYByObject = [];
    private readonly Dictionary<uint, StarfieldMaterialTextureAddressMode> _uvAddressModeByObject = [];
    private readonly Dictionary<uint, StarfieldMaterialUvChannel> _uvChannelByObject = [];
    private readonly HashSet<uint> _malformedUvObjects = [];

    /// <summary>Root material dbID → authored shader route; Deferred is the constructor default.</summary>
    private readonly Dictionary<uint, StarfieldMaterialShaderRoute> _shaderRouteByObject = [];
    private readonly HashSet<uint> _malformedShaderRouteObjects = [];

    /// <summary>Root material shader model; constructor default is BaseMaterial.</summary>
    private readonly Dictionary<uint, string> _shaderModelByObject = [];
    private readonly HashSet<uint> _malformedShaderModelObjects = [];

    /// <summary>
    ///     Root CE2Material dbID → locally authored Flag_TwoSided setters in component-list order.
    ///     Derived components replace an inherited component key without moving its base-list
    ///     position, so resolving only the nearest setter would not reproduce copyBaseObject.
    /// </summary>
    private readonly Dictionary<uint, List<TwoSidedSetter>> _twoSidedSettersByObject = [];
    private readonly HashSet<uint> _malformedTwoSidedObjects = [];

    /// <summary>Layer dbID → the material object it uses.</summary>
    private readonly Dictionary<uint, uint> _materialByLayer = [];

    /// <summary>Objects reached through a layer's <c>BSMaterial::MaterialID</c> (object type 4).</summary>
    private readonly HashSet<uint> _materialObjects = [];

    /// <summary>Layer-material dbID → authored material colour at its original XMFLOAT4 precision.</summary>
    private readonly Dictionary<uint, Vector4> _materialColorByObject = [];

    /// <summary>Layer-material dbID → Multiply/Lerp colour override mode.</summary>
    private readonly Dictionary<uint, StarfieldMaterialColorOverrideMode> _materialColorModeByObject = [];

    /// <summary>
    ///     Layer-material dbID → whether its texture sheet uses animated flipbook UVs. Static
    ///     opacity sampling must reject this path until the renderer carries the authored frame grid
    ///     and clock; otherwise the opacity and base-colour samples can address different frames.
    /// </summary>
    private readonly Dictionary<uint, bool> _materialIsFlipbookByObject = [];

    /// <summary>
    ///     Layer-material dbID → whether mesh vertex colour is a surface tint. The identical
    ///     ParamBool slot on a root CE2Material means two-sided instead, so callers must resolve this
    ///     through layer[0]'s MaterialID rather than reading it from the file-backed root.
    /// </summary>
    private readonly Dictionary<uint, bool> _materialUsesVertexColorByObject = [];

    // AlphaSettings lives on the root CE2Material (object type 1), not on the layer material.
    // Keep every field separate: a DIFF component may override just one nested member and inherits
    // every other member independently from the base object.
    private readonly HashSet<uint> _alphaSettingsObjects = [];
    private readonly Dictionary<uint, bool> _alphaHasOpacityByObject = [];
    private readonly Dictionary<uint, float> _alphaThresholdByObject = [];
    private readonly Dictionary<uint, int> _alphaSourceLayerByObject = [];
    private readonly Dictionary<uint, StarfieldMaterialAlphaBlenderMode> _alphaBlenderModeByObject = [];
    private readonly Dictionary<uint, bool> _alphaUsesDetailBlendMaskByObject = [];
    private readonly Dictionary<uint, bool> _alphaUsesVertexColorByObject = [];
    private readonly Dictionary<uint, StarfieldMaterialColorChannel> _alphaVertexColorChannelByObject = [];
    private readonly Dictionary<uint, uint> _alphaUvStreamByObject = [];
    private readonly Dictionary<uint, float> _alphaHeightBlendThresholdByObject = [];
    private readonly Dictionary<uint, float> _alphaHeightBlendFactorByObject = [];
    private readonly Dictionary<uint, float> _alphaPositionByObject = [];
    private readonly Dictionary<uint, float> _alphaContrastByObject = [];
    private readonly Dictionary<uint, bool> _alphaUsesDitheredTransparencyByObject = [];
    private readonly HashSet<uint> _malformedAlphaSettingsObjects = [];

    /// <summary>Resource ID (dir, file, ext CRCs) → dbID, for resolving a <c>.mat</c> path.</summary>
    private readonly Dictionary<(uint Dir, uint File, uint Ext), uint> _objectByResourceId = [];

    /// <summary>
    ///     Texture-set dbID → per-slot flat colour standing in for an absent texture
    ///     (<c>BSMaterial::TextureReplacement</c>). Enabled replacements are how a material declares
    ///     "this slot is a solid colour, not an image" — plain plastic, painted metal, and so on.
    /// </summary>
    private readonly Dictionary<uint, Dictionary<int, TextureReplacementOverride>> _replacementsByObject = [];

    /// <summary>
    ///     dbID → texture declarations by slot. Empty paths are retained because they explicitly clear
    ///     an inherited image; MRTextureFile is also retained because only that class may replace an
    ///     already-populated inherited path in the reference implementation.
    /// </summary>
    private readonly Dictionary<uint, Dictionary<int, TexturePathOverride>> _texturesByObject = [];

    private readonly HashSet<uint> _malformedMaterialGraphObjects = [];
    private readonly HashSet<uint> _malformedTextureObjects = [];

    /// <summary>Material dbID → the texture-set object it uses.</summary>
    private readonly Dictionary<uint, uint> _textureSetByMaterial = [];

    /// <summary>Objects whose components were successfully decoded (diagnostics).</summary>
    public int TextureObjectCount => _texturesByObject.Count;

    /// <summary>Objects the file index declared (diagnostics).</summary>
    public int ObjectCount { get; private set; }

    /// <summary>ComponentInfo entries seen (diagnostics).</summary>
    public int ComponentTableCount { get; private set; }

    /// <summary>OBJT/DIFF chunks seen (diagnostics). Must equal ComponentTableCount.</summary>
    public int ComponentChunkCount { get; private set; }

    /// <summary>Per-object texture slots. Exposed for format diagnostics.</summary>
    internal IReadOnlyDictionary<uint, Dictionary<int, TexturePathOverride>> TexturesByObject => _texturesByObject;

    /// <summary>File-backed objects keyed by resource ID. Exposed for format diagnostics.</summary>
    internal IReadOnlyDictionary<(uint Dir, uint File, uint Ext), uint> ResourceIds => _objectByResourceId;

    /// <summary>Layer-material objects with an explicit vertex-colour policy (diagnostics).</summary>
    internal int MaterialVertexColorPolicyObjectCount =>
        _materialUsesVertexColorByObject.Keys.Count(_materialObjects.Contains);

    /// <summary>Blender objects with an explicit mask-channel selector (diagnostics).</summary>
    internal int BlenderColorChannelObjectCount =>
        _colorChannelByBlender.Keys.Count(_blenderObjects.Contains);

    /// <summary>Reflected CRC-32 of an ASCII string, lowercased. Exposed for format diagnostics.</summary>
    internal static uint DiagnosticCrc(string value)
    {
        return Crc32Lower(value);
    }

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
        return db.ReadChunks(data, ref pos, chunksRemaining, strings) ? db : null;
    }

    /// <summary>
    ///     Resolves a <c>materials\...\*.mat</c> path to its diffuse texture path, or null when the
    ///     material is absent or declares none.
    /// </summary>
    public string? ResolveDiffuse(string materialPath)
    {
        return ResolveSlot(materialPath, DiffuseSlot).TexturePath;
    }

    /// <summary>Resolves a material path to its normal-map texture path, or null.</summary>
    public string? ResolveNormal(string materialPath)
    {
        return ResolveSlot(materialPath, NormalSlot).TexturePath;
    }

    /// <summary>Resolves the albedo slot, which may be a texture path OR a flat replacement colour.</summary>
    internal StarfieldMaterialSlot ResolveDiffuseSlot(string materialPath)
    {
        return ResolveSlot(materialPath, DiffuseSlot);
    }

    /// <summary>
    ///     True when the database has an object for <paramref name="materialPath" /> at all —
    ///     distinguishes "material exists but resolves no albedo" (occluders, normal-only decals:
    ///     legitimately drawn without a diffuse, or not at all) from "material missing" (broken
    ///     content, worth surfacing loudly).
    /// </summary>
    internal bool Contains(string materialPath)
    {
        return !string.IsNullOrWhiteSpace(materialPath) &&
               _objectByResourceId.ContainsKey(ComputeResourceId(materialPath));
    }

    /// <summary>Resolves the normal slot, which may be a texture path OR a flat replacement colour.</summary>
    internal StarfieldMaterialSlot ResolveNormalSlot(string materialPath)
    {
        return ResolveSlot(materialPath, NormalSlot);
    }

    /// <summary>Resolves the root material's effective AlphaSettings and its selected opacity slot.</summary>
    internal StarfieldMaterialAlphaPolicy ResolveAlphaPolicy(string materialPath)
    {
        return TryResolveRoot(materialPath, out var root) &&
               IsObjectTypeRootedAt(root, LayeredMaterialsRootFileCrc)
            ? ResolveAlphaPolicy(root)
            : default;
    }

    /// <summary>
    ///     Resolves the effective CE2 root-material two-sided flag. Null means the path, runtime
    ///     object type, inheritance chain, or one of the flag-setting components was not trustworthy;
    ///     false is the resolved constructor/default state. This is distinct from the identically
    ///     named ParamBool on a type-4 layer material, where it enables vertex tint.
    /// </summary>
    internal bool? ResolveRootTwoSided(string materialPath)
    {
        if (!TryResolveRoot(materialPath, out var root) ||
            !IsObjectTypeRootedAt(root, LayeredMaterialsRootFileCrc))
        {
            return null;
        }

        // Build the already-validated chain without allocating. copyBaseObject flattens it from
        // base→derived, replacing matching component keys in place and appending new keys.
        Span<uint> chain = stackalloc uint[64];
        var chainCount = 0;
        for (var current = root; current != 0; current = GetEffectiveBaseObject(current))
        {
            var repeated = false;
            for (var i = 0; i < chainCount; i++)
            {
                repeated |= chain[i] == current;
            }

            if (chainCount == chain.Length || repeated || _malformedTwoSidedObjects.Contains(current))
            {
                return null;
            }

            chain[chainCount++] = current;
        }

        var hasShaderModel = false;
        var hasParamBool = false;
        var shaderModelPosition = -1;
        var paramBoolPosition = -1;
        var shaderModelValue = false;
        var paramBoolValue = false;
        var nextPosition = 0;
        for (var chainIndex = chainCount - 1; chainIndex >= 0; chainIndex--)
        {
            if (!_twoSidedSettersByObject.TryGetValue(chain[chainIndex], out var setters))
            {
                continue;
            }

            foreach (var setter in setters)
            {
                if (setter.Kind == TwoSidedSetterKind.ShaderModel)
                {
                    if (!hasShaderModel)
                    {
                        hasShaderModel = true;
                        shaderModelPosition = nextPosition++;
                    }

                    shaderModelValue = setter.Value;
                }
                else
                {
                    if (!hasParamBool)
                    {
                        hasParamBool = true;
                        paramBoolPosition = nextPosition++;
                    }

                    paramBoolValue = setter.Value;
                }
            }
        }

        // Both component readers assign the same flag, so replaying the flattened list is equivalent
        // to taking the value at the later preserved key position. No setter means constructor false.
        return (hasShaderModel, hasParamBool) switch
        {
            (true, true) => shaderModelPosition > paramBoolPosition ? shaderModelValue : paramBoolValue,
            (true, false) => shaderModelValue,
            (false, true) => paramBoolValue,
            _ => false
        };
    }

    /// <summary>
    ///     Resolves the bounded static-layer policy used by GLB export for CE2 roughness,
    ///     metalness, and ambient occlusion. The policy retains unsupported graph/UV facts for
    ///     diagnostics; <see cref="StarfieldMaterialOrmPolicy.TryResolveStaticLayer0Orm" /> is the
    ///     only method that authorizes packing.
    /// </summary>
    internal StarfieldMaterialOrmPolicy ResolveOrmPolicy(string materialPath)
    {
        if (!TryResolveRoot(materialPath, out var root) ||
            !IsObjectTypeRootedAt(root, LayeredMaterialsRootFileCrc))
        {
            return default;
        }

        var layers = EffectiveSlots(_layersByObject, root);
        if (!layers.TryGetValue(0, out var layer) || layer == 0)
        {
            return default;
        }

        var material = Inherited(_materialByLayer, layer);
        var textureSet = material != 0 ? Inherited(_textureSetByMaterial, material) : 0;
        if (material == 0 || textureSet == 0 ||
            !_objectIds.Contains(layer) || !_objectIds.Contains(material) ||
            !_objectIds.Contains(textureSet))
        {
            return default;
        }

        var uvStream = InheritedOrDefault(_uvStreamByObject, layer, 0u);
        var malformed = !IsObjectTypeRootedAt(layer, LayersRootFileCrc) ||
                        !IsObjectTypeRootedAt(material, MaterialsRootFileCrc) ||
                        !IsObjectTypeRootedAt(textureSet, TextureSetsRootFileCrc) ||
                        InheritedContains(_malformedMaterialGraphObjects, root) ||
                        InheritedContains(_malformedMaterialGraphObjects, layer) ||
                        InheritedContains(_malformedMaterialGraphObjects, material) ||
                        InheritedContains(_malformedShaderRouteObjects, root) ||
                        InheritedContains(_malformedShaderModelObjects, root) ||
                        InheritedContains(_malformedUvObjects, layer) ||
                        InheritedContains(_malformedTextureObjects, textureSet) ||
                        uvStream != 0 &&
                        (!IsObjectTypeRootedAt(uvStream, UvStreamsRootFileCrc) ||
                         InheritedContains(_malformedUvObjects, uvStream));
        var scale = uvStream == 0
            ? Vector2.One
            : new Vector2(
                InheritedOrDefault(_uvScaleXByObject, uvStream, 1f),
                InheritedOrDefault(_uvScaleYByObject, uvStream, 1f));
        var offset = uvStream == 0
            ? Vector2.Zero
            : new Vector2(
                InheritedOrDefault(_uvOffsetXByObject, uvStream, 0f),
                InheritedOrDefault(_uvOffsetYByObject, uvStream, 0f));
        var addressMode = uvStream == 0
            ? StarfieldMaterialTextureAddressMode.Wrap
            : InheritedOrDefault(
                _uvAddressModeByObject,
                uvStream,
                StarfieldMaterialTextureAddressMode.Wrap);
        var uvChannel = uvStream == 0
            ? StarfieldMaterialUvChannel.One
            : InheritedOrDefault(
                _uvChannelByObject,
                uvStream,
                StarfieldMaterialUvChannel.One);

        return new StarfieldMaterialOrmPolicy(
            true,
            layers.Count == 1,
            EffectiveSlots(_blendersByObject, root).Count != 0,
            InheritedOrDefault(_materialIsFlipbookByObject, material, false),
            malformed,
            InheritedOrDefault(
                _shaderRouteByObject,
                root,
                StarfieldMaterialShaderRoute.Deferred),
            string.Equals(
                InheritedOrDefault(_shaderModelByObject, root, "BaseMaterial"),
                "Hair1Layer",
                StringComparison.Ordinal),
            scale,
            offset,
            addressMode,
            uvChannel,
            SlotFromTextureSet(textureSet, RoughnessSlot),
            SlotFromTextureSet(textureSet, MetalnessSlot),
            SlotFromTextureSet(textureSet, AmbientOcclusionSlot));
    }

    /// <summary>
    ///     Counts retail AlphaSettings shapes without requiring unhashed material paths. Resource IDs
    ///     already identify every file-backed material root, so walking their distinct dbIDs exercises
    ///     the same inheritance and typed layer/texture links as a normal lookup.
    /// </summary>
    internal StarfieldMaterialAlphaCensus BuildAlphaCensus()
    {
        var resourceRoots = _objectByResourceId.Values
            .Distinct()
            .Where(root => IsObjectTypeRootedAt(root, LayeredMaterialsRootFileCrc))
            .ToArray();
        var withOpacity = 0;
        var supported = 0;
        var missingSlot = 0;
        var nonLayer0 = 0;
        var unsupportedUv = 0;
        var malformedSettings = 0;
        var vertexOrDetail = 0;
        var dithered = 0;
        var flipbook = 0;
        var nonLinear = 0;
        var nonCuttingThreshold = 0;

        foreach (var root in resourceRoots)
        {
            var policy = ResolveAlphaPolicy(root);
            if (!policy.IsResolved || !policy.HasOpacity)
            {
                continue;
            }

            withOpacity++;
            if (!policy.OpacitySlot.IsResolved) missingSlot++;
            if (policy.OpacitySourceLayer != 0) nonLayer0++;
            if (!policy.OpacityUvUsesIdentityUv0) unsupportedUv++;
            if (policy.HasMalformedSettings) malformedSettings++;
            if (policy.UsesVertexColor || policy.UsesDetailBlendMask) vertexOrDetail++;
            if (policy.UsesDitheredTransparency) dithered++;
            if (policy.OpacityLayerUsesFlipbook) flipbook++;
            if (policy.BlenderMode != StarfieldMaterialAlphaBlenderMode.Linear) nonLinear++;
            if (!float.IsFinite(policy.AlphaTestThreshold) ||
                policy.AlphaTestThreshold <= 0f || policy.AlphaTestThreshold >= 1f)
            {
                nonCuttingThreshold++;
            }

            if (policy.TryResolveStaticCutout(out _))
            {
                supported++;
            }
        }

        return new StarfieldMaterialAlphaCensus(
            _alphaSettingsObjects.Count,
            resourceRoots.Length,
            withOpacity,
            supported,
            missingSlot,
            nonLayer0,
            unsupportedUv,
            malformedSettings,
            vertexOrDetail,
            dithered,
            flipbook,
            nonLinear,
            nonCuttingThreshold);
    }

    /// <summary>
    ///     Resolves the effective colour policy of a material's lowest (base-surface) layer.
    ///     <para>
    ///         CE2 overloads <c>BSMaterial::ParamBool</c> slot 0 by owner type: on the file-backed
    ///         root it means two-sided, while on the layer's material object it means "use vertex
    ///         colour as tint". Following the typed <c>LayerID → MaterialID</c> links here prevents
    ///         the root flag from being mistaken for a tint enable.
    ///     </para>
    /// </summary>
    internal StarfieldMaterialColorPolicy ResolveBaseColorPolicy(string materialPath)
    {
        if (!TryResolveRoot(materialPath, out var root) || LowestLayer(root) is not { } layer)
        {
            return default;
        }

        var material = Inherited(_materialByLayer, layer);
        if (material == 0)
        {
            return default;
        }

        var usesVertexColor = InheritedOrDefault(_materialUsesVertexColorByObject, material, false);
        var overrideMode = InheritedOrDefault(
            _materialColorModeByObject,
            material,
            StarfieldMaterialColorOverrideMode.Lerp);
        var color = InheritedOrDefault(_materialColorByObject, material, new Vector4(1f, 1f, 1f, 0f));
        return new StarfieldMaterialColorPolicy(true, usesVertexColor, overrideMode, color);
    }

    private StarfieldMaterialAlphaPolicy ResolveAlphaPolicy(uint root)
    {
        // Absence is a resolved opaque material, not a parse failure. CE2's constructor defaults are
        // mirrored exactly from libfo76utils/material.cpp; per-field inheritance matches copyBaseObject.
        var hasOpacity = InheritedOrDefault(_alphaHasOpacityByObject, root, false);
        var threshold = InheritedOrDefault(_alphaThresholdByObject, root, 1f / 3f);
        var sourceLayer = InheritedOrDefault(_alphaSourceLayerByObject, root, 0);
        var mode = InheritedOrDefault(
            _alphaBlenderModeByObject, root, StarfieldMaterialAlphaBlenderMode.Linear);
        var usesDetail = InheritedOrDefault(_alphaUsesDetailBlendMaskByObject, root, false);
        var usesVertex = InheritedOrDefault(_alphaUsesVertexColorByObject, root, false);
        var vertexChannel = InheritedOrDefault(
            _alphaVertexColorChannelByObject, root, StarfieldMaterialColorChannel.Red);
        var uvStream = InheritedOrDefault(_alphaUvStreamByObject, root, 0u);
        var uvIsWellFormed = uvStream == 0 ||
                             IsObjectTypeRootedAt(uvStream, UvStreamsRootFileCrc) &&
                             !InheritedContains(_malformedUvObjects, uvStream);
        var uvScale = uvStream == 0
            ? Vector2.One
            : new Vector2(
                InheritedOrDefault(_uvScaleXByObject, uvStream, 1f),
                InheritedOrDefault(_uvScaleYByObject, uvStream, 1f));
        var uvOffset = uvStream == 0
            ? Vector2.Zero
            : new Vector2(
                InheritedOrDefault(_uvOffsetXByObject, uvStream, 0f),
                InheritedOrDefault(_uvOffsetYByObject, uvStream, 0f));
        var uvAddressMode = uvStream == 0
            ? StarfieldMaterialTextureAddressMode.Wrap
            : InheritedOrDefault(
                _uvAddressModeByObject,
                uvStream,
                StarfieldMaterialTextureAddressMode.Wrap);
        var uvChannel = uvStream == 0
            ? StarfieldMaterialUvChannel.One
            : InheritedOrDefault(
                _uvChannelByObject,
                uvStream,
                StarfieldMaterialUvChannel.One);
        var malformedSettings = InheritedContains(_malformedAlphaSettingsObjects, root) ||
                                InheritedContains(_malformedMaterialGraphObjects, root);
        var uvUsesIdentityUv0 = uvIsWellFormed &&
                                uvScale == Vector2.One &&
                                uvOffset == Vector2.Zero &&
                                uvAddressMode == StarfieldMaterialTextureAddressMode.Wrap &&
                                uvChannel == StarfieldMaterialUvChannel.One;
        var heightThreshold = InheritedOrDefault(_alphaHeightBlendThresholdByObject, root, 0f);
        var heightFactor = InheritedOrDefault(_alphaHeightBlendFactorByObject, root, 0.05f);
        var position = InheritedOrDefault(_alphaPositionByObject, root, 0.5f);
        var contrast = InheritedOrDefault(_alphaContrastByObject, root, 0f);
        var dithered = InheritedOrDefault(_alphaUsesDitheredTransparencyByObject, root, false);

        var opacitySlot = default(StarfieldMaterialSlot);
        var opacityLayerUsesFlipbook = false;
        if (InheritedSlot(_layersByObject, root, sourceLayer) is { } layer && layer != 0)
        {
            malformedSettings |= !IsObjectTypeRootedAt(layer, LayersRootFileCrc) ||
                                 InheritedContains(_malformedMaterialGraphObjects, layer);
            var material = Inherited(_materialByLayer, layer);
            malformedSettings |= material != 0 &&
                                 (!IsObjectTypeRootedAt(material, MaterialsRootFileCrc) ||
                                  InheritedContains(_malformedMaterialGraphObjects, material));
            opacityLayerUsesFlipbook = material != 0 &&
                                       InheritedOrDefault(_materialIsFlipbookByObject, material, false);
            var textureSet = material != 0 ? Inherited(_textureSetByMaterial, material) : 0;
            if (textureSet != 0)
            {
                malformedSettings |= !IsObjectTypeRootedAt(textureSet, TextureSetsRootFileCrc) ||
                                     InheritedContains(_malformedTextureObjects, textureSet);
                opacitySlot = SlotFromTextureSet(textureSet, OpacitySlot);
            }
        }

        return new StarfieldMaterialAlphaPolicy(
            true,
            hasOpacity,
            threshold,
            sourceLayer,
            mode,
            usesDetail,
            usesVertex,
            vertexChannel,
            uvStream,
            uvUsesIdentityUv0 && !malformedSettings,
            malformedSettings,
            heightThreshold,
            heightFactor,
            position,
            contrast,
            dithered,
            opacityLayerUsesFlipbook,
            opacitySlot);
    }

    /// <summary>
    ///     Resolves one layer blender's vertex-colour mask channel. Red is CE2's default when the
    ///     blender exists but carries no explicit <c>ColorChannelTypeComponent</c>; null means the
    ///     material has no blender at that index.
    /// </summary>
    internal StarfieldMaterialColorChannel? ResolveBlenderColorChannel(string materialPath, int blenderIndex = 0)
    {
        if (!TryResolveRoot(materialPath, out var root) ||
            InheritedSlot(_blendersByObject, root, blenderIndex) is not { } blender ||
            blender == 0)
        {
            return null;
        }

        return InheritedOrDefault(_colorChannelByBlender, blender, StarfieldMaterialColorChannel.Red);
    }

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
        if (!TryResolveRoot(materialPath, out var root))
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

    private bool TryResolveRoot(string materialPath, out uint root)
    {
        root = 0;
        return !string.IsNullOrWhiteSpace(materialPath) &&
               _objectByResourceId.TryGetValue(ComputeResourceId(materialPath), out root);
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
                    if (!seenIndices.Add(index) || dbId == 0)
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

            current = GetEffectiveBaseObject(current);
        }

        return pick != 0 ? pick : null;
    }

    /// <summary>Reads <paramref name="map" /> at <paramref name="obj" />, then up its base chain.</summary>
    private uint Inherited(Dictionary<uint, uint> map, uint obj)
    {
        var visited = new HashSet<uint>();
        for (var current = obj; current != 0 && visited.Add(current);)
        {
            if (map.TryGetValue(current, out var value))
            {
                return value;
            }

            current = GetEffectiveBaseObject(current);
        }

        return 0;
    }

    /// <summary>Reads a value on <paramref name="obj" />, then up its base chain, or a format default.</summary>
    private T InheritedOrDefault<T>(Dictionary<uint, T> map, uint obj, T defaultValue)
    {
        var visited = new HashSet<uint>();
        for (var current = obj; current != 0 && visited.Add(current);)
        {
            if (map.TryGetValue(current, out var value))
            {
                return value;
            }

            current = GetEffectiveBaseObject(current);
        }

        return defaultValue;
    }

    /// <summary>Reads one indexed object link on <paramref name="obj" />, then up its base chain.</summary>
    private uint? InheritedSlot(Dictionary<uint, Dictionary<int, uint>> map, uint obj, int slot)
    {
        var visited = new HashSet<uint>();
        for (var current = obj; current != 0 && visited.Add(current);)
        {
            if (map.TryGetValue(current, out var slots) &&
                slots.TryGetValue(slot, out var value))
            {
                return value;
            }

            current = GetEffectiveBaseObject(current);
        }

        return null;
    }

    /// <summary>
    ///     Builds the effective indexed link map, with the nearest declaration winning per index.
    ///     Static ORM export uses the complete map so a derived material that adds layer 1 cannot be
    ///     mistaken for a single-layer material merely because layer 0 lives on its base object.
    /// </summary>
    private Dictionary<int, uint> EffectiveSlots(
        Dictionary<uint, Dictionary<int, uint>> map,
        uint obj)
    {
        var result = new Dictionary<int, uint>();
        var visited = new HashSet<uint>();
        var seenIndices = new HashSet<int>();
        for (var current = obj; current != 0 && visited.Add(current);)
        {
            if (map.TryGetValue(current, out var slots))
            {
                foreach (var (index, value) in slots)
                {
                    if (seenIndices.Add(index) && value != 0)
                    {
                        result[index] = value;
                    }
                }
            }

            current = GetEffectiveBaseObject(current);
        }

        return result;
    }

    /// <summary>True when an object or any inherited base object is marked malformed.</summary>
    private bool InheritedContains(HashSet<uint> set, uint obj)
    {
        var visited = new HashSet<uint>();
        for (var current = obj; current != 0 && visited.Add(current);)
        {
            if (set.Contains(current))
            {
                return true;
            }

            current = GetEffectiveBaseObject(current);
        }

        return false;
    }

    /// <summary>
    ///     CE2 assigns object types from the terminal file-backed object in the base chain. Match
    ///     the same rule instead of accepting any existing dbID that happens to carry no conflicting
    ///     components and therefore looks like a constructor-default UV stream.
    /// </summary>
    private bool IsObjectTypeRootedAt(uint obj, uint expectedRootFileCrc)
    {
        var current = obj;
        for (var depth = 0; current != 0 && depth < 64; depth++)
        {
            if (!_objectIds.Contains(current))
            {
                return false;
            }

            var baseObject = GetEffectiveBaseObject(current);
            if (baseObject != 0)
            {
                current = baseObject;
                continue;
            }

            return _resourceIdByObject.TryGetValue(current, out var resource) &&
                   IsCanonicalTypeResource(resource, expectedRootFileCrc);
        }

        // Missing root, an implausibly deep chain, or a cycle all fail closed.
        return false;
    }

    /// <summary>
    ///     ObjectInfo's effective inheritance edge. A valid database-local numeric ID wins; only a
    ///     missing/dangling numeric ID may fall back to the HasData-qualified persistent resource.
    ///     Keeping this in one helper makes value inheritance and runtime-type admission agree.
    /// </summary>
    private uint GetEffectiveBaseObject(uint obj)
    {
        if (_baseByObject.TryGetValue(obj, out var numericBase) &&
            numericBase != 0 && _objectIds.Contains(numericBase))
        {
            return numericBase;
        }

        return _parentResourceIdByObject.TryGetValue(obj, out var parentResource) &&
               _objectByResourceId.TryGetValue(parentResource, out var persistentBase) &&
               persistentBase != obj
            ? persistentBase
            : 0;
    }

    private static bool IsCanonicalTypeResource(
        (uint Dir, uint File, uint Ext) resource,
        uint expectedRootFileCrc)
    {
        return resource.Dir == LayeredRootDirectoryCrc &&
               resource.File == expectedRootFileCrc &&
               resource.Ext == MaterialExtension;
    }

    private void SetTwoSidedSetter(uint owner, TwoSidedSetterKind kind, bool value)
    {
        if (!_twoSidedSettersByObject.TryGetValue(owner, out var setters))
        {
            _twoSidedSettersByObject[owner] = setters = [];
        }

        // A component key overridden more than once keeps its original list position, matching the
        // reference reader's findComponent replacement behavior.
        for (var i = 0; i < setters.Count; i++)
        {
            if (setters[i].Kind != kind)
            {
                continue;
            }

            setters[i] = new TwoSidedSetter(kind, value);
            return;
        }

        setters.Add(new TwoSidedSetter(kind, value));
    }

    /// <summary>
    ///     Exact shader-model set selected by the reference reader's
    ///     <c>0xF060000000000000</c> Flag_TwoSided mask (indices 53, 54, and 60..63).
    /// </summary>
    private static bool ShaderModelForcesTwoSided(string shaderModel)
    {
        return shaderModel is
            "TranslucentTwoSided1Layer" or
            "TwoSided1Layer" or
            "VegetationTranslucent1Layer" or
            "VegetationTranslucent2Layer" or
            "Water" or
            "Water1Layer";
    }

    private enum TwoSidedSetterKind : byte
    {
        ShaderModel,
        ParamBool
    }

    private readonly record struct TwoSidedSetter(TwoSidedSetterKind Kind, bool Value);

    /// <summary>
    ///     A texture set's effective image and replacement inherit independently. The renderer uses
    ///     the replacement only when the effective image is empty/unavailable, matching getSFTexture.
    /// </summary>
    private StarfieldMaterialSlot SlotFromTextureSet(uint textureSet, int slot)
    {
        var path = ResolveTexturePath(textureSet, slot);
        if (path is { Length: > 0 })
        {
            return new StarfieldMaterialSlot(path, null);
        }

        var replacement = ResolveTextureReplacement(textureSet, slot);
        return replacement.Enabled
            ? new StarfieldMaterialSlot(null, replacement.Rgba)
            : default;
    }

    /// <summary>
    ///     Replays texture-path inheritance oldest-to-newest. A normal TextureFile may initialize an
    ///     empty slot, but only MRTextureFile may replace a path that a base object already populated.
    ///     Empty MR paths are retained and clear the inherited image.
    /// </summary>
    private string? ResolveTexturePath(uint textureSet, int slot)
    {
        var chain = new List<uint>();
        var visited = new HashSet<uint>();
        for (var current = textureSet; current != 0 && visited.Add(current);)
        {
            chain.Add(current);
            current = GetEffectiveBaseObject(current);
        }

        string? path = null;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (!_texturesByObject.TryGetValue(chain[i], out var textures) ||
                !textures.TryGetValue(slot, out var declaration))
            {
                continue;
            }

            if (declaration.IsMultiResolution || string.IsNullOrEmpty(path))
            {
                path = declaration.Path;
            }
        }

        return path;
    }

    private (bool Enabled, uint Rgba) ResolveTextureReplacement(uint textureSet, int slot)
    {
        bool? enabled = null;
        float? r = null;
        float? g = null;
        float? b = null;
        float? a = null;
        var visited = new HashSet<uint>();
        for (var current = textureSet; current != 0 && visited.Add(current);)
        {
            if (_replacementsByObject.TryGetValue(current, out var replacements) &&
                replacements.TryGetValue(slot, out var replacement))
            {
                enabled ??= replacement.Enabled;
                r ??= replacement.R;
                g ??= replacement.G;
                b ??= replacement.B;
                a ??= replacement.A;
            }

            current = GetEffectiveBaseObject(current);
        }

        var defaultRgba = DefaultTextureReplacementRgba(slot);
        return (
            enabled ?? false,
            PackColor(
                r ?? ((defaultRgba >> 0) & 0xFF) / 255f,
                g ?? ((defaultRgba >> 8) & 0xFF) / 255f,
                b ?? ((defaultRgba >> 16) & 0xFF) / 255f,
                a ?? ((defaultRgba >> 24) & 0xFF) / 255f));
    }

    private static uint DefaultTextureReplacementRgba(int slot)
    {
        ReadOnlySpan<uint> defaults =
        [
            0xFF000000u, 0xFFFF8080u, 0xFFFFFFFFu, 0xFF000000u,
            0xFF000000u, 0xFFFFFFFFu, 0xFF000000u, 0xFF000000u,
            0xFF000000u, 0xFF808080u, 0xFF000000u, 0xFF808080u,
            0xFF000000u, 0xFF000000u, 0xFF808080u, 0xFF808080u,
            0xFF808080u, 0xFF000000u, 0xFF000000u, 0xFFFFFFFFu,
            0xFF808080u
        ];
        return (uint)slot < (uint)defaults.Length ? defaults[slot] : 0xFF000000u;
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
                if (SlotFromTextureSet(current, slot) is { IsResolved: true } resolved)
                {
                    return resolved;
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

    private bool ReadChunks(
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


        while (chunksRemaining > 0)
        {
            if (!TryReadChunk(data, ref pos, out var type, out var body))
            {
                return false;
            }

            chunksRemaining--;
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
                // Object chunks that appear BEFORE the component table has been read describe the
                // file index itself, not components, and must not consume a table slot. Counting them
                // shifts every later pairing by the same amount — which does not fail loudly, it just
                // returns a neighbouring material's texture (measured: a 2-chunk lead-in shifted the
                // whole database and produced same-folder-wrong-file results).
                if (componentOwners.Count == 0)
                {
                    continue;
                }

                // Positional pairing: component chunk N belongs to component N, whatever its class.
                // The two file-index lead-in objects above are OBJT chunks too, but are not entries
                // in ComponentInfo and therefore are deliberately absent from this diagnostic count.
                ComponentChunkCount++;

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
        // A missing payload or an extra payload shifts the positional component pairing. Returning a
        // partial database would make the absent fields indistinguishable from CE2 defaults.
        return componentIndex == componentOwners.Count &&
               ComponentChunkCount == ComponentTableCount;
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
                ReadTextureFile(body, owner, slot, isDiff, isMultiResolution: true);
                break;
            case "BSMaterial::TextureFile":
                ReadTextureFile(body, owner, slot, isDiff, isMultiResolution: false);
                break;

            // Each of these wraps a single BSComponentDB2::ID (a 4-byte dbID reference).
            case "BSMaterial::LayerID":
                if (TryReadIdComponent(body, isDiff, out var layerId))
                {
                    if (!_layersByObject.TryGetValue(owner, out var layers))
                    {
                        _layersByObject[owner] = layers = [];
                    }

                    layers[slot] = layerId;
                }
                else
                {
                    _malformedMaterialGraphObjects.Add(owner);
                }

                break;

            case "BSMaterial::BlenderID":
                if (TryReadIdComponent(body, isDiff, out var blenderId))
                {
                    if (blenderId != 0)
                    {
                        _blenderObjects.Add(blenderId);
                    }

                    if (!_blendersByObject.TryGetValue(owner, out var blenders))
                    {
                        _blendersByObject[owner] = blenders = [];
                    }

                    blenders[slot] = blenderId;
                }
                else
                {
                    _malformedMaterialGraphObjects.Add(owner);
                }

                break;

            case "BSMaterial::MaterialID":
                if (TryReadIdComponent(body, isDiff, out var materialId))
                {
                    _materialByLayer[owner] = materialId;
                    if (materialId != 0)
                    {
                        _materialObjects.Add(materialId);
                    }
                }
                else
                {
                    _malformedMaterialGraphObjects.Add(owner);
                }

                break;

            case "BSMaterial::TextureSetID":
                if (TryReadIdComponent(body, isDiff, out var textureSetId))
                {
                    _textureSetByMaterial[owner] = textureSetId;
                }
                else
                {
                    _malformedMaterialGraphObjects.Add(owner);
                }

                break;

            case "BSMaterial::UVStreamID":
                if (TryReadIdComponent(body, isDiff, out var uvStreamId))
                {
                    // Zero is an explicit reset to the constructor-default UV stream and must beat
                    // an inherited non-zero link, hence InheritedOrDefault rather than Inherited.
                    _uvStreamByObject[owner] = uvStreamId;
                }
                else
                {
                    _malformedUvObjects.Add(owner);
                }

                break;

            case "BSMaterial::Scale":
                ReadUvFloat2(
                    body,
                    owner,
                    isDiff,
                    _uvScaleXByObject,
                    _uvScaleYByObject);
                break;

            case "BSMaterial::Offset":
                ReadUvFloat2(
                    body,
                    owner,
                    isDiff,
                    _uvOffsetXByObject,
                    _uvOffsetYByObject);
                break;

            case "BSMaterial::TextureAddressModeComponent":
                if (slot == 0 &&
                    TryReadStringValue(body, isDiff, out var addressMode) &&
                    TryParseDefinedEnum(addressMode, out StarfieldMaterialTextureAddressMode parsedAddressMode))
                {
                    _uvAddressModeByObject[owner] = parsedAddressMode;
                }
                else if (slot == 0)
                {
                    _malformedUvObjects.Add(owner);
                }

                break;

            case "BSMaterial::Channel":
                if (slot == 0 &&
                    TryReadStringValue(body, isDiff, out var uvChannel) &&
                    TryParseDefinedEnum(uvChannel, out StarfieldMaterialUvChannel parsedUvChannel))
                {
                    _uvChannelByObject[owner] = parsedUvChannel;
                }
                else if (slot == 0)
                {
                    _malformedUvObjects.Add(owner);
                }

                break;

            case "BSMaterial::ShaderRouteComponent":
                if (slot == 0 &&
                    TryReadStringValue(body, isDiff, out var shaderRoute) &&
                    TryParseDefinedEnum(shaderRoute, out StarfieldMaterialShaderRoute parsedShaderRoute))
                {
                    _shaderRouteByObject[owner] = parsedShaderRoute;
                }
                else if (slot == 0)
                {
                    _malformedShaderRouteObjects.Add(owner);
                }

                break;

            case "BSMaterial::ShaderModelComponent":
                if (slot == 0 && TryReadStringValue(body, isDiff, out var shaderModel))
                {
                    _shaderModelByObject[owner] = shaderModel;
                    if (IsObjectTypeRootedAt(owner, LayeredMaterialsRootFileCrc))
                    {
                        // libfo76utils readShaderModelComponent writes the same flag as ParamBool.
                        // Retain this object's component-list order; effective base-to-derived
                        // replacement is replayed by ResolveRootTwoSided.
                        SetTwoSidedSetter(
                            owner,
                            TwoSidedSetterKind.ShaderModel,
                            ShaderModelForcesTwoSided(shaderModel));
                    }
                }
                else if (slot == 0)
                {
                    _malformedShaderModelObjects.Add(owner);
                    if (IsObjectTypeRootedAt(owner, LayeredMaterialsRootFileCrc))
                    {
                        _malformedTwoSidedObjects.Add(owner);
                    }
                }

                break;

            case "BSMaterial::TextureReplacement":
                ReadTextureReplacement(body, owner, slot, isDiff);
                break;

            // A standalone Color component belongs to the layer-material object (object type 4 in
            // the reference implementation). TextureReplacement embeds the same class but reaches
            // its colour through ReadTextureReplacement above, so the two cannot alias here.
            case "BSMaterial::Color":
                if (slot == 0 && TryReadColorComponent(body, isDiff, out var color))
                {
                    _materialColorByObject[owner] = color;
                }

                break;

            case "BSMaterial::MaterialOverrideColorTypeComponent":
                if (slot == 0 && TryReadStringValue(body, isDiff, out var mode))
                {
                    if (mode.Equals("Multiply", StringComparison.OrdinalIgnoreCase))
                    {
                        _materialColorModeByObject[owner] = StarfieldMaterialColorOverrideMode.Multiply;
                    }
                    else if (mode.Equals("Lerp", StringComparison.OrdinalIgnoreCase))
                    {
                        _materialColorModeByObject[owner] = StarfieldMaterialColorOverrideMode.Lerp;
                    }
                }

                break;

            // ParamBool slot 0 is type-dependent. ResolveBaseColorPolicy reaches only a LayerID's
            // MaterialID target (type 4), while a root CE2Material (type 1) writes Flag_TwoSided.
            // Keep both interpretations owner-typed so neither can leak into the other.
            case "BSMaterial::ParamBool":
                if (slot == 0 && TryReadBoolValue(body, isDiff, out var paramValue))
                {
                    _materialUsesVertexColorByObject[owner] = paramValue;
                    if (IsObjectTypeRootedAt(owner, LayeredMaterialsRootFileCrc))
                    {
                        SetTwoSidedSetter(owner, TwoSidedSetterKind.ParamBool, paramValue);
                    }
                }
                else if (slot == 0 && IsObjectTypeRootedAt(owner, LayeredMaterialsRootFileCrc))
                {
                    _malformedTwoSidedObjects.Add(owner);
                }

                break;

            // FlipbookComponent is a five-field component on the layer-material object (type 4).
            // Field 0 is IsAFlipbook. DIFF fields are indexed but not guaranteed to be ordered, so
            // its dedicated reader walks every known field rather than assuming field 0 comes first.
            case "BSMaterial::FlipbookComponent":
                if (TryReadFlipbookEnabled(body, isDiff, out var isFlipbook))
                {
                    _materialIsFlipbookByObject[owner] = isFlipbook;
                }

                break;

            case "BSMaterial::ColorChannelTypeComponent":
                if (slot == 0 && TryReadStringValue(body, isDiff, out var channel) &&
                    TryParseColorChannel(channel, out var parsedChannel))
                {
                    _colorChannelByBlender[owner] = parsedChannel;
                }

                break;

            case "BSMaterial::AlphaSettingsComponent":
                if (slot == 0)
                {
                    ReadAlphaSettingsComponent(body, owner, isDiff);
                }

                break;
        }
    }

    /// <summary>
    ///     Reads CE2's root-material AlphaSettings component. OBJT stores every field sequentially;
    ///     DIFF prefixes only the changed fields with u16 indices, including the nested
    ///     AlphaBlenderSettings/UVStreamID structures. Values land in per-field maps so a partial
    ///     derived component inherits all untouched fields from its base object.
    /// </summary>
    private void ReadAlphaSettingsComponent(
        ReadOnlySpan<byte> body,
        uint owner,
        bool isDiff)
    {
        _alphaSettingsObjects.Add(owner);
        var pos = 4;
        if (!isDiff)
        {
            if (!TryReadByte(body, ref pos, out var hasOpacity) ||
                !TryReadSingle(body, ref pos, out var threshold) ||
                !TryReadLengthPrefixedString(body, ref pos, out var sourceLayer) ||
                !TryReadLengthPrefixedString(body, ref pos, out var mode) ||
                !TryReadByte(body, ref pos, out var detailMask) ||
                !TryReadByte(body, ref pos, out var vertexColor) ||
                !TryReadLengthPrefixedString(body, ref pos, out var vertexChannel) ||
                !TryReadUInt32(body, ref pos, out var uvStream) ||
                !TryReadSingle(body, ref pos, out var heightThreshold) ||
                !TryReadSingle(body, ref pos, out var heightFactor) ||
                !TryReadSingle(body, ref pos, out var position) ||
                !TryReadSingle(body, ref pos, out var contrast) ||
                !TryReadByte(body, ref pos, out var dithered))
            {
                _malformedAlphaSettingsObjects.Add(owner);
                return;
            }

            if (pos != body.Length)
            {
                _malformedAlphaSettingsObjects.Add(owner);
                return;
            }

            if (!TryParseMaterialLayer(sourceLayer, out var layer) ||
                !TryParseAlphaBlenderMode(mode, out var parsedMode) ||
                !TryParseColorChannel(vertexChannel, out var parsedChannel))
            {
                _malformedAlphaSettingsObjects.Add(owner);
                return;
            }

            _alphaHasOpacityByObject[owner] = hasOpacity != 0;
            _alphaThresholdByObject[owner] = threshold;
            _alphaSourceLayerByObject[owner] = layer;
            _alphaBlenderModeByObject[owner] = parsedMode;
            _alphaUsesDetailBlendMaskByObject[owner] = detailMask != 0;
            _alphaUsesVertexColorByObject[owner] = vertexColor != 0;
            _alphaVertexColorChannelByObject[owner] = parsedChannel;
            _alphaUvStreamByObject[owner] = uvStream;
            _alphaHeightBlendThresholdByObject[owner] = heightThreshold;
            _alphaHeightBlendFactorByObject[owner] = heightFactor;
            _alphaPositionByObject[owner] = position;
            _alphaContrastByObject[owner] = contrast;
            _alphaUsesDitheredTransparencyByObject[owner] = dithered != 0;
            return;
        }

        while (TryReadFieldOrTerminator(body, ref pos, out var field, out var terminated))
        {
            if (terminated)
            {
                if (pos == body.Length)
                {
                    return;
                }

                _malformedAlphaSettingsObjects.Add(owner);
                return;
            }

            switch (field)
            {
                case 0 when TryReadByte(body, ref pos, out var hasOpacity):
                    _alphaHasOpacityByObject[owner] = hasOpacity != 0;
                    break;
                case 1 when TryReadSingle(body, ref pos, out var threshold):
                    _alphaThresholdByObject[owner] = threshold;
                    break;
                case 2 when TryReadLengthPrefixedString(body, ref pos, out var sourceLayer):
                    if (!TryParseMaterialLayer(sourceLayer, out var layer))
                    {
                        _malformedAlphaSettingsObjects.Add(owner);
                        return;
                    }

                    _alphaSourceLayerByObject[owner] = layer;
                    break;
                case 3:
                    if (!ReadAlphaBlenderSettingsDiff(body, ref pos, owner))
                    {
                        _malformedAlphaSettingsObjects.Add(owner);
                        return;
                    }

                    break;
                case 4 when TryReadByte(body, ref pos, out var dithered):
                    _alphaUsesDitheredTransparencyByObject[owner] = dithered != 0;
                    break;
                default:
                    _malformedAlphaSettingsObjects.Add(owner);
                    return;
            }
        }

        _malformedAlphaSettingsObjects.Add(owner);
    }

    private bool ReadAlphaBlenderSettingsDiff(
        ReadOnlySpan<byte> body,
        ref int pos,
        uint owner)
    {
        while (TryReadFieldOrTerminator(body, ref pos, out var field, out var terminated))
        {
            if (terminated)
            {
                return true;
            }

            switch (field)
            {
                case 0 when TryReadLengthPrefixedString(body, ref pos, out var mode):
                    if (!TryParseAlphaBlenderMode(mode, out var parsedMode))
                    {
                        return false;
                    }

                    _alphaBlenderModeByObject[owner] = parsedMode;
                    break;
                case 1 when TryReadByte(body, ref pos, out var detailMask):
                    _alphaUsesDetailBlendMaskByObject[owner] = detailMask != 0;
                    break;
                case 2 when TryReadByte(body, ref pos, out var vertexColor):
                    _alphaUsesVertexColorByObject[owner] = vertexColor != 0;
                    break;
                case 3 when TryReadLengthPrefixedString(body, ref pos, out var vertexChannel):
                    if (!TryParseColorChannel(vertexChannel, out var parsedChannel))
                    {
                        return false;
                    }

                    _alphaVertexColorChannelByObject[owner] = parsedChannel;
                    break;
                case 4:
                    // AlphaBlenderSettings field 4 is UVStreamID, whose field 0 is itself a
                    // BSComponentDB2::ID. DIFF therefore contributes TWO nested field-0 indices
                    // and TWO nested terminators around the dbID before the blender fields resume.
                    if (!TryReadFieldOrTerminator(
                            body, ref pos, out var uvStreamField, out var uvStreamTerminated) ||
                        uvStreamTerminated || uvStreamField != 0 ||
                        !TryReadFieldOrTerminator(
                            body, ref pos, out var idField, out var idTerminated) ||
                        idTerminated || idField != 0 ||
                        !TryReadUInt32(body, ref pos, out var uvStream) ||
                        !TryConsumeTerminator(body, ref pos) ||
                        !TryConsumeTerminator(body, ref pos))
                    {
                        return false;
                    }

                    _alphaUvStreamByObject[owner] = uvStream;
                    break;
                case 5 when TryReadSingle(body, ref pos, out var heightThreshold):
                    _alphaHeightBlendThresholdByObject[owner] = heightThreshold;
                    break;
                case 6 when TryReadSingle(body, ref pos, out var heightFactor):
                    _alphaHeightBlendFactorByObject[owner] = heightFactor;
                    break;
                case 7 when TryReadSingle(body, ref pos, out var position):
                    _alphaPositionByObject[owner] = position;
                    break;
                case 8 when TryReadSingle(body, ref pos, out var contrast):
                    _alphaContrastByObject[owner] = contrast;
                    break;
                default:
                    return false;
            }
        }

        return false;
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

    /// <summary>Reads a one-field Bool component in packed OBJT or indexed DIFF form.</summary>
    private static bool TryReadBoolValue(ReadOnlySpan<byte> body, bool isDiff, out bool value)
    {
        value = false;
        var pos = 4;
        if (isDiff)
        {
            if (pos + 2 > body.Length || BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]) != 0)
            {
                return false;
            }

            pos += 2;
        }

        if (pos >= body.Length)
        {
            return false;
        }

        value = body[pos] != 0;
        return true;
    }

    /// <summary>
    ///     Reads FlipbookComponent field 0 (<c>IsAFlipbook</c>). Unlike a one-field component, its
    ///     DIFF may contain any of the other four fields before field 0, and reflected field order is
    ///     not a wire-format guarantee. If field 0 is absent, return false so inheritance remains in
    ///     force for the material object.
    /// </summary>
    private static bool TryReadFlipbookEnabled(
        ReadOnlySpan<byte> body,
        bool isDiff,
        out bool value)
    {
        value = false;
        var pos = 4;
        if (!isDiff)
        {
            if (!TryReadByte(body, ref pos, out var packed))
            {
                return false;
            }

            value = packed != 0;
            return true;
        }

        while (TryReadFieldIndex(body, ref pos, out var field))
        {
            switch (field)
            {
                case 0 when TryReadByte(body, ref pos, out var enabled):
                    value = enabled != 0;
                    return true;
                case 1 when TryReadUInt32(body, ref pos, out _):
                case 2 when TryReadUInt32(body, ref pos, out _):
                case 3 when TryReadSingle(body, ref pos, out _):
                case 4 when TryReadByte(body, ref pos, out _):
                    break;
                default:
                    return false;
            }
        }

        return false;
    }

    /// <summary>Reads a one-field String component in packed OBJT or indexed DIFF form.</summary>
    private static bool TryReadStringValue(ReadOnlySpan<byte> body, bool isDiff, out string value)
    {
        value = string.Empty;
        var pos = 4;
        if (isDiff)
        {
            if (pos + 2 > body.Length || BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]) != 0)
            {
                return false;
            }

            pos += 2;
        }

        if (pos + 2 > body.Length)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]);
        pos += 2;
        if (length == 0 || pos + length > body.Length)
        {
            return false;
        }

        value = Encoding.ASCII.GetString(body.Slice(pos, length)).TrimEnd('\0', ' ');
        return value.Length > 0;
    }

    /// <summary>Reads a standalone <c>BSMaterial::Color</c> component.</summary>
    private static bool TryReadColorComponent(ReadOnlySpan<byte> body, bool isDiff, out Vector4 color)
    {
        color = default;
        if (isDiff)
        {
            // Outer field 0 is Color.Value (an XMFLOAT4); its x/y/z/w fields are indexed in turn.
            var pos = 4;
            if (pos + 2 > body.Length || BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]) != 0)
            {
                return false;
            }

            pos += 2;
            return TryReadIndexedFloat4(body, ref pos, out color);
        }

        // Packed OBJT: class reference followed directly by XMFLOAT4 x/y/z/w.
        if (body.Length < 4 + 16)
        {
            return false;
        }

        color = new Vector4(
            BitConverter.ToSingle(body.Slice(4, 4)),
            BitConverter.ToSingle(body.Slice(8, 4)),
            BitConverter.ToSingle(body.Slice(12, 4)),
            BitConverter.ToSingle(body.Slice(16, 4)));
        return true;
    }

    /// <summary>
    ///     Reads Scale/Offset's nested XMFLOAT2. Each coordinate is stored independently because a
    ///     DIFF can change only one nested member while inheriting the other coordinate.
    /// </summary>
    private void ReadUvFloat2(
        ReadOnlySpan<byte> body,
        uint owner,
        bool isDiff,
        Dictionary<uint, float> xByObject,
        Dictionary<uint, float> yByObject)
    {
        var pos = 4;
        float? x = null;
        float? y = null;
        if (!isDiff)
        {
            if (!TryReadSingle(body, ref pos, out var packedX) ||
                !TryReadSingle(body, ref pos, out var packedY))
            {
                _malformedUvObjects.Add(owner);
                return;
            }

            x = packedX;
            y = packedY;
        }
        else
        {
            // Component field 0 is Value (XMFLOAT2); its x/y members are indexed again.
            if (!TryReadFieldIndex(body, ref pos, out var outerField) || outerField != 0)
            {
                _malformedUvObjects.Add(owner);
                return;
            }

            while (TryReadFieldIndex(body, ref pos, out var field))
            {
                switch (field)
                {
                    case 0 when TryReadSingle(body, ref pos, out var changedX):
                        x = changedX;
                        break;
                    case 1 when TryReadSingle(body, ref pos, out var changedY):
                        y = changedY;
                        break;
                    default:
                        _malformedUvObjects.Add(owner);
                        return;
                }
            }

            if (!x.HasValue && !y.HasValue)
            {
                _malformedUvObjects.Add(owner);
                return;
            }
        }

        if (x.HasValue)
        {
            xByObject[owner] = x.Value;
        }

        if (y.HasValue)
        {
            yByObject[owner] = y.Value;
        }
    }

    private static bool TryParseDefinedEnum<T>(string value, out T parsed)
        where T : struct, Enum
    {
        return Enum.TryParse(value, true, out parsed) && Enum.IsDefined(parsed);
    }

    private static bool TryParseColorChannel(string value, out StarfieldMaterialColorChannel channel)
    {
        if (Enum.TryParse(value, true, out channel) &&
            Enum.IsDefined(channel))
        {
            return true;
        }

        channel = default;
        return false;
    }

    private static bool TryParseAlphaBlenderMode(
        string value,
        out StarfieldMaterialAlphaBlenderMode mode)
    {
        if (Enum.TryParse(value, true, out mode) && Enum.IsDefined(mode))
        {
            return true;
        }

        mode = default;
        return false;
    }

    private static bool TryParseMaterialLayer(string value, out int layer)
    {
        const string prefix = "MATERIAL_LAYER_";
        layer = 0;
        return value.Length == prefix.Length + 1 &&
               value.StartsWith(prefix, StringComparison.Ordinal) &&
               value[^1] is >= '0' and <= '7' &&
               (layer = value[^1] - '0') >= 0;
    }

    private static bool TryReadFieldIndex(
        ReadOnlySpan<byte> body,
        ref int pos,
        out ushort field)
    {
        field = ushort.MaxValue;
        if (pos + 2 > body.Length)
        {
            return false;
        }

        field = BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]);
        pos += 2;
        return field != ushort.MaxValue;
    }

    private static bool TryReadFieldOrTerminator(
        ReadOnlySpan<byte> body,
        ref int pos,
        out ushort field,
        out bool terminated)
    {
        field = ushort.MaxValue;
        terminated = false;
        if (pos + 2 > body.Length)
        {
            return false;
        }

        field = BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]);
        pos += 2;
        terminated = field == ushort.MaxValue;
        return true;
    }

    private static bool TryConsumeTerminator(ReadOnlySpan<byte> body, ref int pos)
    {
        if (pos + 2 > body.Length ||
            BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]) != ushort.MaxValue)
        {
            return false;
        }

        pos += 2;
        return true;
    }

    private static bool TryReadByte(ReadOnlySpan<byte> body, ref int pos, out byte value)
    {
        value = 0;
        if ((uint)pos >= (uint)body.Length)
        {
            return false;
        }

        value = body[pos++];
        return true;
    }

    private static bool TryReadSingle(ReadOnlySpan<byte> body, ref int pos, out float value)
    {
        value = 0f;
        if (pos < 0 || pos + 4 > body.Length)
        {
            return false;
        }

        value = BitConverter.ToSingle(body.Slice(pos, 4));
        pos += 4;
        return true;
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> body, ref int pos, out uint value)
    {
        value = 0;
        if (pos < 0 || pos + 4 > body.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);
        pos += 4;
        return true;
    }

    private static bool TryReadLengthPrefixedString(
        ReadOnlySpan<byte> body,
        ref int pos,
        out string value)
    {
        value = string.Empty;
        if (pos < 0 || pos + 2 > body.Length)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]);
        pos += 2;
        if (length == 0 || pos + length > body.Length)
        {
            return false;
        }

        value = Encoding.ASCII.GetString(body.Slice(pos, length)).TrimEnd('\0', ' ');
        pos += length;
        return value.Length > 0;
    }

    /// <summary>
    ///     Reads a <c>BSMaterial::TextureReplacement</c>: an <c>Enabled</c> bool plus a
    ///     <c>BSMaterial::Color</c> (an XMFLOAT4). When enabled, this slot is a flat colour rather than
    ///     an image — the material genuinely has no texture there and rendering must use the colour,
    ///     not fall back to white.
    /// </summary>
    private void ReadTextureReplacement(ReadOnlySpan<byte> body, uint owner, int slot, bool isDiff)
    {
        var update = default(TextureReplacementOverride);

        if (isDiff)
        {
            var pos = 4;
            var any = false;
            while (TryReadFieldOrTerminator(body, ref pos, out var field, out var terminated))
            {
                if (terminated)
                {
                    if (!any || pos != body.Length)
                    {
                        _malformedTextureObjects.Add(owner);
                        return;
                    }

                    StoreTextureReplacement(owner, slot, update);
                    return;
                }

                if (field == 0)
                {
                    if (!TryReadByte(body, ref pos, out var enabled))
                    {
                        break;
                    }

                    update = update with { Enabled = enabled != 0 };
                    any = true;
                }
                else if (field == 1)
                {
                    // TextureReplacement.Color → Color.Value → indexed XMFLOAT4. The float-vector
                    // and Color wrappers each close before the outer replacement fields resume.
                    if (!TryReadFieldOrTerminator(
                            body, ref pos, out var colorField, out var colorTerminated) ||
                        colorTerminated || colorField != 0 ||
                        !TryReadIndexedFloat4Override(body, ref pos, out var colorUpdate) ||
                        !TryConsumeTerminator(body, ref pos))
                    {
                        break;
                    }

                    update = MergeTextureReplacement(update, colorUpdate);
                    any = true;
                }
                else
                {
                    break;
                }
            }

            _malformedTextureObjects.Add(owner);
            return;
        }

        // Sequential: class ref, Enabled byte, then four floats. Reject trailing or partial data;
        // constructor defaults are preferable to a plausible value shifted out of malformed bytes.
        if (body.Length != 5 + 16)
        {
            _malformedTextureObjects.Add(owner);
            return;
        }

        update = new TextureReplacementOverride(
            body[4] != 0,
            BitConverter.ToSingle(body.Slice(5, 4)),
            BitConverter.ToSingle(body.Slice(9, 4)),
            BitConverter.ToSingle(body.Slice(13, 4)),
            BitConverter.ToSingle(body.Slice(17, 4)));
        StoreTextureReplacement(owner, slot, update);
    }

    private void StoreTextureReplacement(
        uint owner,
        int slot,
        TextureReplacementOverride update)
    {
        if (!_replacementsByObject.TryGetValue(owner, out var slots))
        {
            _replacementsByObject[owner] = slots = [];
        }

        slots[slot] = slots.TryGetValue(slot, out var existing)
            ? MergeTextureReplacement(existing, update)
            : update;
    }

    private static TextureReplacementOverride MergeTextureReplacement(
        TextureReplacementOverride existing,
        TextureReplacementOverride update)
    {
        return new TextureReplacementOverride(
            update.Enabled ?? existing.Enabled,
            update.R ?? existing.R,
            update.G ?? existing.G,
            update.B ?? existing.B,
            update.A ?? existing.A);
    }

    private static bool TryReadIndexedFloat4Override(
        ReadOnlySpan<byte> body,
        ref int pos,
        out TextureReplacementOverride update)
    {
        update = default;
        var any = false;
        while (TryReadFieldOrTerminator(body, ref pos, out var field, out var terminated))
        {
            if (terminated)
            {
                return any;
            }

            if (field > 3 || !TryReadSingle(body, ref pos, out var value))
            {
                return false;
            }

            update = field switch
            {
                0 => update with { R = value },
                1 => update with { G = value },
                2 => update with { B = value },
                3 => update with { A = value },
                _ => update
            };
            any = true;
        }

        return false;
    }

    /// <summary>Reads an indexed XMFLOAT4 (fields 0..3) without discarding authored precision.</summary>
    private static bool TryReadIndexedFloat4(ReadOnlySpan<byte> body, ref int pos, out Vector4 color)
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

        color = new Vector4(channels[0], channels[1], channels[2], channels[3]);
        return any;
    }

    private static uint PackColor(float r, float g, float b, float a)
    {
        static uint Channel(float v)
        {
            return (uint)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        }

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
            var hasData = record[objectInfoSize - 1] != 0;
            if (dbId == 0)
            {
                continue;
            }

            ObjectCount++;
            _objectIds.Add(dbId);
            if (baseId != 0)
            {
                _baseByObject[dbId] = baseId;
            }

            // Only file-backed objects carry a real path hash; synthetic sub-objects have none, and
            // first-wins matches the reference's duplicate handling.
            if ((dir | file | ext) != 0)
            {
                _resourceIdByObject.TryAdd(dbId, (dir, file, ext));
                _objectByResourceId.TryAdd((dir, file, ext), dbId);
            }

            if (objectInfoSize >= 33 && hasData)
            {
                var parent = (
                    Dir: BinaryPrimitives.ReadUInt32LittleEndian(record[28..]),
                    File: BinaryPrimitives.ReadUInt32LittleEndian(record[20..]),
                    Ext: BinaryPrimitives.ReadUInt32LittleEndian(record[24..]));
                if ((parent.Dir | parent.File | parent.Ext) != 0)
                {
                    _parentResourceIdByObject.TryAdd(dbId, parent);
                }
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
    private void ReadTextureFile(
        ReadOnlySpan<byte> body,
        uint owner,
        int slot,
        bool isDiff,
        bool isMultiResolution)
    {
        var pos = 4; // past the class-name dword
        if (isDiff)
        {
            if (pos + 2 > body.Length || BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]) != 0)
            {
                _malformedTextureObjects.Add(owner);
                return; // only field 0 (FileName) is meaningful on these classes
            }

            pos += 2;
        }

        if (pos + 2 > body.Length)
        {
            _malformedTextureObjects.Add(owner);
            return;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]);
        pos += 2;
        if (pos + length > body.Length)
        {
            _malformedTextureObjects.Add(owner);
            return;
        }

        // The stored length INCLUDES the null terminator, so the raw decode carries a trailing NUL.
        // Left in, it survives every later normalise/compare and the archive lookup misses a texture
        // that is right there — a failure that reads as "material has no diffuse".
        var value = Encoding.ASCII.GetString(body.Slice(pos, length)).TrimEnd('\0', ' ');
        pos += length;
        if (isDiff)
        {
            if (!TryConsumeTerminator(body, ref pos) || pos != body.Length)
            {
                _malformedTextureObjects.Add(owner);
                return;
            }
        }
        else if (pos != body.Length)
        {
            _malformedTextureObjects.Add(owner);
            return;
        }

        if (!_texturesByObject.TryGetValue(owner, out var slots))
        {
            slots = [];
            _texturesByObject[owner] = slots;
        }

        if (!slots.TryAdd(slot, new TexturePathOverride(value, isMultiResolution)))
        {
            // Multiple declarations for one object/slot require component-order replay. The current
            // bounded reader cannot prove that ordering, so keep the last declaration but reject the
            // object from strict ORM admission.
            slots[slot] = new TexturePathOverride(value, isMultiResolution);
            _malformedTextureObjects.Add(owner);
        }
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

    internal readonly record struct TexturePathOverride(string Path, bool IsMultiResolution);

    private readonly record struct TextureReplacementOverride(
        bool? Enabled,
        float? R,
        float? G,
        float? B,
        float? A);
}

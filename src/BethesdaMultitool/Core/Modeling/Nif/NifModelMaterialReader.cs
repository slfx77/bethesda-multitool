using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Types the materials of placed geometry (plan section 3, "Materials and properties"; section 6, slice 5). For each
///     placed occurrence of drawable geometry it computes the effective property set (<see cref="NifModelPropertyResolver" />:
///     ancestors inherited, the nearest property of each slot wins), keys it, and builds one <see cref="SceneMaterial" />
///     per distinct key through <see cref="SceneMaterial.FromSource" /> from an exact <see cref="SceneMaterialSource" />, a
///     <see cref="SceneRenderState" />, ordered layers and deduplicated samplers. Portable fields are never set directly.
/// </summary>
/// <remarks>
///     <para>
///         Rows: NiAlphaProperty becomes blend state (the same factors for color and alpha, Add, clamp true because the
///         target is 8-bit, Assumed; enabled from bit 0), an alpha test (Reference T/255 as a double, RawReference T,
///         enabled from bit 9) and, when blending, a draw order (No Sorter: authored, else back to front).
///         NiMaterialProperty becomes base color (diffuse below BS 26, else white, with the stored alpha), ambient,
///         specular, glossiness, emissive color and the separate Emit Mult (above BS 21), lighting model Blinn-Phong
///         (Assumed). NiStencilProperty, NiZBufferProperty and NiVertexColorProperty become stencil, depth and vertex-color
///         use. Without NiZBufferProperty, depth comes from the Bethesda shader flags (Assumed). Without
///         NiVertexColorProperty, vertex colors always modulate ambient and diffuse, and vertex alpha is opacity except on
///         TallGrassShaderProperty, whose alpha is a wind weight (Assumed; RE-11): BMT's accepted renderer and GLB
///         exporter use exactly this policy, and SF2 bit 5 (Vertex_Colors) cannot be the switch, because the probe census
///         of 2026-09-23 finds it set in 0 of 20,309 shaded FNV files (28 of 10,765 FO3) while 12,268 of those FNV files
///         store color arrays the engine visibly applies.
///         BSShaderPPLightingProperty and Lighting30ShaderProperty bind texture-set slots 0-5 (base color, normal with
///         NormalGreen Down Assumed per RE-13, glow, parallax, environment with the Env Map Scale as its constant,
///         environment mask) through one sampler from Texture Clamp Mode (Assumed), plus a specular layer from the
///         normal map's alpha only when SF1 bit 0 is set (or the Xbox companion, see <see cref="NifModelTextureSource" />).
///         BSShaderNoLightingProperty is unlit, binds its File Name as base color and, above BS 26, carries its falloff as
///         <see cref="SceneViewAngleOpacity" /> (Assumed). Sky, Tile and TallGrass shaders bind their File Name.
///         NiTexturingProperty binds its maps with apply mode, clamp and filter samplers, texture-coordinate set and
///         texture transform (Assumed); the bump map has no carrier. WaterShaderProperty and NiFogProperty have no typed
///         vocabulary: a water shape keeps a material built from its other properties plus a diagnostic.
///     </para>
///     <para>
///         A layer whose texture-coordinate set the geometry does not store is left out (Shared would reject the binding)
///         and reported; the key then includes the usable set limit so geometry that does store it gets its own material.
///         A property block this reader types must have decoded exactly (<see cref="NifModelPropertyFields.RequireComplete" />).
///         Every value, assumption and omission is in the material's <c>bmt.nif.material</c> native row.
///     </para>
/// </remarks>
internal sealed class NifModelMaterialReader
{
    /// <summary>The native-state kind of the per-material rows.</summary>
    public const string MaterialKind = "bmt.nif.material";

    /// <summary>The payload version of <see cref="MaterialKind" /> rows.</summary>
    public const int MaterialPayloadVersion = 1;

    /// <summary>Diagnostic code for a second same-slot property on one object.</summary>
    public const string DuplicatePropertyDiagnostic = "bmt.nif.property-duplicate";

    /// <summary>Diagnostic code for a stored enum value nif.xml does not define.</summary>
    public const string UndefinedValueDiagnostic = "bmt.nif.property-undefined-value";

    /// <summary>Diagnostic code for a value nif.xml defines but whose layer equation is not established.</summary>
    public const string UnmappedValueDiagnostic = "bmt.nif.property-unmapped-value";

    /// <summary>Diagnostic code for a non-finite optional material value that was left undeclared.</summary>
    public const string NonFiniteValueDiagnostic = "bmt.nif.property-non-finite";

    /// <summary>Diagnostic code for water shading (no typed vocabulary).</summary>
    public const string WaterShadingDiagnostic = "bmt.nif.water-shading";

    /// <summary>Diagnostic code for a shader subclass outside the cut-1a table.</summary>
    public const string UntypedShaderDiagnostic = "bmt.nif.shader-untyped";

    /// <summary>Diagnostic code for a layer left out because its texture-coordinate set is absent.</summary>
    public const string UvSetDiagnostic = "bmt.nif.texture-uv-set";

    /// <summary>Diagnostic code for a texture transform with no Shared equivalent.</summary>
    public const string TextureTransformDiagnostic = "bmt.nif.texture-transform";

    /// <summary>Diagnostic code for an Xbox normal map whose specular companion did not resolve.</summary>
    public const string SpecularCompanionDiagnostic = "bmt.nif.xbox-specular-companion";

    /// <summary>
    ///     Coverage reason for an NiMaterialProperty whose alpha or diffuse color is not finite: the base color cannot be
    ///     left undeclared, so the property contributes nothing and stays native state (the file is not refused).
    /// </summary>
    public const string NonFiniteMaterialReason = "non-finite alpha or diffuse color: kept as native state";

    /// <summary>The normal-map green convention evidence (RE-13 open).</summary>
    public const string NormalGreenEvidence =
        "RE-13 open: Gamebryo-era Bethesda normal maps are read as Direct3D-style (green down); recalled, not measured";

    /// <summary>The texture-set slot roles (nif.xml:14189-14205, FO3-era slots 0-5).</summary>
    private static readonly SceneTextureLayerRole[] TextureSetRoles =
    [
        SceneTextureLayerRole.BaseColor, SceneTextureLayerRole.Normal, SceneTextureLayerRole.Glow,
        SceneTextureLayerRole.Parallax, SceneTextureLayerRole.Environment, SceneTextureLayerRole.EnvironmentMask
    ];

    private readonly CancellationToken _cancellationToken;
    private readonly NifModelDiagnosticSink _diagnostics = new();
    private readonly Dictionary<int, NifModelBlockDisposition> _dispositions = [];
    private readonly List<int> _locationBlocks = [];
    private readonly Dictionary<int, int> _materialByFedBlock = [];
    private readonly Dictionary<int, List<int>> _materialsByFedBlock = [];
    private readonly List<IReadOnlyList<NifModelLayerOrigin>> _layerOrigins = [];
    private readonly Dictionary<string, int> _materialByKey = new(StringComparer.Ordinal);
    private readonly List<SceneMaterial> _materials = [];
    private readonly List<JsonObject> _payloads = [];
    private readonly List<int> _placementCounts = [];
    private readonly List<JsonArray> _placements = [];
    private readonly HashSet<(int Owner, string Slot)> _reportedDuplicates = [];
    private readonly HashSet<int> _reportedShaders = [];
    private readonly NifModelPropertyResolver _resolver;
    private readonly Dictionary<SceneSampler, int> _samplerIndices = [];
    private readonly List<SceneSampler> _samplers = [];
    private readonly NifModelReadState _state;
    private readonly NifModelTextureSource _textures;
    private readonly Dictionary<int, NifTexturingPropertyView> _texturingViews = [];

    /// <summary>Creates the reader for one read.</summary>
    public NifModelMaterialReader(NifModelReadState state, NifModelNodeGraph graph, NifModelTextureSource textures,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(textures);
        _state = state;
        _textures = textures;
        _cancellationToken = cancellationToken;
        _resolver = new NifModelPropertyResolver(state, graph);
    }

    /// <summary>
    ///     The material of one placed occurrence of drawable geometry, or null when no property is effective on it. Called
    ///     by the geometry reader once per occurrence, in block and occurrence order, so materials are numbered
    ///     deterministically by first use.
    /// </summary>
    /// <exception cref="InvalidDataException">A property this reader types is corrupt.</exception>
    public int? Resolve(int nodeIndex, NifModelGeometryData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _cancellationToken.ThrowIfCancellationRequested();
        var properties = _resolver.Resolve(nodeIndex);
        ReportDuplicates(properties);
        if (properties.IsEmpty)
        {
            return null;
        }

        // -1 when the geometry stores no texture coordinates: every texture layer is then left out, never bound to set 0.
        var highestUvSet = data.UvSetCount - 1;
        var key = properties.Key + (highestUvSet < 0 ? "|uv=none" : UvKey(properties, highestUvSet));
        if (!_materialByKey.TryGetValue(key, out var index))
        {
            index = Build(properties, highestUvSet, key);
            _materialByKey.Add(key, index);
        }

        _placementCounts[index]++;
        if (_placements[index].Count < NifModelNativeValues.MaximumInlineElements)
        {
            _placements[index].Add(new JsonObject
            {
                ["geometryBlock"] = _resolver.BlockOf(nodeIndex),
                ["dataBlock"] = data.BlockIndex,
                ["node"] = nodeIndex
            });
        }

        return index;
    }

    /// <summary>Finishes the read: materials, samplers, native rows, dispositions and diagnostics.</summary>
    public NifModelMaterialResult Complete()
    {
        var rows = new List<SceneNativeState>(_materials.Count);
        var owner = _state.Item.Reference;
        for (var i = 0; i < _materials.Count; i++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var payload = _payloads[i];
            payload["placements"] = new JsonObject { ["count"] = _placementCounts[i], ["first"] = _placements[i] };
            var block = _state.Blocks[_locationBlocks[i]];
            rows.Add(new SceneNativeState(new SceneElementRef(SceneElementKind.Material, i), MaterialKind,
                MaterialPayloadVersion, payload.ToJsonString(),
                new SceneSourceLocation(owner.SourceId, NifModelCoverage.Identity(block.Index), block.Offset,
                    block.Size, owner)));
        }

        var materialsByFedBlock = new Dictionary<int, IReadOnlyList<int>>(_materialsByFedBlock.Count);
        foreach (var (block, fed) in _materialsByFedBlock)
        {
            materialsByFedBlock.Add(block, fed.AsReadOnly());
        }

        return new NifModelMaterialResult(_materials.AsReadOnly(), _samplers.AsReadOnly(), rows.AsReadOnly(),
            _dispositions, _materialByFedBlock, _diagnostics.ToList(), materialsByFedBlock, _layerOrigins.AsReadOnly());
    }

    private int Build(NifModelPropertySet properties, int highestUvSet, string key)
    {
        var build = new NifModelMaterialBuild(properties, highestUvSet);
        var index = _materials.Count;
        if (properties.Get(NifPropertySlot.Material) is { } material)
        {
            ApplyMaterial(build, material);
        }

        if (properties.Get(NifPropertySlot.Shade) is { } shade)
        {
            ApplyShade(build, shade);
        }

        if (properties.Get(NifPropertySlot.Texturing) is { } texturing)
        {
            ApplyTexturing(build, texturing);
        }

        if (properties.Get(NifPropertySlot.Alpha) is { } alpha)
        {
            ApplyAlpha(build, alpha);
        }

        if (properties.Get(NifPropertySlot.Stencil) is { } stencil)
        {
            ApplyStencil(build, stencil);
        }

        if (properties.Get(NifPropertySlot.ZBuffer) is { } zBuffer)
        {
            ApplyZBuffer(build, zBuffer);
        }

        if (properties.Get(NifPropertySlot.VertexColor) is { } vertexColor)
        {
            ApplyVertexColor(build, vertexColor);
        }

        foreach (var property in properties.Properties)
        {
            if (property.Slot is NifPropertySlot.Fog or NifPropertySlot.Specular or NifPropertySlot.Wireframe or
                NifPropertySlot.Dither or NifPropertySlot.Other)
            {
                var type = _state.Blocks[property.BlockIndex].Type;
                build.Facts[property.SlotName] = new JsonObject
                {
                    ["block"] = property.BlockIndex,
                    ["type"] = type,
                    ["typed"] = false,
                    ["reason"] = NifModelCoverage.CategoryReason(_state.Schema, type)
                };
            }
        }

        build.Assume("lighting model: Gamebryo lighting read as Blinn-Phong (Assumed; design section 6.1)");
        var source = new SceneMaterialSource(build.BaseColor, build.Unlit, SceneLightingModel.BlinnPhong, build.Layers,
            build.AmbientColor, build.SpecularColor, build.Glossiness, build.EmissiveColor, build.EmissiveMultiplier,
            normalGreen: build.NormalGreen);
        var renderState = build.HasRenderState
            ? new SceneRenderState(build.Blend, build.AlphaTest, build.Depth, build.Stencil, null, null, build.DrawOrder,
                build.VertexColorUse, build.ViewAngleOpacity)
            : null;
        var name = build.Name ?? string.Create(CultureInfo.InvariantCulture, $"material{index}");
        _materials.Add(SceneMaterial.FromSource(name, source, renderState));
        foreach (var block in build.TypedBlocks.Order())
        {
            _dispositions[block] = NifModelBlockDisposition.Typed;
            _materialByFedBlock.TryAdd(block, index);
            if (!_materialsByFedBlock.TryGetValue(block, out var fed))
            {
                fed = [];
                _materialsByFedBlock.Add(block, fed);
            }

            fed.Add(index);
        }

        _layerOrigins.Add(build.LayerOrigins.AsReadOnly());
        _payloads.Add(Payload(build, key, index));
        _locationBlocks.Add(properties.Properties[0].BlockIndex);
        _placements.Add(new JsonArray());
        _placementCounts.Add(0);
        return index;
    }

    private string UvKey(NifModelPropertySet properties, int highestUvSet)
    {
        if (properties.Get(NifPropertySlot.Texturing) is not { } texturing)
        {
            return "";
        }

        var maximum = TexturingView(texturing.BlockIndex).MaximumUvSet;
        return maximum == 0
            ? ""
            : string.Create(CultureInfo.InvariantCulture, $"|uv={Math.Min(maximum, highestUvSet)}");
    }

    private void ApplyMaterial(NifModelMaterialBuild build, NifModelEffectiveProperty property)
    {
        var block = _state.Blocks[property.BlockIndex];
        NifModelPropertyFields.RequireComplete(block);
        var view = NifMaterialPropertyView.Read(block);
        if (!float.IsFinite(view.Alpha) || view.DiffuseColor is { } stored && !IsFinite(stored))
        {
            // The base color is required and cannot be left undeclared, so the whole property stays native state.
            NonFiniteOptional(block, "Alpha or Diffuse Color");
            build.Facts["material"] = Untyped(block, NonFiniteMaterialReason);
            MarkNativeOnly(block.Index, NonFiniteMaterialReason);
            return;
        }

        build.Name ??= NifModelPropertyFields.Name(block);
        build.BaseColor = view.DiffuseColor is { } diffuse
            ? new Vector4(diffuse, view.Alpha)
            : new Vector4(1f, 1f, 1f, view.Alpha);

        build.AmbientColor = Finite(view.AmbientColor, block, "Ambient Color");
        build.SpecularColor = Finite(view.SpecularColor, block, "Specular Color");
        build.EmissiveColor = Finite(view.EmissiveColor, block, "Emissive Color");
        build.Glossiness = Finite(view.Glossiness, block, "Glossiness");
        build.EmissiveMultiplier = view.EmissiveMultiplier is { } multiplier
            ? Finite(multiplier, block, "Emissive Mult")
            : null;
        var facts = view.ToJson();
        facts["block"] = block.Index;
        facts["bsVersion"] = _state.Header.BsVersion;
        facts["baseColorRule"] = view.DiffuseColor is null
            ? "white with the stored alpha: the block has no Diffuse Color field (BS 26 and above)"
            : "the stored Diffuse Color with the stored alpha (below BS 26)";
        facts["emissiveMultRule"] = view.EmissiveMultiplier is null
            ? "absent: Emit Mult exists above BS 21 only"
            : "kept separate from the emissive color (never pre-multiplied)";
        build.Facts["material"] = facts;
        build.TypedBlocks.Add(block.Index);
    }

    private void ApplyShade(NifModelMaterialBuild build, NifModelEffectiveProperty property)
    {
        var block = _state.Blocks[property.BlockIndex];
        var type = block.Type;
        if (string.Equals(type, NifShaderPropertyView.WaterType, StringComparison.Ordinal))
        {
            build.Facts["shade"] = Untyped(block, NifModelCoverage.WaterReason);
            build.Assume("water: a placeholder material built from the other properties; water shading is not typed");
            if (_reportedShaders.Add(block.Index))
            {
                _diagnostics.Add(WaterShadingDiagnostic, string.Create(CultureInfo.InvariantCulture,
                    $"Block {block.Index} (WaterShaderProperty) has no typed vocabulary (later-cut 2); shapes using it " +
                    $"keep a placeholder material from their other properties, and the shader stays native state."));
            }

            return;
        }

        if (!NifShaderPropertyView.IsTypedShader(type))
        {
            var reason = NifModelCoverage.CategoryReason(_state.Schema, type) ?? NifModelCoverage.FallbackReason;
            build.Facts["shade"] = Untyped(block, reason);
            if (_reportedShaders.Add(block.Index))
            {
                _diagnostics.Add(UntypedShaderDiagnostic, string.Create(CultureInfo.InvariantCulture,
                    $"Block {block.Index} ({type}) is a shade property outside the cut-1a shader table; it stays " +
                    $"native state and contributes nothing to the material."));
            }

            return;
        }

        NifModelPropertyFields.RequireComplete(block);
        var view = NifShaderPropertyView.Read(block);
        build.Name ??= NifModelPropertyFields.Name(block);
        var facts = view.ToJson();
        facts["block"] = block.Index;
        build.Facts["shade"] = facts;
        build.TypedBlocks.Add(block.Index);

        if (build.Properties.Get(NifPropertySlot.ZBuffer) is null)
        {
            build.Depth = new SceneDepthState(view.DepthTest, view.DepthWrite, SceneCompareFunction.LessEqual);
            build.Assume("depth from SF1 bit 31 (test) and SF2 bit 0 (write) with compare LessEqual " +
                         "(Assumed; no NiZBufferProperty is effective)");
        }

        if (build.Properties.Get(NifPropertySlot.VertexColor) is null)
        {
            var tallGrass = string.Equals(type, NifShaderPropertyView.TallGrassType, StringComparison.Ordinal);
            build.VertexColorUse = new SceneVertexColorUse(SceneVertexColorSource.AmbientDiffuse,
                SceneVertexLightingMode.EmissiveAmbientDiffuse, Vector4.One, !tallGrass);
            build.Assume(tallGrass
                ? "vertex colors modulate ambient and diffuse; vertex alpha is the grass wind weight, not opacity; " +
                  "lighting emissive + ambient + diffuse (Assumed; RE-11; BMT renderer policy)"
                : "vertex colors modulate ambient and diffuse and vertex alpha is opacity, whatever SF2 bit 5 " +
                  "(Vertex_Colors) and SF1 bit 3 (Vertex_Alpha) say; lighting emissive + ambient + diffuse (Assumed; " +
                  "RE-11; BMT renderer policy; SF2 bit 5 is set in 0 of 20,309 shaded FNV files)");
        }

        var sampler = ShaderSampler(build, view, block);
        switch (type)
        {
            case NifShaderPropertyView.PerPixelLightingType or NifShaderPropertyView.Lighting30Type:
                AddTextureSetLayers(build, view, sampler);
                break;
            case NifShaderPropertyView.NoLightingType:
                build.Unlit = true;
                AddFileLayer(build, view, sampler, block);
                AddFalloff(build, view, block);
                break;
            default:
                AddFileLayer(build, view, sampler, block);
                break;
        }
    }

    private int ShaderSampler(NifModelMaterialBuild build, NifShaderPropertyView view, NifDecodedBlock block)
    {
        if (view.TextureClampMode is not { } clamp)
        {
            build.Assume(string.Create(CultureInfo.InvariantCulture,
                $"{view.Type} stores no Texture Clamp Mode: wrap in both directions (Assumed)"));
            return Sampler(new SceneSampler(SceneTextureWrap.Repeat, SceneTextureWrap.Repeat));
        }

        build.Assume("one sampler from Texture Clamp Mode (S to U, T to V) for every shader layer (Assumed)");
        if (NifModelRenderStateMapping.Clamp(clamp) is { } wrap)
        {
            return Sampler(new SceneSampler(wrap.U, wrap.V));
        }

        Undefined(block, "Texture Clamp Mode", clamp, "the sampler wraps in both directions as an assumption");
        build.Assume(string.Create(CultureInfo.InvariantCulture,
            $"undefined Texture Clamp Mode {clamp}: wrap in both directions (Assumed)"));
        return Sampler(new SceneSampler(SceneTextureWrap.Repeat, SceneTextureWrap.Repeat));
    }

    private void AddTextureSetLayers(NifModelMaterialBuild build, NifShaderPropertyView view, int sampler)
    {
        var setIndex = view.TextureSet!.Value;
        if (setIndex < 0)
        {
            build.Facts["textureSet"] = "none";
            return;
        }

        if (OmitWithoutUvSets(build, setIndex, "texture set"))
        {
            return;
        }

        var setBlock = _state.Blocks[setIndex];
        if (!string.Equals(setBlock.Type, "BSShaderTextureSet", StringComparison.Ordinal))
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"A {view.Type} links block {setIndex} ({setBlock.Type}) as its Texture Set; it must be a BSShaderTextureSet."));
        }

        NifModelPropertyFields.RequireComplete(setBlock);
        var set = NifTextureSetView.Read(setBlock);
        build.TypedBlocks.Add(setIndex);
        build.Facts["textureSet"] = new JsonObject
        {
            ["block"] = setIndex,
            ["count"] = set.Textures.Count,
            ["slotsBeyondFive"] = Math.Max(0, set.Textures.Count - TextureSetRoles.Length)
        };
        int? normalImage = null;
        SceneTextureBinding? normalBinding = null;
        for (var slot = 0; slot < Math.Min(set.Textures.Count, TextureSetRoles.Length); slot++)
        {
            var path = set.Textures[slot];
            var text = path.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var role = TextureSetRoles[slot];
            var origin = string.Create(CultureInfo.InvariantCulture, $"texture set slot {slot}");
            Vector4? constant = null;
            if (role == SceneTextureLayerRole.Environment)
            {
                if (!float.IsFinite(view.EnvironmentMapScale))
                {
                    Omit(build, origin, "the Environment Map Scale is not finite");
                    continue;
                }

                constant = new Vector4(view.EnvironmentMapScale);
            }

            var image = _textures.Resolve(text, path.RawBytes, role, setIndex);
            var binding = new SceneTextureBinding(image, sampler);
            AddLayer(build, NifModelLayerAlgebra.Modulate(role, binding, constant), origin, setIndex, "product",
                NifModelLayerOrigin.TextureSetSlot(slot));
            if (role == SceneTextureLayerRole.Normal)
            {
                normalImage = image;
                normalBinding = binding;
                DeclareNormalGreen(build);
            }
        }

        AddSpecularLayer(build, view, normalImage, normalBinding, sampler, setIndex);
    }

    private void AddSpecularLayer(NifModelMaterialBuild build, NifShaderPropertyView view, int? normalImage,
        SceneTextureBinding? normalBinding, int sampler, int setIndex)
    {
        if (normalImage is not { } normal || normalBinding is not { } binding)
        {
            build.Facts["specular"] = view.Specular
                ? "not bound: SF1 bit 0 (Specular) is set but texture set slot 1 is empty"
                : "not bound: SF1 bit 0 (Specular) is clear";
            return;
        }

        if (!view.Specular)
        {
            build.Facts["specular"] = "not bound: SF1 bit 0 (Specular) is clear";
            return;
        }

        if (_textures.IsXboxNormalMap(normal))
        {
            if (_textures.TryResolveSpecularCompanion(normal, setIndex) is { } companion)
            {
                var companionBinding = new SceneTextureBinding(companion, sampler);
                var swizzle = new SceneTextureSwizzle(SceneTextureChannel.Red, SceneTextureChannel.Green,
                    SceneTextureChannel.Blue, SceneTextureChannel.Red);
                AddLayer(build,
                    NifModelLayerAlgebra.Modulate(SceneTextureLayerRole.Specular, companionBinding, swizzle: swizzle),
                    "Xbox specular companion of texture set slot 1", setIndex, "product, alpha from the red channel",
                    NifModelLayerOrigin.XboxSpecularCompanion);
                build.Facts["specular"] = "the Xbox _s companion (its own image; BC5 and BC4 are never merged)";
                build.Assume("Xbox specular companion: its red channel is the specular strength (alpha <- red; Assumed)");
                return;
            }

            if (_textures.IsBc5(normal))
            {
                build.Facts["specular"] =
                    "not bound: the Xbox specular companion did not resolve and the BC5 normal map has no alpha";
                _diagnostics.Add(SpecularCompanionDiagnostic, string.Create(CultureInfo.InvariantCulture,
                    $"Image {normal} is an Xbox BC5 normal map with SF1 Specular set, but its _s companion did not " +
                    $"resolve; the material has no specular layer."));
                return;
            }

            build.Assume("an Xbox normal map without a resolvable _s companion supplies specular from its alpha " +
                         "(Assumed: its format is not BC5)");
        }

        AddLayer(build, NifModelLayerAlgebra.Modulate(SceneTextureLayerRole.Specular, binding),
            "texture set slot 1 alpha (normal-map gloss)", setIndex, "product", NifModelLayerOrigin.NormalMapGloss);
        build.Facts["specular"] = "the normal map's alpha (SF1 bit 0 set)";
    }

    private void AddFileLayer(NifModelMaterialBuild build, NifShaderPropertyView view, int sampler,
        NifDecodedBlock block)
    {
        if (view.FileName is not { } file || string.IsNullOrWhiteSpace(file.Text))
        {
            build.Facts["fileName"] = "none";
            return;
        }

        if (OmitWithoutUvSets(build, block.Index, "shader File Name"))
        {
            return;
        }

        var image = _textures.Resolve(file.Text, file.RawBytes, SceneTextureLayerRole.BaseColor, block.Index);
        AddLayer(build, NifModelLayerAlgebra.Modulate(SceneTextureLayerRole.BaseColor,
            new SceneTextureBinding(image, sampler)), "shader File Name", block.Index, "product",
            NifModelLayerOrigin.ShaderFileName);
    }

    private void AddFalloff(NifModelMaterialBuild build, NifShaderPropertyView view, NifDecodedBlock block)
    {
        if (!view.HasFalloff)
        {
            build.Facts["falloff"] = "absent: the falloff fields exist above BS 26 only";
            return;
        }

        var start = view.FalloffStartAngle!.Value;
        var stop = view.FalloffStopAngle!.Value;
        var startOpacity = view.FalloffStartOpacity!.Value;
        var stopOpacity = view.FalloffStopOpacity!.Value;
        if (!float.IsFinite(start) || !float.IsFinite(stop) || !float.IsFinite(startOpacity) ||
            !float.IsFinite(stopOpacity))
        {
            NonFiniteOptional(block, "Falloff");
            return;
        }

        build.ViewAngleOpacity = new SceneViewAngleOpacity(start, stop, startOpacity, stopOpacity, true, true,
            provenance: SceneValueProvenance.Assumed,
            evidence: "nif.xml stores the falloff angles as cosines; the ramp over the absolute normal/view dot " +
                      "product is assumed always active (no enable flag is known)");
    }

    private void ApplyTexturing(NifModelMaterialBuild build, NifModelEffectiveProperty property)
    {
        var block = _state.Blocks[property.BlockIndex];
        var view = TexturingView(block.Index);
        build.Name ??= NifModelPropertyFields.Name(block);
        var facts = view.ToJson();
        facts["block"] = block.Index;
        build.Facts["texturing"] = facts;
        build.TypedBlocks.Add(block.Index);
        build.Assume("NiTexturingProperty maps: apply modes and map equations follow Gamebryo's fixed-function " +
                     "texturing (Assumed)");
        foreach (var map in view.Maps)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var origin = "NiTexturingProperty " + map.Slot + " map";
            if (string.Equals(map.Slot, "Bump Map", StringComparison.Ordinal))
            {
                if (map.Source >= 0)
                {
                    Omit(build, origin, "the bump map has no typed carrier (its values are native state)");
                }

                continue;
            }

            if (map.Source < 0)
            {
                continue;
            }

            if (map.UvSet > build.HighestUvSet)
            {
                Omit(build, origin, build.HighestUvSet < 0
                    ? "the placed geometry stores no texture coordinates"
                    : string.Create(CultureInfo.InvariantCulture,
                        $"texture-coordinate set {map.UvSet} is not stored by the placed geometry (highest set {build.HighestUvSet})"));
                _diagnostics.Add(UvSetDiagnostic, string.Create(CultureInfo.InvariantCulture,
                    $"Block {block.Index}'s {map.Slot} map uses texture-coordinate set {map.UvSet}, which a geometry " +
                    $"using it does not store; that material leaves the layer out."));
                continue;
            }

            SceneTextureTransform? transform = null;
            if (map.HasTransform)
            {
                if (!NifModelTextureTransform.TryCompose(map.TransformMethod, map.Translation, map.Scale, map.Rotation,
                        map.Center, out var composed, out var residual))
                {
                    Omit(build, origin, string.Create(CultureInfo.InvariantCulture,
                        $"its texture transform (method {map.TransformMethod}) has no Shared equivalent " +
                        $"(residual {residual:R})"));
                    _diagnostics.Add(TextureTransformDiagnostic, string.Create(CultureInfo.InvariantCulture,
                        $"Block {block.Index}'s {map.Slot} map has a texture transform with no Shared equivalent; " +
                        $"the layer is left out and its values stay native state."));
                    continue;
                }

                transform = composed;
                build.Assume("texture transform: " + NifModelTextureTransform.Formula);
            }

            var role = MapRole(map.Slot);
            var sampler = MapSampler(build, map, block);
            var sourceBlock = _state.Blocks[map.Source];
            if (!_state.Schema.Inherits(sourceBlock.Type, "NiSourceTexture"))
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"NIF block {block.Index} ({block.Type}) {map.Slot} map links block {map.Source} " +
                    $"({sourceBlock.Type}), not an NiSourceTexture."));
            }

            NifModelPropertyFields.RequireComplete(sourceBlock);
            var sourceView = NifSourceTextureView.Read(sourceBlock);
            int image;
            if (sourceView.IsExternal)
            {
                if (string.IsNullOrWhiteSpace(sourceView.FileNameText))
                {
                    Omit(build, origin, "its external NiSourceTexture names no file");
                    continue;
                }

                image = _textures.Resolve(sourceView.FileNameText, sourceView.FileName!.RawBytes, role,
                    sourceBlock.Index);
            }
            else
            {
                image = _textures.ResolveEmbedded(sourceView.FileNameText, sourceBlock.Index, sourceView.PixelData);
            }

            var binding = new SceneTextureBinding(image, sampler)
            {
                TextureCoordinateSet = map.UvSet,
                Transform = transform
            };
            var (layer, equation) = MapLayer(build, view, map, role, binding, block);
            AddLayer(build, layer, origin, block.Index, equation, map.Slot, map.UvSet);
            if (role == SceneTextureLayerRole.Normal)
            {
                DeclareNormalGreen(build);
            }
        }
    }

    private (SceneTextureLayer Layer, string Equation) MapLayer(NifModelMaterialBuild build,
        NifTexturingPropertyView view, NifTextureMapView map, SceneTextureLayerRole role, SceneTextureBinding binding,
        NifDecodedBlock block)
    {
        switch (map.Slot)
        {
            case "Base":
                switch (view.ApplyMode)
                {
                    case 0:
                        return (NifModelLayerAlgebra.Replace(role, binding), "replace (APPLY_REPLACE)");
                    case 1:
                        return (NifModelLayerAlgebra.Decal(role, binding), "decal (APPLY_DECAL)");
                    case 2:
                        return (NifModelLayerAlgebra.Modulate(role, binding), "product (APPLY_MODULATE)");
                    default:
                        if (view.ApplyMode is 3 or 4)
                        {
                            _diagnostics.Add(UnmappedValueDiagnostic, string.Create(CultureInfo.InvariantCulture,
                                $"Block {block.Index} ({block.Type}) stores Apply Mode {view.ApplyMode} " +
                                $"({(view.ApplyMode == 3 ? "APPLY_HILIGHT" : "APPLY_HILIGHT2")}), which nif.xml defines " +
                                $"but whose layer equation is not established; a product layer is typed as an assumption " +
                                $"and the raw value stays in native state."));
                        }
                        else
                        {
                            Undefined(block, "Apply Mode", view.ApplyMode,
                                "a product layer is typed from it as an assumption");
                        }

                        build.Assume(string.Create(CultureInfo.InvariantCulture,
                            $"apply mode {view.ApplyMode} has no established equation: product (Assumed)"));
                        return (NifModelLayerAlgebra.Modulate(role, binding), "product (apply mode not established)");
                }
            case "Detail":
                return (NifModelLayerAlgebra.DoubledModulate(role, binding), "doubled product (MODULATE2X, Assumed)");
            default:
                return map.Slot.StartsWith("Decal", StringComparison.Ordinal)
                    ? (NifModelLayerAlgebra.Decal(role, binding), "decal")
                    : (NifModelLayerAlgebra.Modulate(role, binding), "product");
        }
    }

    private int MapSampler(NifModelMaterialBuild build, NifTextureMapView map, NifDecodedBlock block)
    {
        var wrap = NifModelRenderStateMapping.Clamp(map.ClampMode);
        if (wrap is null)
        {
            Undefined(block, map.Slot + " Clamp Mode", map.ClampMode,
                "the sampler wraps in both directions as an assumption");
            build.Assume(string.Create(CultureInfo.InvariantCulture,
                $"undefined Clamp Mode {map.ClampMode}: wrap in both directions (Assumed)"));
        }

        var filter = NifModelRenderStateMapping.Filter(map.FilterMode);
        if (filter is null)
        {
            Undefined(block, map.Slot + " Filter Mode", map.FilterMode);
        }
        else if (map.FilterMode == 6)
        {
            build.Assume("FILTER_ANISOTROPIC read as trilinear; the anisotropy has no typed carrier");
        }

        var (u, v) = wrap ?? (SceneTextureWrap.Repeat, SceneTextureWrap.Repeat);
        var (min, mag) = filter ?? (SceneTextureFilter.Automatic, SceneTextureFilter.Automatic);
        return Sampler(new SceneSampler(u, v, min, mag));
    }

    private static SceneTextureLayerRole MapRole(string slot)
    {
        return slot switch
        {
            "Base" => SceneTextureLayerRole.BaseColor,
            "Dark" => SceneTextureLayerRole.Dark,
            "Detail" => SceneTextureLayerRole.Detail,
            "Gloss" => SceneTextureLayerRole.Specular,
            "Glow" => SceneTextureLayerRole.Glow,
            "Normal" => SceneTextureLayerRole.Normal,
            "Parallax" => SceneTextureLayerRole.Parallax,
            _ => SceneTextureLayerRole.Decal
        };
    }

    private void ApplyAlpha(NifModelMaterialBuild build, NifModelEffectiveProperty property)
    {
        var block = _state.Blocks[property.BlockIndex];
        NifModelPropertyFields.RequireComplete(block);
        var view = NifAlphaPropertyView.Read(block);
        var sourceFactor = NifModelRenderStateMapping.BlendFactor(view.SourceFunction);
        var destinationFactor = NifModelRenderStateMapping.BlendFactor(view.DestinationFunction);
        if (sourceFactor is null || destinationFactor is null)
        {
            if (sourceFactor is null)
            {
                Undefined(block, "Source Blend Mode", view.SourceFunction);
            }

            if (destinationFactor is null)
            {
                Undefined(block, "Destination Blend Mode", view.DestinationFunction);
            }
        }
        else
        {
            var equation = new SceneBlendEquation(sourceFactor, destinationFactor, SceneBlendOperation.Add, true);
            build.Blend = new SceneBlendState(equation, equation, view.BlendEnabled);
            build.Assume("blend: the same factors for color and alpha, Add, clamped to [0,1] (Assumed: 8-bit target)");
        }

        build.AlphaTest = new SceneAlphaTest(NifModelRenderStateMapping.TestFunction(view.TestFunction)!.Value,
            view.Threshold / 255.0, view.Threshold, view.TestEnabled);
        if (view.BlendEnabled)
        {
            build.DrawOrder = new SceneDrawOrder(0, view.NoSorter ? SceneDrawSort.Authored : SceneDrawSort.BackToFront);
        }

        var facts = view.ToJson();
        facts["block"] = block.Index;
        facts["sourceFactor"] = NifModelRenderStateMapping.BlendFactorName(view.SourceFunction);
        facts["destinationFactor"] = NifModelRenderStateMapping.BlendFactorName(view.DestinationFunction);
        facts["testReference"] = "T/255 against 8-bit alpha (a8 > T iff a8/255 > T/255)";
        build.Facts["alpha"] = facts;
        build.TypedBlocks.Add(block.Index);
    }

    private void ApplyStencil(NifModelMaterialBuild build, NifModelEffectiveProperty property)
    {
        var block = _state.Blocks[property.BlockIndex];
        NifModelPropertyFields.RequireComplete(block);
        var view = NifStencilPropertyView.Read(block);
        var drawMode = NifModelRenderStateMapping.StencilDrawMode(view.DrawMode, out var assumed);
        if (assumed)
        {
            build.Assume("stencil DRAW_CCW_OR_BOTH (0, the application default) read as counterclockwise (Assumed)");
        }

        var fail = NifModelRenderStateMapping.StencilAction(view.FailAction);
        var depthFail = NifModelRenderStateMapping.StencilAction(view.DepthFailAction);
        var pass = NifModelRenderStateMapping.StencilAction(view.PassAction);
        SceneStencilTest? test = null;
        if (fail is { } failOperation && depthFail is { } depthFailOperation && pass is { } passOperation)
        {
            test = new SceneStencilTest(NifModelRenderStateMapping.StencilFunction(view.TestFunction)!.Value,
                view.Reference, view.Mask, view.Mask, failOperation, depthFailOperation, passOperation, view.Enabled);
            build.Assume("the stencil mask is both the read and the write mask (Assumed)");
            if (new[] { failOperation, depthFailOperation, passOperation }.Any(o =>
                    o is SceneStencilOperation.IncrementSaturate or SceneStencilOperation.DecrementSaturate))
            {
                build.Assume("stencil INCREMENT and DECREMENT saturate (Assumed)");
            }
        }
        else
        {
            Undefined(block, "Stencil Action", view.Flags);
        }

        build.Stencil = new SceneStencilState(drawMode, test);
        var facts = view.ToJson();
        facts["block"] = block.Index;
        build.Facts["stencil"] = facts;
        build.TypedBlocks.Add(block.Index);
    }

    private void ApplyZBuffer(NifModelMaterialBuild build, NifModelEffectiveProperty property)
    {
        var block = _state.Blocks[property.BlockIndex];
        NifModelPropertyFields.RequireComplete(block);
        var view = NifZBufferPropertyView.Read(block);
        build.Depth = new SceneDepthState(view.Test, view.Write,
            NifModelRenderStateMapping.TestFunction(view.TestFunction)!.Value);
        var facts = view.ToJson();
        facts["block"] = block.Index;
        build.Facts["zBuffer"] = facts;
        build.TypedBlocks.Add(block.Index);
    }

    private void ApplyVertexColor(NifModelMaterialBuild build, NifModelEffectiveProperty property)
    {
        var block = _state.Blocks[property.BlockIndex];
        NifModelPropertyFields.RequireComplete(block);
        var view = NifVertexColorPropertyView.Read(block);
        var facts = view.ToJson();
        facts["block"] = block.Index;
        build.Facts["vertexColor"] = facts;
        build.TypedBlocks.Add(block.Index);
        if (NifModelRenderStateMapping.VertexColorSource(view.SourceVertexMode) is not { } source)
        {
            Undefined(block, "Source Vertex Mode", view.SourceVertexMode);
            return;
        }

        build.VertexColorUse = new SceneVertexColorUse(source,
            NifModelRenderStateMapping.VertexLighting(view.LightingMode), Vector4.One,
            source == SceneVertexColorSource.AmbientDiffuse);
        build.Assume("vertex alpha affects opacity only when vertex colors feed ambient and diffuse (Assumed)");
    }

    private static void DeclareNormalGreen(NifModelMaterialBuild build)
    {
        build.NormalGreen ??= new SceneNormalGreenConvention(SceneNormalGreen.Down, SceneValueProvenance.Assumed,
            NormalGreenEvidence);
        build.Assume("normal maps: green down (Assumed; RE-13)");
    }

    private static void AddLayer(NifModelMaterialBuild build, SceneTextureLayer layer, string origin, int sourceBlock,
        string equation, string slot, int uvSet = 0)
    {
        build.Layers.Add(layer);
        build.LayerOrigins.Add(new NifModelLayerOrigin(sourceBlock, slot));
        var facts = new JsonObject
        {
            ["role"] = layer.Role.ToString(),
            ["image"] = layer.Binding.ImageIndex,
            ["sampler"] = layer.Binding.SamplerIndex,
            ["uvSet"] = uvSet,
            ["source"] = origin,
            ["block"] = sourceBlock,
            ["equation"] = equation
        };
        if (layer.Binding.Transform is { } transform)
        {
            facts["transform"] = new JsonObject
            {
                ["offset"] = new JsonArray(NifModelNativeValues.Float(transform.Offset.X),
                    NifModelNativeValues.Float(transform.Offset.Y)),
                ["scale"] = new JsonArray(NifModelNativeValues.Float(transform.Scale.X),
                    NifModelNativeValues.Float(transform.Scale.Y)),
                ["rotation"] = NifModelNativeValues.Float(transform.Rotation)
            };
        }

        if (layer.Swizzle != SceneTextureSwizzle.Identity)
        {
            facts["swizzle"] = string.Join(",", layer.Swizzle.Red, layer.Swizzle.Green, layer.Swizzle.Blue,
                layer.Swizzle.Alpha);
        }

        build.LayerFacts.Add(facts);
    }

    private static void Omit(NifModelMaterialBuild build, string origin, string reason)
    {
        build.OmittedLayers.Add(new JsonObject { ["source"] = origin, ["reason"] = reason });
    }

    /// <summary>
    ///     When the placed geometry stores no texture coordinates, leaves a set-0 texture layer out and reports it: bound,
    ///     it would sample one texel over the whole surface. Its texture is not resolved.
    /// </summary>
    /// <returns>True when the layer was left out.</returns>
    private bool OmitWithoutUvSets(NifModelMaterialBuild build, int sourceBlock, string origin)
    {
        if (build.HighestUvSet >= 0)
        {
            return false;
        }

        Omit(build, origin, "the placed geometry stores no texture coordinates");
        _diagnostics.Add(UvSetDiagnostic, string.Create(CultureInfo.InvariantCulture,
            $"Block {sourceBlock}'s {origin} needs texture-coordinate set 0, which a geometry using it does not store; " +
            $"that material leaves the layer out."));
        return true;
    }

    private NifTexturingPropertyView TexturingView(int blockIndex)
    {
        if (!_texturingViews.TryGetValue(blockIndex, out var view))
        {
            var block = _state.Blocks[blockIndex];
            NifModelPropertyFields.RequireComplete(block);
            view = NifTexturingPropertyView.Read(block);
            _texturingViews.Add(blockIndex, view);
        }

        return view;
    }

    private int Sampler(SceneSampler sampler)
    {
        if (!_samplerIndices.TryGetValue(sampler, out var index))
        {
            index = _samplers.Count;
            _samplers.Add(sampler);
            _samplerIndices.Add(sampler, index);
        }

        return index;
    }

    private void ReportDuplicates(NifModelPropertySet properties)
    {
        foreach (var duplicate in properties.Duplicates)
        {
            if (_reportedDuplicates.Add((duplicate.OwnerBlockIndex, duplicate.SlotName)))
            {
                _diagnostics.Add(DuplicatePropertyDiagnostic, string.Create(CultureInfo.InvariantCulture,
                    $"Block {duplicate.OwnerBlockIndex} lists two {duplicate.SlotName} properties; block " +
                    $"{duplicate.KeptBlockIndex} (first in its Properties list) is used and block " +
                    $"{duplicate.IgnoredBlockIndex} is ignored (Assumed)."));
            }
        }
    }

    private JsonObject Payload(NifModelMaterialBuild build, string key, int index)
    {
        var payload = new JsonObject
        {
            ["index"] = index,
            ["key"] = key,
            ["properties"] = new JsonArray(build.Properties.Properties.Select(p => (JsonNode?)new JsonObject
            {
                ["slot"] = p.SlotName,
                ["block"] = p.BlockIndex,
                ["type"] = _state.Blocks[p.BlockIndex].Type,
                ["owner"] = p.OwnerBlockIndex,
                ["typed"] = build.TypedBlocks.Contains(p.BlockIndex)
            }).ToArray()),
            ["duplicates"] = new JsonArray(build.Properties.Duplicates.Select(d => (JsonNode?)new JsonObject
            {
                ["slot"] = d.SlotName,
                ["owner"] = d.OwnerBlockIndex,
                ["kept"] = d.KeptBlockIndex,
                ["ignored"] = d.IgnoredBlockIndex
            }).ToArray()),
            ["highestUvSet"] = build.HighestUvSet
        };
        foreach (var (name, value) in build.Facts.ToArray())
        {
            build.Facts.Remove(name);
            payload[name] = value;
        }

        payload["layers"] = build.LayerFacts.DeepClone();
        payload["omittedLayers"] = build.OmittedLayers.DeepClone();
        payload["assumptions"] = new JsonArray(build.Assumptions.Select(a => (JsonNode?)a).ToArray());
        return payload;
    }

    private void Undefined(NifDecodedBlock block, string field, long value,
        string consequence = "nothing is typed from it")
    {
        _diagnostics.Add(UndefinedValueDiagnostic, string.Create(CultureInfo.InvariantCulture,
            $"Block {block.Index} ({block.Type}) stores {field} {value}, which nif.xml does not define; the raw value " +
            $"stays in native state and {consequence}."));
    }

    private Vector3? Finite(Vector3? value, NifDecodedBlock block, string field)
    {
        if (value is not { } color)
        {
            return null;
        }

        if (IsFinite(color))
        {
            return color;
        }

        NonFiniteOptional(block, field);
        return null;
    }

    private float? Finite(float value, NifDecodedBlock block, string field)
    {
        if (float.IsFinite(value))
        {
            return value;
        }

        NonFiniteOptional(block, field);
        return null;
    }

    private void NonFiniteOptional(NifDecodedBlock block, string field)
    {
        _diagnostics.Add(NonFiniteValueDiagnostic, string.Create(CultureInfo.InvariantCulture,
            $"Block {block.Index} ({block.Type}) stores a non-finite {field}; it is left undeclared in the material " +
            $"and its bits stay in native state."));
    }

    private void MarkNativeOnly(int block, string reason)
    {
        if (!_dispositions.TryGetValue(block, out var existing) || !existing.IsTyped)
        {
            _dispositions[block] = NifModelBlockDisposition.NativeOnly(reason);
        }
    }

    private static JsonObject Untyped(NifDecodedBlock block, string reason)
    {
        return new JsonObject
        {
            ["block"] = block.Index,
            ["type"] = block.Type,
            ["typed"] = false,
            ["reason"] = reason
        };
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}

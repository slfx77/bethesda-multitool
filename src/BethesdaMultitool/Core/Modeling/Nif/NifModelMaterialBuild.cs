using System.Numerics;
using System.Text.Json.Nodes;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The working state of one material while <see cref="NifModelMaterialReader" /> applies its effective properties:
///     the exact source values and layers for <see cref="SceneMaterialSource" />, the render state, and the native facts
///     and assumptions its <c>bmt.nif.material</c> row records. Lives only while the material is built.
/// </summary>
internal sealed class NifModelMaterialBuild
{
    /// <summary>Starts a material for one effective property set.</summary>
    /// <param name="properties">The effective property set.</param>
    /// <param name="highestUvSet">The highest texture-coordinate set the placed geometry stores (0 for one or none).</param>
    public NifModelMaterialBuild(NifModelPropertySet properties, int highestUvSet)
    {
        Properties = properties;
        HighestUvSet = highestUvSet;
    }

    /// <summary>The effective property set.</summary>
    public NifModelPropertySet Properties { get; }

    /// <summary>The highest texture-coordinate set a layer may bind on the placed geometry.</summary>
    public int HighestUvSet { get; }

    /// <summary>The material label: the first named material, shade or texturing property.</summary>
    public string? Name { get; set; }

    /// <summary>The exact source RGBA factor.</summary>
    public Vector4 BaseColor { get; set; } = Vector4.One;

    /// <summary>The unlit declaration (BSShaderNoLightingProperty).</summary>
    public bool Unlit { get; set; }

    /// <summary>The ambient color (below BS 26).</summary>
    public Vector3? AmbientColor { get; set; }

    /// <summary>The specular color.</summary>
    public Vector3? SpecularColor { get; set; }

    /// <summary>The glossiness.</summary>
    public float? Glossiness { get; set; }

    /// <summary>The emissive color, never multiplied.</summary>
    public Vector3? EmissiveColor { get; set; }

    /// <summary>The separate emissive multiplier (above BS 21).</summary>
    public float? EmissiveMultiplier { get; set; }

    /// <summary>The declared normal-map green convention.</summary>
    public SceneNormalGreenConvention? NormalGreen { get; set; }

    /// <summary>The ordered layers.</summary>
    public List<SceneTextureLayer> Layers { get; } = [];

    /// <summary>
    ///     Where each layer of <see cref="Layers" /> came from, one entry per layer in the same order (slice 14: the
    ///     texture-slot to layer-ordinal map property tracks need).
    /// </summary>
    public List<NifModelLayerOrigin> LayerOrigins { get; } = [];

    /// <summary>The blend state.</summary>
    public SceneBlendState? Blend { get; set; }

    /// <summary>The alpha test.</summary>
    public SceneAlphaTest? AlphaTest { get; set; }

    /// <summary>The draw order.</summary>
    public SceneDrawOrder? DrawOrder { get; set; }

    /// <summary>The depth state.</summary>
    public SceneDepthState? Depth { get; set; }

    /// <summary>The stencil state.</summary>
    public SceneStencilState? Stencil { get; set; }

    /// <summary>The vertex-color use.</summary>
    public SceneVertexColorUse? VertexColorUse { get; set; }

    /// <summary>The view-angle opacity ramp.</summary>
    public SceneViewAngleOpacity? ViewAngleOpacity { get; set; }

    /// <summary>The blocks this material typed (properties, texture sets).</summary>
    public HashSet<int> TypedBlocks { get; } = [];

    /// <summary>The assumptions this material relies on, in the order they were made.</summary>
    public List<string> Assumptions { get; } = [];

    /// <summary>The per-property native facts.</summary>
    public JsonObject Facts { get; } = new();

    /// <summary>One entry per layer, in layer order.</summary>
    public JsonArray LayerFacts { get; } = new();

    /// <summary>Layers the source declares that this material could not bind (with the reason).</summary>
    public JsonArray OmittedLayers { get; } = new();

    /// <summary>True when any render-state member was declared.</summary>
    public bool HasRenderState => Blend is not null || AlphaTest is not null || DrawOrder is not null ||
                                  Depth is not null || Stencil is not null || VertexColorUse is not null ||
                                  ViewAngleOpacity is not null;

    /// <summary>Records an assumption once.</summary>
    public void Assume(string assumption)
    {
        if (!Assumptions.Contains(assumption, StringComparer.Ordinal))
        {
            Assumptions.Add(assumption);
        }
    }
}

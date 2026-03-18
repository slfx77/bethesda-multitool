namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     One geometry block's renderable data with transforms already applied.
/// </summary>
internal sealed class RenderableSubmesh
{
    /// <summary>Name of the source NiTriShape/NiTriStrips block, if available.</summary>
    public string? ShapeName { get; init; }

    /// <summary>X, Y, Z per vertex (length = numVertices * 3).</summary>
    public required float[] Positions { get; init; }

    /// <summary>3 indices per triangle (length = numTriangles * 3).</summary>
    public required ushort[] Triangles { get; init; }

    /// <summary>X, Y, Z per vertex (optional, for shading). Same length as Positions.</summary>
    public float[]? Normals { get; init; }

    /// <summary>U, V per vertex (optional, for texture mapping). Length = numVertices * 2.</summary>
    public float[]? UVs { get; init; }

    /// <summary>R, G, B, A per vertex (optional). Length = numVertices * 4.</summary>
    public byte[]? VertexColors { get; init; }

    /// <summary>Tangent X, Y, Z per vertex (optional, for bump mapping). Same length as Positions.</summary>
    public float[]? Tangents { get; init; }

    /// <summary>Bitangent X, Y, Z per vertex (optional, for bump mapping). Same length as Positions.</summary>
    public float[]? Bitangents { get; init; }

    /// <summary>Diffuse texture path resolved from shader properties (e.g., "textures\architecture\foo.dds").</summary>
    public string? DiffuseTexturePath { get; set; }

    /// <summary>Normal map texture path resolved from shader properties (slot 1).</summary>
    public string? NormalMapTexturePath { get; init; }

    /// <summary>Shader property metadata resolved from the source NIF.</summary>
    public NifShaderTextureMetadata? ShaderMetadata { get; init; }

    /// <summary>True if this submesh uses BSShaderNoLightingProperty (self-illuminated, e.g., neon signs).</summary>
    public bool IsEmissive { get; init; }

    /// <summary>True if BSShaderFlags2 bit 5 (Vertex_Colors) is set, meaning vertex colors should modulate the texture.</summary>
    public bool UseVertexColors { get; init; }

    /// <summary>True if NiStencilProperty DrawMode is DRAW_BOTH (3), meaning both sides should be rendered.</summary>
    public bool IsDoubleSided { get; init; }

    /// <summary>True if NiAlphaProperty flags bit 0 is set (alpha blending enabled).</summary>
    public bool HasAlphaBlend { get; set; }

    /// <summary>True if NiAlphaProperty flags bit 9 is set (alpha testing enabled).</summary>
    public bool HasAlphaTest { get; set; }

    /// <summary>Alpha test threshold (0-255). Pixels with alpha &lt;= this value are discarded. Default 128 per NIF spec.</summary>
    public byte AlphaTestThreshold { get; set; } = 128;

    /// <summary>
    ///     Alpha test comparison function from NiAlphaProperty bits 10-12.
    ///     0=ALWAYS, 1=LESS, 2=EQUAL, 3=LEQUAL, 4=GREATER, 5=NOTEQUAL, 6=GEQUAL, 7=NEVER.
    ///     Default 4 (GREATER) matches the standard renderer semantics (pass if a &gt; threshold).
    /// </summary>
    public byte AlphaTestFunction { get; set; } = 4;

    /// <summary>Source blend factor from NiAlphaProperty bits 1-4 (default 6 = SRC_ALPHA).</summary>
    public byte SrcBlendMode { get; set; } = 6;

    /// <summary>Dest blend factor from NiAlphaProperty bits 5-8 (default 7 = INV_SRC_ALPHA).</summary>
    public byte DstBlendMode { get; set; } = 7;

    /// <summary>Material alpha from NiMaterialProperty (0.0-1.0). Values &lt; 1.0 trigger alpha blending.</summary>
    public float MaterialAlpha { get; set; } = 1f;

    /// <summary>Material glossiness from NiMaterialProperty. Fallout 3 / New Vegas commonly default this to 10.</summary>
    public float MaterialGlossiness { get; init; } = 10f;

    /// <summary>True if BSShaderFlags bit 17 (Eye_Environment_Mapping = 0x20000) is set.</summary>
    public bool IsEyeEnvmap { get; init; }

    /// <summary>BSShaderProperty EnvMapScale — controls eye cubemap reflection strength. Typical 0.5-1.0.</summary>
    public float EnvMapScale { get; init; }

    /// <summary>
    ///     Render order for layer-based compositing (engine renders head parts in scene graph order).
    ///     0 = head (default), 1 = hair, 2 = eyes. Higher layers render after lower layers.
    /// </summary>
    public int RenderOrder { get; set; }

    /// <summary>
    ///     Multiplicative tint color (R, G, B in 0-1 range). Applied to texture color during rasterization.
    ///     Used for hair color tinting (engine applies HCLR as shader uniform on hair/eyebrow/beard submeshes).
    ///     Null = no tint (1.0 multiplier).
    /// </summary>
    public (float R, float G, float B)? TintColor { get; set; }

    /// <summary>
    ///     True if this submesh uses the FaceGen skin shader (shader type 14, flag bit 10).
    ///     When set, the renderer applies a subsurface scattering approximation using
    ///     <see cref="SubsurfaceColor" /> to simulate light transmission through skin.
    /// </summary>
    public bool IsFaceGen { get; set; }

    /// <summary>
    ///     Subsurface scattering color for FaceGen skin shader (normalized 0-1 RGB).
    ///     Derived from the BSShaderTextureSet slot 2 face tint texture (_sk).
    ///     The engine multiplies this by a scatter intensity to add warm red backlighting
    ///     that counteracts green casts from EGT texture morphs.
    ///     Only used when <see cref="IsFaceGen" /> is true. Default = (0, 0, 0) = no scatter.
    /// </summary>
    public (float R, float G, float B) SubsurfaceColor { get; set; }

    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Triangles.Length / 3;
}

using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

internal struct TriangleData
{
    // Vertex positions
    public float X0, Y0, Z0;
    public float X1, Y1, Z1;
    public float X2, Y2, Z2;
    public float AvgZ;

    // Per-vertex normals (for smooth shading)
    public float Nx0, Ny0, Nz0;
    public float Nx1, Ny1, Nz1;
    public float Nx2, Ny2, Nz2;
    public bool HasVertexNormals;

    // Per-vertex tangents (for bump mapping)
    public float Tx0, Ty0, Tz0;
    public float Tx1, Ty1, Tz1;
    public float Tx2, Ty2, Tz2;
    public float Bx0, By0, Bz0;
    public float Bx1, By1, Bz1;
    public float Bx2, By2, Bz2;
    public bool HasTangents;

    // Per-vertex colors (RGBA, 0-255)
    public float R0, G0, B0, A0;
    public float R1, G1, B1, A1;
    public float R2, G2, B2, A2;
    public bool HasVertexColors;

    // Flat face normal shade (fallback when no vertex normals)
    public float FlatShade;

    // UV coordinates per vertex
    public float U0, V0, U1, V1, U2, V2;

    // Textures
    public DecodedTexture? Texture;
    public DecodedTexture? NormalMap;

    // Emissive (self-illuminated, no lighting applied)
    public bool IsEmissive;

    // Double-sided (NiStencilProperty DRAW_BOTH: flip normals instead of culling)
    public bool IsDoubleSided;

    // NiAlphaProperty: per-mesh blend/test control
    public bool HasAlphaBlend;
    public bool HasAlphaTest;
    public byte AlphaTestThreshold;
    public byte AlphaTestFunction; // 0=ALWAYS..4=GREATER..7=NEVER
    public byte SrcBlendMode; // 0=ONE, 1=ZERO, 6=SRC_ALPHA, 7=INV_SRC_ALPHA, etc.
    public byte DstBlendMode;
    public float MaterialAlpha; // From NiMaterialProperty, 1.0 = opaque
    public NifAlphaRenderMode AlphaRenderMode;

    // Eye environment map (SLS2057.pso cubemap reflection approximation)
    public bool IsEyeEnvmap;
    public float EnvMapScale;

    // Layer-based render order (engine renders head parts in scene graph order)
    public int RenderOrder;

    // Hair tint from HCLR: SM3002.pso formula 2*(vc*(tint-0.5)+0.5) * accDiffuse * tex
    public bool HasTintColor;
    public float TintR, TintG, TintB;
}

/// <summary>
///     Result of rendering a NIF model to a sprite.
/// </summary>
internal sealed class SpriteResult
{
    /// <summary>RGBA pixel data (length = Width * Height * 4).</summary>
    public required byte[] Pixels { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Original model width in game units.</summary>
    public required float BoundsWidth { get; init; }

    /// <summary>Original model height in game units.</summary>
    public required float BoundsHeight { get; init; }

    /// <summary>Whether at least one submesh was texture-mapped.</summary>
    public bool HasTexture { get; init; }
}

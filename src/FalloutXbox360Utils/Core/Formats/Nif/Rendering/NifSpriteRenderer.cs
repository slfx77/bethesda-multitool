using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Renders a <see cref="NifRenderableModel"/> to a transparent RGBA pixel buffer
///     using orthographic top-down projection with per-vertex smooth shading, optional texture mapping,
///     bump mapping, and vertex colors.
///     Uses a scanline triangle rasterizer with Z-buffer depth testing.
/// </summary>
internal static class NifSpriteRenderer
{
    /// <summary>SSAA supersample factor: render at Nx resolution, then box-filter downscale.</summary>
    private const int SsaaFactor = 2;

    // Debug flags for diagnosing rendering artifacts
    internal static bool DisableBilinear { get; set; }
    internal static bool DisableBumpMapping { get; set; }
    internal static bool DisableTextures { get; set; }

    /// <summary>
    ///     Normal map bump strength (0 = flat, 1 = full).  The game's multi-light
    ///     environment naturally softens bump detail; our single key light exaggerates
    ///     it.  Default 0.5 compensates for the missing fill lights.
    /// </summary>
    internal static float BumpStrength { get; set; } = 0.5f;

    // Lighting: SKIN2000.pso-accurate formula from D3D9 bytecode disassembly.
    // Hemisphere ambient simulates sky/ground bounce light — normals facing up get
    // more ambient than normals facing down.
    private const float SkyAmbient = 0.50f;
    private const float GroundAmbient = 0.30f;

    // PSLightColor intensity — the game sets this per-light from the cell/weather system.
    // We use a single white key light; this scales the directional contribution.
    private const float LightIntensity = 0.65f;

    // SM3002.pso hair tint shader constant: the "lightScalar" in the tint formula is
    // a hardcoded -0.5 (from def c6 = {-0.5, 0, -1, -2}, register never overwritten).
    // Formula: tintedShade = 2 * (vc * (HairTint - 0.5) + 0.5)
    // With vc=1 this simplifies to 2 * HairTint, so dark tints darken and light tints brighten.

    // Light direction: mostly top-down with slight angle for depth cues
    private static readonly float LightDirX = Normalize(0.3f, 0.2f, 1.0f).x;
    private static readonly float LightDirY = Normalize(0.3f, 0.2f, 1.0f).y;
    private static readonly float LightDirZ = Normalize(0.3f, 0.2f, 1.0f).z;

    // Half vector: normalize(lightDir + viewDir), where viewDir = (0, 0, 1) for top-down orthographic.
    // Used for NdotH in the SKIN2000 Fresnel term.
    private static readonly float HalfVecX = Normalize(LightDirX, LightDirY, LightDirZ + 1f).x;
    private static readonly float HalfVecY = Normalize(LightDirX, LightDirY, LightDirZ + 1f).y;
    private static readonly float HalfVecZ = Normalize(LightDirX, LightDirY, LightDirZ + 1f).z;

    // Precomputed: dot(halfVec, -lightDir) — constant for the Fresnel rim light term.
    // SKIN2000.pso: fresnel = dot(H, -L) * (1 - NdotH)^2
    private static readonly float HdotNegL = -(HalfVecX * LightDirX + HalfVecY * LightDirY + HalfVecZ * LightDirZ);

    private static (float x, float y, float z) Normalize(float x, float y, float z)
    {
        var len = MathF.Sqrt(x * x + y * y + z * z);
        return (x / len, y / len, z / len);
    }

    public static SpriteResult? Render(NifRenderableModel model,
        NifTextureResolver? textureResolver = null,
        float pixelsPerUnit = 1.0f, int minSize = 32, int maxSize = 1024,
        int? fixedSize = null)
    {
        if (!model.HasGeometry)
        {
            return null;
        }

        var hasTexture = false;
        var triangleList = CollectTriangles(model, textureResolver, ref hasTexture);

        return RenderCore(triangleList, hasTexture,
            model.MinX, model.MinY, model.Width, model.Height,
            pixelsPerUnit, minSize, maxSize, fixedSize);
    }

    /// <summary>
    ///     Render from a specific camera angle defined by azimuth and elevation.
    ///     Produces an isometric-style orthographic view from the given direction.
    /// </summary>
    /// <param name="azimuthDeg">Camera azimuth in degrees (0=S, 45=NE, 90=E, 135=NW, etc.)</param>
    /// <param name="elevationDeg">Camera elevation in degrees above the horizontal plane (e.g., 30).</param>
    public static SpriteResult? Render(NifRenderableModel model,
        NifTextureResolver? textureResolver,
        float pixelsPerUnit, int minSize, int maxSize,
        float azimuthDeg, float elevationDeg,
        int? fixedSize = null)
    {
        if (!model.HasGeometry)
        {
            return null;
        }

        var hasTexture = false;
        var triangleList = CollectTriangles(model, textureResolver, ref hasTexture);

        // Apply view rotation to transform world space into view space
        var (minX, minY, projWidth, projHeight) =
            ApplyViewRotation(triangleList, azimuthDeg, elevationDeg);

        return RenderCore(triangleList, hasTexture,
            minX, minY, projWidth, projHeight,
            pixelsPerUnit, minSize, maxSize, fixedSize);
    }

    /// <summary>
    ///     Core render pipeline: canvas sizing → sort → rasterize → downsample.
    ///     Shared by both top-down and isometric render paths.
    /// </summary>
    private static SpriteResult? RenderCore(List<TriangleData> triangleList, bool hasTexture,
        float minX, float minY, float modelWidth, float modelHeight,
        float pixelsPerUnit, int minSize, int maxSize, int? fixedSize = null)
    {
        if (modelWidth < 0.001f && modelHeight < 0.001f)
        {
            return null;
        }

        int width, height;

        if (fixedSize.HasValue)
        {
            // Fixed size mode: longest edge = fixedSize, preserve aspect ratio
            var aspect = modelWidth / Math.Max(modelHeight, 0.001f);
            if (aspect >= 1f)
            {
                width = fixedSize.Value;
                height = Math.Clamp((int)(fixedSize.Value / aspect), 1, fixedSize.Value);
            }
            else
            {
                height = fixedSize.Value;
                width = Math.Clamp((int)(fixedSize.Value * aspect), 1, fixedSize.Value);
            }
        }
        else
        {
            // Proportional mode: scale by PPU with min/max clamp
            var rawWidth = (int)MathF.Ceiling(modelWidth * pixelsPerUnit) + 2; // +2 for margin
            var rawHeight = (int)MathF.Ceiling(modelHeight * pixelsPerUnit) + 2;

            var scale = 1.0f;
            var maxDim = Math.Max(rawWidth, rawHeight);

            if (maxDim > maxSize)
            {
                scale = (float)maxSize / maxDim;
            }
            else if (maxDim < minSize)
            {
                scale = (float)minSize / maxDim;
            }

            width = Math.Clamp((int)(rawWidth * scale), 1, maxSize);
            height = Math.Clamp((int)(rawHeight * scale), 1, maxSize);
        }

        // Render at supersampled resolution for antialiasing
        var ssWidth = width * SsaaFactor;
        var ssHeight = height * SsaaFactor;

        // Effective pixels-per-unit after clamping (at supersampled resolution)
        var effPpuX = (ssWidth - 2 * SsaaFactor) / Math.Max(modelWidth, 0.001f);
        var effPpuY = (ssHeight - 2 * SsaaFactor) / Math.Max(modelHeight, 0.001f);
        var effPpu = MathF.Min(effPpuX, effPpuY);

        // Center the model in the supersampled output
        var offsetX = (ssWidth - modelWidth * effPpu) / 2f - minX * effPpu;
        var offsetY = (ssHeight - modelHeight * effPpu) / 2f - minY * effPpu;

        // Allocate supersampled pixel buffer (RGBA), depth buffer, emissive mask,
        // and face-orientation buffer (0=unwritten, 1=front-face, 2=back-face).
        // The face-orientation buffer lets front-facing fragments always override
        // back-facing ones at the same depth — eliminates Z-fighting on double-sided
        // thin shells (dresses, capes, flags) without manual bias constants.
        var ssPixels = new byte[ssWidth * ssHeight * 4];
        var depthBuffer = new float[ssWidth * ssHeight];
        var faceKind = new byte[ssWidth * ssHeight];
        var emissiveMask = new bool[ssWidth * ssHeight];
        Array.Fill(depthBuffer, float.MinValue);

        // Sort by render order (head first, then hair, then eyes) so the engine's
        // scene-graph rendering order is preserved. Within each layer, sort by
        // average Z ascending (back-to-front) for correct depth compositing.
        triangleList.Sort((a, b) =>
        {
            var layer = a.RenderOrder.CompareTo(b.RenderOrder);
            return layer != 0 ? layer : a.AvgZ.CompareTo(b.AvgZ);
        });

        // Band-parallel rasterization: divide the framebuffer into horizontal bands,
        // each processed by a separate thread. Since bands own exclusive rows, no
        // synchronization is needed — each pixel (px, py) belongs to exactly one band.
        // Triangle ordering is preserved within each band for correct alpha blending.
        var bandCount = Math.Max(1, Environment.ProcessorCount);
        Parallel.For(0, bandCount, bandIdx =>
        {
            var bMinY = bandIdx * ssHeight / bandCount;
            var bMaxY = (bandIdx + 1) * ssHeight / bandCount - 1;
            foreach (var tri in triangleList)
            {
                RasterizeTriangle(ssPixels, depthBuffer, faceKind, emissiveMask, ssWidth,
                    tri, effPpu, offsetX, offsetY, bMinY, bMaxY);
            }
        });

        // Apply bloom glow around emissive pixels
        ApplyBloom(ssPixels, emissiveMask, ssWidth, ssHeight);

        // Downsample to final resolution with box filter
        var pixels = Downsample(ssPixels, ssWidth, ssHeight, SsaaFactor);

        return new SpriteResult
        {
            Pixels = pixels,
            Width = width,
            Height = height,
            BoundsWidth = modelWidth,
            BoundsHeight = modelHeight,
            HasTexture = hasTexture
        };
    }

    /// <summary>
    ///     Apply a view rotation to all triangle data, transforming from world space to view space.
    ///     Returns the projected 2D bounds (minX, minY, width, height) for canvas sizing.
    /// </summary>
    private static (float MinX, float MinY, float Width, float Height) ApplyViewRotation(
        List<TriangleData> triangles, float azimuthDeg, float elevationDeg)
    {
        var alpha = azimuthDeg * MathF.PI / 180f;
        var theta = elevationDeg * MathF.PI / 180f;

        var ca = MathF.Cos(alpha);
        var sa = MathF.Sin(alpha);
        var ct = MathF.Cos(theta);
        var st = MathF.Sin(theta);

        // View basis vectors (rows of the rotation matrix)
        // right       = (-sin(α), cos(α), 0)              — negated for right-handed screen coords
        // screen_down = (sin(θ)·cos(α), sin(θ)·sin(α), -cos(θ))  — negated so world +Z maps to screen up
        // toward_cam  = (cos(α)·cos(θ), sin(α)·cos(θ), sin(θ))   — points toward camera so closer = higher Z
        float r0 = -sa, r1 = ca, r2 = 0;
        float u0 = st * ca, u1 = st * sa, u2 = -ct;
        float f0 = ca * ct, f1 = sa * ct, f2 = st;

        var projMinX = float.MaxValue;
        var projMinY = float.MaxValue;
        var projMaxX = float.MinValue;
        var projMaxY = float.MinValue;

        for (var i = 0; i < triangles.Count; i++)
        {
            var tri = triangles[i];

            // Rotate vertex positions
            RotatePoint(ref tri.X0, ref tri.Y0, ref tri.Z0, r0, r1, r2, u0, u1, u2, f0, f1, f2);
            RotatePoint(ref tri.X1, ref tri.Y1, ref tri.Z1, r0, r1, r2, u0, u1, u2, f0, f1, f2);
            RotatePoint(ref tri.X2, ref tri.Y2, ref tri.Z2, r0, r1, r2, u0, u1, u2, f0, f1, f2);
            tri.AvgZ = (tri.Z0 + tri.Z1 + tri.Z2) / 3f;

            // Rotate normals (direction only, same matrix)
            if (tri.HasVertexNormals)
            {
                RotatePoint(ref tri.Nx0, ref tri.Ny0, ref tri.Nz0, r0, r1, r2, u0, u1, u2, f0, f1, f2);
                RotatePoint(ref tri.Nx1, ref tri.Ny1, ref tri.Nz1, r0, r1, r2, u0, u1, u2, f0, f1, f2);
                RotatePoint(ref tri.Nx2, ref tri.Ny2, ref tri.Nz2, r0, r1, r2, u0, u1, u2, f0, f1, f2);
            }
            else
            {
                // Recompute flat shade from rotated face normal
                var ex1 = tri.X1 - tri.X0;
                var ey1 = tri.Y1 - tri.Y0;
                var ez1 = tri.Z1 - tri.Z0;
                var ex2 = tri.X2 - tri.X0;
                var ey2 = tri.Y2 - tri.Y0;
                var ez2 = tri.Z2 - tri.Z0;
                var nx = ey1 * ez2 - ez1 * ey2;
                var ny = ez1 * ex2 - ex1 * ez2;
                var nz = ex1 * ey2 - ey1 * ex2;
                var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 0.0001f)
                {
                    nx /= len;
                    ny /= len;
                    nz /= len;
                }

                tri.FlatShade = ComputeShade(nx, ny, nz, tri.IsDoubleSided);
            }

            // Rotate tangents and bitangents
            if (tri.HasTangents)
            {
                RotatePoint(ref tri.Tx0, ref tri.Ty0, ref tri.Tz0, r0, r1, r2, u0, u1, u2, f0, f1, f2);
                RotatePoint(ref tri.Tx1, ref tri.Ty1, ref tri.Tz1, r0, r1, r2, u0, u1, u2, f0, f1, f2);
                RotatePoint(ref tri.Tx2, ref tri.Ty2, ref tri.Tz2, r0, r1, r2, u0, u1, u2, f0, f1, f2);
                RotatePoint(ref tri.Bx0, ref tri.By0, ref tri.Bz0, r0, r1, r2, u0, u1, u2, f0, f1, f2);
                RotatePoint(ref tri.Bx1, ref tri.By1, ref tri.Bz1, r0, r1, r2, u0, u1, u2, f0, f1, f2);
                RotatePoint(ref tri.Bx2, ref tri.By2, ref tri.Bz2, r0, r1, r2, u0, u1, u2, f0, f1, f2);
            }

            triangles[i] = tri;

            // Track projected 2D bounds
            UpdateBounds(tri.X0, tri.Y0, ref projMinX, ref projMinY, ref projMaxX, ref projMaxY);
            UpdateBounds(tri.X1, tri.Y1, ref projMinX, ref projMinY, ref projMaxX, ref projMaxY);
            UpdateBounds(tri.X2, tri.Y2, ref projMinX, ref projMinY, ref projMaxX, ref projMaxY);
        }

        return (projMinX, projMinY, projMaxX - projMinX, projMaxY - projMinY);

        static void UpdateBounds(float x, float y,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
    }

    /// <summary>
    ///     Apply a 3x3 rotation matrix (given as 3 row vectors) to a point in-place.
    /// </summary>
    private static void RotatePoint(ref float x, ref float y, ref float z,
        float r0, float r1, float r2,
        float u0, float u1, float u2,
        float f0, float f1, float f2)
    {
        var ox = x;
        var oy = y;
        var oz = z;
        x = r0 * ox + r1 * oy + r2 * oz;
        y = u0 * ox + u1 * oy + u2 * oz;
        z = f0 * ox + f1 * oy + f2 * oz;
    }

    private static List<TriangleData> CollectTriangles(NifRenderableModel model,
        NifTextureResolver? textureResolver, ref bool hasTexture)
    {
        var list = new List<TriangleData>();

        foreach (var submesh in model.Submeshes)
        {
            var pos = submesh.Positions;
            var nrm = submesh.Normals;
            var tris = submesh.Triangles;
            var uvs = submesh.UVs;
            var vcol = submesh.VertexColors;
            var tan = submesh.Tangents;
            var bitan = submesh.Bitangents;

            // Resolve textures for this submesh (if available)
            DecodedTexture? texture = null;
            DecodedTexture? normalMap = null;
            if (textureResolver != null && uvs != null && submesh.DiffuseTexturePath != null)
            {
                texture = textureResolver.GetTexture(submesh.DiffuseTexturePath);
                if (texture != null)
                {
                    hasTexture = true;
                }
            }

            if (textureResolver != null && uvs != null && submesh.NormalMapTexturePath != null)
            {
                normalMap = textureResolver.GetTexture(submesh.NormalMapTexturePath);
            }

            // Skip untextured submeshes when texture rendering is active — but only
            // those that never had a texture path assigned (night-glow overlays, etc.).
            // Submeshes whose texture path was set but failed to load still render with
            // flat shading so the geometry remains visible (e.g., cut NPCs with missing textures).
            // Vertex-colored submeshes render even without texture (e.g., hair with HCLR tint).
            if (textureResolver != null && texture == null &&
                submesh.DiffuseTexturePath == null &&
                !(submesh.UseVertexColors && submesh.VertexColors != null))
            {
                continue;
            }

            for (var t = 0; t < tris.Length; t += 3)
            {
                var i0 = tris[t] * 3;
                var i1 = tris[t + 1] * 3;
                var i2 = tris[t + 2] * 3;

                // Bounds check
                if (i0 + 2 >= pos.Length || i1 + 2 >= pos.Length || i2 + 2 >= pos.Length)
                {
                    continue;
                }

                var tri = new TriangleData
                {
                    X0 = pos[i0], Y0 = pos[i0 + 1], Z0 = pos[i0 + 2],
                    X1 = pos[i1], Y1 = pos[i1 + 1], Z1 = pos[i1 + 2],
                    X2 = pos[i2], Y2 = pos[i2 + 1], Z2 = pos[i2 + 2],
                    AvgZ = (pos[i0 + 2] + pos[i1 + 2] + pos[i2 + 2]) / 3f,
                    IsEmissive = submesh.IsEmissive,
                    IsDoubleSided = submesh.IsDoubleSided,
                    HasAlphaBlend = submesh.HasAlphaBlend,
                    HasAlphaTest = submesh.HasAlphaTest,
                    AlphaTestThreshold = submesh.AlphaTestThreshold,
                    AlphaTestFunction = submesh.AlphaTestFunction,
                    SrcBlendMode = submesh.SrcBlendMode,
                    DstBlendMode = submesh.DstBlendMode,
                    MaterialAlpha = submesh.MaterialAlpha,
                    IsEyeEnvmap = submesh.IsEyeEnvmap,
                    EnvMapScale = submesh.EnvMapScale,
                    RenderOrder = submesh.RenderOrder,
                    HasTintColor = submesh.TintColor.HasValue,
                    TintR = submesh.TintColor?.R ?? 1f,
                    TintG = submesh.TintColor?.G ?? 1f,
                    TintB = submesh.TintColor?.B ?? 1f
                };

                // Populate UV data if texture is available
                if (texture != null && uvs != null)
                {
                    var uv0 = tris[t] * 2;
                    var uv1 = tris[t + 1] * 2;
                    var uv2 = tris[t + 2] * 2;

                    if (uv0 + 1 < uvs.Length && uv1 + 1 < uvs.Length && uv2 + 1 < uvs.Length)
                    {
                        tri.U0 = uvs[uv0]; tri.V0 = uvs[uv0 + 1];
                        tri.U1 = uvs[uv1]; tri.V1 = uvs[uv1 + 1];
                        tri.U2 = uvs[uv2]; tri.V2 = uvs[uv2 + 1];
                        tri.Texture = texture;
                        tri.NormalMap = normalMap;
                    }
                }

                // Per-vertex normals for smooth shading
                var hasVertexNormals = false;
                if (nrm != null && i0 + 2 < nrm.Length && i1 + 2 < nrm.Length && i2 + 2 < nrm.Length)
                {
                    tri.Nx0 = nrm[i0]; tri.Ny0 = nrm[i0 + 1]; tri.Nz0 = nrm[i0 + 2];
                    tri.Nx1 = nrm[i1]; tri.Ny1 = nrm[i1 + 1]; tri.Nz1 = nrm[i1 + 2];
                    tri.Nx2 = nrm[i2]; tri.Ny2 = nrm[i2 + 1]; tri.Nz2 = nrm[i2 + 2];
                    tri.HasVertexNormals = true;
                    hasVertexNormals = true;
                }

                // Per-vertex tangents/bitangents for bump mapping
                if (hasVertexNormals && tan != null && bitan != null &&
                    i0 + 2 < tan.Length && i1 + 2 < tan.Length && i2 + 2 < tan.Length &&
                    i0 + 2 < bitan.Length && i1 + 2 < bitan.Length && i2 + 2 < bitan.Length)
                {
                    tri.Tx0 = tan[i0]; tri.Ty0 = tan[i0 + 1]; tri.Tz0 = tan[i0 + 2];
                    tri.Tx1 = tan[i1]; tri.Ty1 = tan[i1 + 1]; tri.Tz1 = tan[i1 + 2];
                    tri.Tx2 = tan[i2]; tri.Ty2 = tan[i2 + 1]; tri.Tz2 = tan[i2 + 2];
                    tri.Bx0 = bitan[i0]; tri.By0 = bitan[i0 + 1]; tri.Bz0 = bitan[i0 + 2];
                    tri.Bx1 = bitan[i1]; tri.By1 = bitan[i1 + 1]; tri.Bz1 = bitan[i1 + 2];
                    tri.Bx2 = bitan[i2]; tri.By2 = bitan[i2 + 1]; tri.Bz2 = bitan[i2 + 2];
                    tri.HasTangents = true;
                }

                // Per-vertex colors: gated by BSShaderFlags2 Vertex_Colors bit for lit shaders,
                // but always applied for emissive submeshes (neon signs use vertex colors for glow tint)
                if (vcol != null && (submesh.UseVertexColors || submesh.IsEmissive))
                {
                    var ci0 = tris[t] * 4;
                    var ci1 = tris[t + 1] * 4;
                    var ci2 = tris[t + 2] * 4;
                    if (ci0 + 3 < vcol.Length && ci1 + 3 < vcol.Length && ci2 + 3 < vcol.Length)
                    {
                        tri.R0 = vcol[ci0]; tri.G0 = vcol[ci0 + 1]; tri.B0 = vcol[ci0 + 2]; tri.A0 = vcol[ci0 + 3];
                        tri.R1 = vcol[ci1]; tri.G1 = vcol[ci1 + 1]; tri.B1 = vcol[ci1 + 2]; tri.A1 = vcol[ci1 + 3];
                        tri.R2 = vcol[ci2]; tri.G2 = vcol[ci2 + 1]; tri.B2 = vcol[ci2 + 2]; tri.A2 = vcol[ci2 + 3];
                        tri.HasVertexColors = true;
                    }
                }

                // Compute flat face normal fallback shade
                if (!hasVertexNormals)
                {
                    var ex1 = tri.X1 - tri.X0;
                    var ey1 = tri.Y1 - tri.Y0;
                    var ez1 = tri.Z1 - tri.Z0;
                    var ex2 = tri.X2 - tri.X0;
                    var ey2 = tri.Y2 - tri.Y0;
                    var ez2 = tri.Z2 - tri.Z0;

                    var nx = ey1 * ez2 - ez1 * ey2;
                    var ny = ez1 * ex2 - ex1 * ez2;
                    var nz = ex1 * ey2 - ey1 * ex2;
                    var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);

                    if (len > 0.0001f)
                    {
                        nx /= len;
                        ny /= len;
                        nz /= len;
                    }

                    tri.FlatShade = ComputeShade(nx, ny, nz, tri.IsDoubleSided);
                }

                list.Add(tri);
            }
        }

        return list;
    }

    /// <summary>
    ///     Compute shading from a world-space normal using the SKIN2000.pso formula
    ///     (from D3D9 bytecode disassembly of Bethesda's face/skin pixel shader).
    ///     <para>
    ///     SKIN2000 lighting:
    ///       fresnel = dot(H, -L) * (1 - NdotH)^2
    ///       directional = min(PSLightColor * NdotL + PSLightColor * fresnel * 0.5, 1.0)
    ///       shade = directional + AmbientColor
    ///     </para>
    /// </summary>
    private static float ComputeShade(float nx, float ny, float nz, bool twoSidedLighting = false)
    {
        // Hemisphere ambient: blend between ground and sky based on normal Y
        // Negated: view-space Y points down, so -ny maps "up" normals to sky
        var hemiBlend = -ny * 0.5f + 0.5f;
        var ambient = GroundAmbient + (SkyAmbient - GroundAmbient) * hemiBlend;

        // NdotL — diffuse with wrap lighting to soften terminator line on FaceGen creases.
        // Wrap factor 0.25: allows normals slightly facing away from light to receive partial
        // illumination, preventing the harsh dark line at the lit/unlit boundary.
        const float wrap = 0.25f;
        var rawNdotL = nx * LightDirX + ny * LightDirY + nz * LightDirZ;
        // Two-sided lighting: thin surfaces (skirts, flags) are lit from both sides
        if (twoSidedLighting)
            rawNdotL = MathF.Abs(rawNdotL);
        var NdotL = MathF.Max(0, (rawNdotL + wrap) / (1f + wrap));

        // NdotH — for Fresnel rim light (not Blinn-Phong specular)
        var NdotH = MathF.Max(0, nx * HalfVecX + ny * HalfVecY + nz * HalfVecZ);

        // SKIN2000 Fresnel: (1 - NdotH)^2 * dot(halfVec, -lightDir)
        // This creates a rim-light effect at grazing angles rather than a specular hotspot.
        var oneMinusNdotH = 1f - NdotH;
        var fresnel = MathF.Max(0, HdotNegL) * oneMinusNdotH * oneMinusNdotH;

        // SKIN2000: min(lightColor * NdotL + lightColor * fresnel * 0.5, 1.0) + ambient
        var directional = MathF.Min(LightIntensity * NdotL + LightIntensity * fresnel * 0.5f, 1f);

        return Math.Clamp(directional + ambient, 0f, 1f);
    }

    /// <summary>
    ///     Rasterize a single filled triangle using scanline algorithm with per-pixel Z-buffer.
    ///     Supports per-vertex normal interpolation, texture mapping, bump mapping, and vertex colors.
    /// </summary>
    private static void RasterizeTriangle(byte[] pixels, float[] depthBuffer, byte[] faceKind,
        bool[] emissiveMask,
        int width, TriangleData tri, float ppu, float offsetX, float offsetY,
        int bandMinY, int bandMaxY)
    {
        // Project to screen coordinates (top-down: X→screenX, Y→screenY)
        var sx0 = tri.X0 * ppu + offsetX;
        var sy0 = tri.Y0 * ppu + offsetY;
        var sx1 = tri.X1 * ppu + offsetX;
        var sy1 = tri.Y1 * ppu + offsetY;
        var sx2 = tri.X2 * ppu + offsetX;
        var sy2 = tri.Y2 * ppu + offsetY;

        // Bounding box (clipped to image)
        var minPx = Math.Max(0, (int)MathF.Floor(MathF.Min(sx0, MathF.Min(sx1, sx2))));
        var maxPx = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(sx0, MathF.Max(sx1, sx2))));
        var minPy = Math.Max(bandMinY, (int)MathF.Floor(MathF.Min(sy0, MathF.Min(sy1, sy2))));
        var maxPy = Math.Min(bandMaxY, (int)MathF.Ceiling(MathF.Max(sy0, MathF.Max(sy1, sy2))));

        if (minPx > maxPx || minPy > maxPy)
        {
            return;
        }

        // Precompute edge function denominators — sign indicates triangle winding in screen space.
        // In screen space (Y-down), front-facing CCW triangles from 3D produce denom < 0.
        var denom = (sy1 - sy2) * (sx0 - sx2) + (sx2 - sx1) * (sy0 - sy2);
        if (MathF.Abs(denom) < 0.0001f)
        {
            return; // Degenerate triangle
        }

        // Backface culling: skip back-facing triangles unless double-sided.
        // For double-sided meshes (NiStencilProperty DRAW_BOTH), we render both faces
        // and flip vertex normals on back-facing triangles so they shade correctly.
        var isBackFacing = denom > 0;
        if (isBackFacing && !tri.IsDoubleSided)
        {
            return;
        }

        var invDenom = 1f / denom;
        var tex = tri.Texture;
        var normalMap = tri.NormalMap;

        // Rasterize using barycentric coordinates
        for (var py = minPy; py <= maxPy; py++)
        {
            for (var px = minPx; px <= maxPx; px++)
            {
                var cx = px + 0.5f;
                var cy = py + 0.5f;

                // Barycentric coordinates
                var w0 = ((sy1 - sy2) * (cx - sx2) + (sx2 - sx1) * (cy - sy2)) * invDenom;
                var w1 = ((sy2 - sy0) * (cx - sx2) + (sx0 - sx2) * (cy - sy2)) * invDenom;
                var w2 = 1f - w0 - w1;

                // Inside triangle?
                if (w0 < 0 || w1 < 0 || w2 < 0)
                {
                    continue;
                }

                // Interpolate Z for depth test
                var z = tri.Z0 * w0 + tri.Z1 * w1 + tri.Z2 * w2;
                var idx = py * width + px;

                // Per-pixel front-face priority for double-sided thin shells:
                // - Front face always overrides back face (regardless of depth)
                // - Back face can never overwrite front face (prevents Z-fighting)
                // - Same orientation or non-double-sided: normal depth test
                var passesDepthTest = z > depthBuffer[idx];
                var frontOverBack = !isBackFacing && faceKind[idx] == 2;
                var backBlockedByFront = isBackFacing && faceKind[idx] == 1;

                if (backBlockedByFront || (!passesDepthTest && !frontOverBack))
                {
                    continue;
                }

                var pIdx = idx * 4;

                // Emissive surfaces (BSShaderNoLightingProperty) are self-illuminated — no shading
                float shade;

                if (tri.IsEmissive)
                {
                    shade = 1.0f;
                }
                else if (tri.HasVertexNormals)
                {
                    var nx = tri.Nx0 * w0 + tri.Nx1 * w1 + tri.Nx2 * w2;
                    var ny = tri.Ny0 * w0 + tri.Ny1 * w1 + tri.Ny2 * w2;
                    var nz = tri.Nz0 * w0 + tri.Nz1 * w1 + tri.Nz2 * w2;

                    // Flip normals for back-facing double-sided triangles so they shade correctly
                    if (isBackFacing)
                    {
                        nx = -nx;
                        ny = -ny;
                        nz = -nz;
                    }
                    var nLen = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (nLen > 0.001f)
                    {
                        nx /= nLen;
                        ny /= nLen;
                        nz /= nLen;
                    }

                    // Bump mapping: perturb normal using normal map + TBN matrix
                    if (!DisableBumpMapping && normalMap != null && tri.HasTangents && tex != null)
                    {
                        var u = tri.U0 * w0 + tri.U1 * w1 + tri.U2 * w2;
                        var v = tri.V0 * w0 + tri.V1 * w1 + tri.V2 * w2;
                        u -= MathF.Floor(u);
                        v -= MathF.Floor(v);

                        var (mnr, mng, mnb, _) = SampleTexture(normalMap, u, v);

                        // Decode from [0,255] to [-1,1]
                        // Y is negated: Bethesda normal maps use DirectX convention (Y-down)
                        // but NIF bitangent vectors point in UV V+ direction (Y-up).
                        var mapNx = (mnr / 127.5f - 1f) * BumpStrength;
                        var mapNy = -(mng / 127.5f - 1f) * BumpStrength;
                        var mapNz = mnb / 127.5f - 1f;

                        // Construct TBN matrix from per-vertex tangent/bitangent data.
                        // When available, interpolate stored NIF tangent/bitangent vectors
                        // (already rotated to view space in ApplyViewRotation).
                        // Fall back to ad-hoc derivation only when tangent data is missing.
                        float tx, ty, tz, bx, by, bz;
                        if (tri.HasTangents)
                        {
                            // Interpolate per-vertex tangent and bitangent
                            tx = tri.Tx0 * w0 + tri.Tx1 * w1 + tri.Tx2 * w2;
                            ty = tri.Ty0 * w0 + tri.Ty1 * w1 + tri.Ty2 * w2;
                            tz = tri.Tz0 * w0 + tri.Tz1 * w1 + tri.Tz2 * w2;
                            bx = tri.Bx0 * w0 + tri.Bx1 * w1 + tri.Bx2 * w2;
                            by = tri.By0 * w0 + tri.By1 * w1 + tri.By2 * w2;
                            bz = tri.Bz0 * w0 + tri.Bz1 * w1 + tri.Bz2 * w2;
                            var tLen = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
                            if (tLen > 0.001f) { tx /= tLen; ty /= tLen; tz /= tLen; }
                            var bLen = MathF.Sqrt(bx * bx + by * by + bz * bz);
                            if (bLen > 0.001f) { bx /= bLen; by /= bLen; bz /= bLen; }
                        }
                        else
                        {
                            // Fallback: derive tangent from cross product with stable axis
                            if (MathF.Abs(nx) < 0.9f)
                            {
                                tx = 0f; ty = nz; tz = -ny;
                            }
                            else
                            {
                                tx = -nz; ty = 0f; tz = nx;
                            }
                            var tLen = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
                            if (tLen > 0.001f) { tx /= tLen; ty /= tLen; tz /= tLen; }
                            bx = ny * tz - nz * ty;
                            by = nz * tx - nx * tz;
                            bz = nx * ty - ny * tx;
                        }

                        // TBN matrix transform: world = T * mapNx + B * mapNy + N * mapNz
                        var wnx = tx * mapNx + bx * mapNy + nx * mapNz;
                        var wny = ty * mapNx + by * mapNy + ny * mapNz;
                        var wnz = tz * mapNx + bz * mapNy + nz * mapNz;
                        var wnLen = MathF.Sqrt(wnx * wnx + wny * wny + wnz * wnz);
                        if (wnLen > 0.001f)
                        {
                            nx = wnx / wnLen;
                            ny = wny / wnLen;
                            nz = wnz / wnLen;
                        }
                    }

                    shade = ComputeShade(nx, ny, nz, tri.IsDoubleSided);

                    // Eye specular: approximate SLS2057.pso cubemap reflection as Blinn-Phong.
                    // The game uses EnvironmentCubeMap sampled at the reflection vector; we
                    // approximate with a focused specular highlight (shininess=16).
                    if (tri.IsEyeEnvmap)
                    {
                        var specNdotH = MathF.Max(0f, nx * HalfVecX + ny * HalfVecY + nz * HalfVecZ);
                        shade = MathF.Min(shade + MathF.Pow(specNdotH, 16f) * tri.EnvMapScale * 0.6f, 1f);
                    }
                }
                else
                {
                    shade = tri.FlatShade;
                }

                if (tex != null)
                {
                    // Texture-mapped rendering: interpolate UVs and sample texture
                    var u = tri.U0 * w0 + tri.U1 * w1 + tri.U2 * w2;
                    var v = tri.V0 * w0 + tri.V1 * w1 + tri.V2 * w2;

                    // Wrap UVs for tiling (handles negative values correctly)
                    u -= MathF.Floor(u);
                    v -= MathF.Floor(v);

                    // Bilinear filtered sample
                    var (r, g, b, a) = DisableTextures ? ((byte)200, (byte)200, (byte)200, (byte)255) : SampleTexture(tex, u, v);

                    // Apply vertex alpha: multiply texture alpha by interpolated vertex alpha
                    if (tri.HasVertexColors)
                    {
                        var vca = tri.A0 * w0 + tri.A1 * w1 + tri.A2 * w2;
                        a = (byte)Math.Clamp(a * vca / 255f, 0, 255);
                    }

                    // Alpha test: apply comparison function from NiAlphaProperty bits 10-12
                    if (tri.HasAlphaTest)
                    {
                        var pass = tri.AlphaTestFunction switch
                        {
                            0 => true,                              // ALWAYS
                            1 => a < tri.AlphaTestThreshold,        // LESS
                            2 => a == tri.AlphaTestThreshold,       // EQUAL
                            3 => a <= tri.AlphaTestThreshold,       // LEQUAL
                            4 => a > tri.AlphaTestThreshold,        // GREATER
                            5 => a != tri.AlphaTestThreshold,       // NOTEQUAL
                            6 => a >= tri.AlphaTestThreshold,       // GEQUAL
                            _ => false,                             // NEVER
                        };
                        if (!pass) continue;
                    }
                    else if (a == 0 || (tri.HasAlphaBlend && a < 16))
                    {
                        continue; // Skip fully transparent + DXT fringe on blended meshes
                    }

                    float fr, fg, fb;
                    if (tri.HasTintColor)
                    {
                        // SM3002.pso hair shader (from D3D9 bytecode disassembly):
                        //   tintedShade = 2 * (vc * (HairTint - 0.5) + 0.5)
                        //   final = accumulatedDiffuse * blendedTex * tintedShade
                        // The "lightScalar" is a hardcoded -0.5 (def c6), NOT per-pixel NdotL.
                        // With vc=1: tintedShade = 2*HairTint. Dark tints darken, light tints brighten.
                        float vc = tri.HasVertexColors
                            ? (tri.G0 * w0 + tri.G1 * w1 + tri.G2 * w2) / 255f
                            : 1f;
                        float tintShadeR = 2f * (vc * (tri.TintR - 0.5f) + 0.5f);
                        float tintShadeG = 2f * (vc * (tri.TintG - 0.5f) + 0.5f);
                        float tintShadeB = 2f * (vc * (tri.TintB - 0.5f) + 0.5f);
                        fr = r * tintShadeR * shade;
                        fg = g * tintShadeG * shade;
                        fb = b * tintShadeB * shade;
                    }
                    else
                    {
                        // Standard path: modulate texture color by shade and vertex color
                        fr = r * shade;
                        fg = g * shade;
                        fb = b * shade;

                        if (tri.HasVertexColors)
                        {
                            var vcr = (tri.R0 * w0 + tri.R1 * w1 + tri.R2 * w2) / 255f;
                            var vcg = (tri.G0 * w0 + tri.G1 * w1 + tri.G2 * w2) / 255f;
                            var vcb = (tri.B0 * w0 + tri.B1 * w1 + tri.B2 * w2) / 255f;
                            fr *= vcr;
                            fg *= vcg;
                            fb *= vcb;
                        }
                    }

                    var useBlend = tri.HasAlphaBlend || tri.MaterialAlpha < 1f;
                    var fk = isBackFacing ? (byte)2 : (byte)1;
                    if (!useBlend || a >= 255)
                    {
                        // Fully opaque: overwrite pixel and update depth
                        depthBuffer[idx] = z;
                        faceKind[idx] = fk;
                        pixels[pIdx + 0] = (byte)Math.Clamp(fr, 0, 255);
                        pixels[pIdx + 1] = (byte)Math.Clamp(fg, 0, 255);
                        pixels[pIdx + 2] = (byte)Math.Clamp(fb, 0, 255);
                        pixels[pIdx + 3] = 255;
                    }
                    else
                    {
                        // Semi-transparent: alpha-blend over existing pixel using NiAlphaProperty blend modes.
                        // Only write depth for mostly-opaque pixels (alpha >= 128). This prevents
                        // semi-transparent hair strands from occluding geometry behind them while
                        // still allowing opaque hair regions to block later geometry (e.g., eyebrow
                        // fringe). Matches the GPU approach (blended pipelines have depth writes OFF).
                        if (a >= 128) { depthBuffer[idx] = z; faceKind[idx] = fk; }
                        var srcA = a / 255f;
                        var sf = ResolveBlendFactor(tri.SrcBlendMode, srcA);
                        var df = ResolveBlendFactor(tri.DstBlendMode, srcA);
                        pixels[pIdx + 0] = (byte)Math.Clamp(fr * sf + pixels[pIdx + 0] * df, 0, 255);
                        pixels[pIdx + 1] = (byte)Math.Clamp(fg * sf + pixels[pIdx + 1] * df, 0, 255);
                        pixels[pIdx + 2] = (byte)Math.Clamp(fb * sf + pixels[pIdx + 2] * df, 0, 255);
                        pixels[pIdx + 3] = Math.Max(pixels[pIdx + 3], a);
                    }
                }
                else
                {
                    // Grayscale shading fallback
                    depthBuffer[idx] = z;
                    faceKind[idx] = isBackFacing ? (byte)2 : (byte)1;
                    var brightness = (byte)(shade * 220 + 35); // Range 35-255

                    if (tri.HasTintColor)
                    {
                        // Hair tint (no texture fallback): same SM3002 formula
                        float vc = tri.HasVertexColors
                            ? (tri.G0 * w0 + tri.G1 * w1 + tri.G2 * w2) / 255f
                            : 1f;
                        float baseVal = tri.HasVertexColors ? 255f : brightness;
                        pixels[pIdx + 0] = (byte)Math.Clamp(baseVal * 2f * (vc * (tri.TintR - 0.5f) + 0.5f) * shade, 0, 255);
                        pixels[pIdx + 1] = (byte)Math.Clamp(baseVal * 2f * (vc * (tri.TintG - 0.5f) + 0.5f) * shade, 0, 255);
                        pixels[pIdx + 2] = (byte)Math.Clamp(baseVal * 2f * (vc * (tri.TintB - 0.5f) + 0.5f) * shade, 0, 255);
                    }
                    else if (tri.HasVertexColors)
                    {
                        float vcr = tri.R0 * w0 + tri.R1 * w1 + tri.R2 * w2;
                        float vcg = tri.G0 * w0 + tri.G1 * w1 + tri.G2 * w2;
                        float vcb = tri.B0 * w0 + tri.B1 * w1 + tri.B2 * w2;
                        vcr *= shade;
                        vcg *= shade;
                        vcb *= shade;
                        pixels[pIdx + 0] = (byte)Math.Clamp(vcr, 0, 255);
                        pixels[pIdx + 1] = (byte)Math.Clamp(vcg, 0, 255);
                        pixels[pIdx + 2] = (byte)Math.Clamp(vcb, 0, 255);
                    }
                    else
                    {
                        pixels[pIdx + 0] = brightness;
                        pixels[pIdx + 1] = brightness;
                        pixels[pIdx + 2] = brightness;
                    }

                    pixels[pIdx + 3] = 255;
                }

                // Track whether the front-most pixel at this location is emissive.
                // Must clear when a non-emissive pixel overwrites, otherwise bloom
                // bleeds through walls from occluded emissive surfaces behind them.
                emissiveMask[idx] = tri.IsEmissive;
            }
        }
    }

    /// <summary>
    ///     Post-processing bloom: extracts emissive pixels, applies Gaussian blur, then
    ///     additively blends the glow back onto the main framebuffer.
    /// </summary>
    private static void ApplyBloom(byte[] pixels, bool[] emissiveMask, int width, int height)
    {
        const int radius = 8;
        const float sigma = radius / 2.5f;
        const float bloomIntensity = 0.8f;

        // Build 1D Gaussian kernel
        var kernelSize = radius * 2 + 1;
        var kernel = new float[kernelSize];
        var sum = 0f;
        for (var i = 0; i < kernelSize; i++)
        {
            var x = i - radius;
            kernel[i] = MathF.Exp(-(x * x) / (2f * sigma * sigma));
            sum += kernel[i];
        }

        // Normalize kernel
        for (var i = 0; i < kernelSize; i++)
        {
            kernel[i] /= sum;
        }

        // Check if any emissive pixels exist (skip bloom entirely if none)
        var hasEmissive = false;
        for (var i = 0; i < emissiveMask.Length; i++)
        {
            if (emissiveMask[i])
            {
                hasEmissive = true;
                break;
            }
        }

        if (!hasEmissive)
        {
            return;
        }

        // Extract emissive pixel colors into float buffers
        var srcR = new float[width * height];
        var srcG = new float[width * height];
        var srcB = new float[width * height];

        for (var i = 0; i < emissiveMask.Length; i++)
        {
            if (emissiveMask[i])
            {
                var pIdx = i * 4;
                srcR[i] = pixels[pIdx + 0];
                srcG[i] = pixels[pIdx + 1];
                srcB[i] = pixels[pIdx + 2];
            }
        }

        // Horizontal Gaussian blur pass
        var tmpR = new float[width * height];
        var tmpG = new float[width * height];
        var tmpB = new float[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var sx = Math.Clamp(x + k, 0, width - 1);
                    var sIdx = y * width + sx;
                    var w = kernel[k + radius];
                    r += srcR[sIdx] * w;
                    g += srcG[sIdx] * w;
                    b += srcB[sIdx] * w;
                }

                var idx = y * width + x;
                tmpR[idx] = r;
                tmpG[idx] = g;
                tmpB[idx] = b;
            }
        }

        // Vertical Gaussian blur pass
        var blurR = new float[width * height];
        var blurG = new float[width * height];
        var blurB = new float[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var sy = Math.Clamp(y + k, 0, height - 1);
                    var sIdx = sy * width + x;
                    var w = kernel[k + radius];
                    r += tmpR[sIdx] * w;
                    g += tmpG[sIdx] * w;
                    b += tmpB[sIdx] * w;
                }

                var idx = y * width + x;
                blurR[idx] = r;
                blurG[idx] = g;
                blurB[idx] = b;
            }
        }

        // Additively blend bloom back onto the framebuffer
        for (var i = 0; i < width * height; i++)
        {
            var bloom = blurR[i] + blurG[i] + blurB[i];
            if (bloom < 1f)
            {
                continue;
            }

            var pIdx = i * 4;
            pixels[pIdx + 0] = (byte)Math.Min(pixels[pIdx + 0] + blurR[i] * bloomIntensity, 255);
            pixels[pIdx + 1] = (byte)Math.Min(pixels[pIdx + 1] + blurG[i] * bloomIntensity, 255);
            pixels[pIdx + 2] = (byte)Math.Min(pixels[pIdx + 2] + blurB[i] * bloomIntensity, 255);

            // Ensure bloom pixels are visible even on transparent background
            if (pixels[pIdx + 3] == 0 && bloom > 5f)
            {
                var glowAlpha = Math.Min(bloom * bloomIntensity / 3f, 255f);
                pixels[pIdx + 3] = (byte)glowAlpha;
            }
        }
    }

    /// <summary>
    ///     Bilinear texture sampling: interpolates between the 4 nearest texels
    ///     for smooth texture filtering instead of blocky nearest-neighbor.
    /// </summary>
    private static (byte R, byte G, byte B, byte A) SampleTexture(DecodedTexture tex, float u, float v)
    {
        return DisableBilinear ? SampleNearest(tex, u, v) : SampleBilinear(tex, u, v);
    }

    private static (byte R, byte G, byte B, byte A) SampleNearest(DecodedTexture tex, float u, float v)
    {
        var x = ((int)(u * tex.Width) % tex.Width + tex.Width) % tex.Width;
        var y = ((int)(v * tex.Height) % tex.Height + tex.Height) % tex.Height;
        var i = (y * tex.Width + x) * 4;
        return (tex.Pixels[i], tex.Pixels[i + 1], tex.Pixels[i + 2], tex.Pixels[i + 3]);
    }

    private static (byte R, byte G, byte B, byte A) SampleBilinear(DecodedTexture tex, float u, float v)
    {
        var fx = u * tex.Width - 0.5f;
        var fy = v * tex.Height - 0.5f;

        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var fracX = fx - x0;
        var fracY = fy - y0;

        // Wrap coordinates for tiling
        var x1 = x0 + 1;
        x0 = ((x0 % tex.Width) + tex.Width) % tex.Width;
        x1 = ((x1 % tex.Width) + tex.Width) % tex.Width;
        y0 = ((y0 % tex.Height) + tex.Height) % tex.Height;
        var y1 = (y0 + 1) % tex.Height;

        // Fetch 4 texels
        var i00 = (y0 * tex.Width + x0) * 4;
        var i10 = (y0 * tex.Width + x1) * 4;
        var i01 = (y1 * tex.Width + x0) * 4;
        var i11 = (y1 * tex.Width + x1) * 4;

        var p = tex.Pixels;
        var invFx = 1f - fracX;
        var invFy = 1f - fracY;
        var w00 = invFx * invFy;
        var w10 = fracX * invFy;
        var w01 = invFx * fracY;
        var w11 = fracX * fracY;

        return (
            (byte)(p[i00] * w00 + p[i10] * w10 + p[i01] * w01 + p[i11] * w11),
            (byte)(p[i00 + 1] * w00 + p[i10 + 1] * w10 + p[i01 + 1] * w01 + p[i11 + 1] * w11),
            (byte)(p[i00 + 2] * w00 + p[i10 + 2] * w10 + p[i01 + 2] * w01 + p[i11 + 2] * w11),
            (byte)(p[i00 + 3] * w00 + p[i10 + 3] * w10 + p[i01 + 3] * w01 + p[i11 + 3] * w11)
        );
    }

    /// <summary>
    ///     Downsample a supersampled RGBA buffer by a given factor using box filter averaging.
    ///     Input dimensions must be exact multiples of the factor.
    /// </summary>
    internal static byte[] Downsample(byte[] src, int srcW, int srcH, int factor)
    {
        var dstW = srcW / factor;
        var dstH = srcH / factor;
        var dst = new byte[dstW * dstH * 4];
        var area = factor * factor;
        var invArea = 1f / area;

        for (var dy = 0; dy < dstH; dy++)
        {
            for (var dx = 0; dx < dstW; dx++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                var sx0 = dx * factor;
                var sy0 = dy * factor;

                for (var sy = sy0; sy < sy0 + factor; sy++)
                {
                    var rowOff = sy * srcW * 4 + sx0 * 4;
                    for (var sx = 0; sx < factor; sx++)
                    {
                        var i = rowOff + sx * 4;
                        r += src[i];
                        g += src[i + 1];
                        b += src[i + 2];
                        a += src[i + 3];
                    }
                }

                var dIdx = (dy * dstW + dx) * 4;
                dst[dIdx] = (byte)(r * invArea);
                dst[dIdx + 1] = (byte)(g * invArea);
                dst[dIdx + 2] = (byte)(b * invArea);
                dst[dIdx + 3] = (byte)(a * invArea);
            }
        }

        return dst;
    }

    /// <summary>
    ///     Resolve a D3D blend factor enum value to a multiplier.
    ///     Matches SetupGeometryAlphaBlending (VA 0x82AAD430) blend mode extraction.
    ///     Modes 2/3 (SRC_COLOR/INV_SRC_COLOR) approximated using alpha since we don't have
    ///     per-channel source color separately in the blend equation.
    /// </summary>
    private static float ResolveBlendFactor(byte mode, float srcAlpha) => mode switch
    {
        0 => 1f,           // ONE
        1 => 0f,           // ZERO
        2 => srcAlpha,     // SRC_COLOR (approx: use alpha)
        3 => 1f - srcAlpha, // INV_SRC_COLOR (approx)
        6 => srcAlpha,     // SRC_ALPHA
        7 => 1f - srcAlpha, // INV_SRC_ALPHA
        _ => srcAlpha      // Fallback to SRC_ALPHA behavior
    };

    private struct TriangleData
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

        // Eye environment map (SLS2057.pso cubemap reflection approximation)
        public bool IsEyeEnvmap;
        public float EnvMapScale;

        // Layer-based render order (engine renders head parts in scene graph order)
        public int RenderOrder;

        // Hair tint from HCLR: SM3002.pso formula 2*(vc*(tint-0.5)+0.5) * accDiffuse * tex
        public bool HasTintColor;
        public float TintR, TintG, TintB;
    }
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

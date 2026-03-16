using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Renders a <see cref="NifRenderableModel" /> to a transparent RGBA pixel buffer
///     using orthographic top-down projection with per-vertex smooth shading, optional texture mapping,
///     bump mapping, and vertex colors.
///     Uses a scanline triangle rasterizer with Z-buffer depth testing.
/// </summary>
internal static class NifSpriteRenderer
{
    // SM3002.pso hair tint shader constant: the "lightScalar" in the tint formula is
    // a hardcoded -0.5 (from def c6 = {-0.5, 0, -1, -2}, register never overwritten).
    // Formula: tintedShade = 2 * (vc * (HairTint - 0.5) + 0.5)
    // With vc=1 this simplifies to 2 * HairTint, so dark tints darken and light tints brighten.

    // Lighting constants from shared RenderLightingConstants
    private static readonly float LightDirX = RenderLightingConstants.LightDir.X;
    private static readonly float LightDirY = RenderLightingConstants.LightDir.Y;
    private static readonly float LightDirZ = RenderLightingConstants.LightDir.Z;
    private static readonly float HalfVecX = RenderLightingConstants.HalfVec.X;
    private static readonly float HalfVecY = RenderLightingConstants.HalfVec.Y;

    private static readonly float HalfVecZ = RenderLightingConstants.HalfVec.Z;

    // Debug flags for diagnosing rendering artifacts
    internal static bool DisableBilinear { get; set; }
    internal static bool DisableBumpMapping { get; set; }
    internal static bool DisableTextures { get; set; }
    internal static bool DrawWireframeOverlay { get; set; }

    /// <summary>
    ///     Normal map bump strength (0 = flat, 1 = full).  The game's multi-light
    ///     environment naturally softens bump detail; our single key light exaggerates
    ///     it.  Default 0.5 compensates for the missing fill lights.
    /// </summary>
    internal static float BumpStrength { get; set; } = 0.5f;

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
    ///     Core render pipeline: canvas sizing -> sort -> rasterize -> downsample.
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
        var ssWidth = width * RenderLightingConstants.SsaaFactor;
        var ssHeight = height * RenderLightingConstants.SsaaFactor;

        // Effective pixels-per-unit after clamping (at supersampled resolution)
        var effPpuX = (ssWidth - 2 * RenderLightingConstants.SsaaFactor) / Math.Max(modelWidth, 0.001f);
        var effPpuY = (ssHeight - 2 * RenderLightingConstants.SsaaFactor) / Math.Max(modelHeight, 0.001f);
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

        var renderLayers = BuildRenderLayers(triangleList);

        // Band-parallel rasterization: divide the framebuffer into horizontal bands,
        // each processed by a separate thread. Since bands own exclusive rows, no
        // synchronization is needed — each pixel (px, py) belongs to exactly one band.
        // Within a render layer, opaque/cutout triangles run before blended triangles so
        // thin shells write depth before later transparent overlays are composited.
        var bandCount = Math.Max(1, Environment.ProcessorCount);
        Parallel.For(0, bandCount, bandIdx =>
        {
            var bMinY = bandIdx * ssHeight / bandCount;
            var bMaxY = (bandIdx + 1) * ssHeight / bandCount - 1;
            foreach (var layer in renderLayers)
            {
                foreach (var tri in layer.OpaqueAndCutoutTriangles)
                {
                    NifScanlineRasterizer.RasterizeTriangle(ssPixels, depthBuffer, faceKind, emissiveMask, ssWidth,
                        tri, effPpu, offsetX, offsetY, bMinY, bMaxY);
                }

                foreach (var tri in layer.BlendedTriangles)
                {
                    NifScanlineRasterizer.RasterizeTriangle(ssPixels, depthBuffer, faceKind, emissiveMask, ssWidth,
                        tri, effPpu, offsetX, offsetY, bMinY, bMaxY);
                }
            }
        });

        // Apply bloom glow around emissive pixels
        NifPostProcessing.ApplyBloom(ssPixels, emissiveMask, ssWidth, ssHeight);

        if (DrawWireframeOverlay)
        {
            foreach (var tri in triangleList)
            {
                NifScanlineRasterizer.DrawTriangleWireframeOverlay(
                    ssPixels,
                    depthBuffer,
                    ssWidth,
                    ssHeight,
                    tri,
                    effPpu,
                    offsetX,
                    offsetY);
            }
        }

        // Downsample to final resolution with box filter
        var pixels = Downsample(ssPixels, ssWidth, ssHeight, RenderLightingConstants.SsaaFactor);

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
        // right       = (-sin(a), cos(a), 0)              — negated for right-handed screen coords
        // screen_down = (sin(t)*cos(a), sin(t)*sin(a), -cos(t))  — negated so world +Z maps to screen up
        // toward_cam  = (cos(a)*cos(t), sin(a)*cos(t), sin(t))   — points toward camera so closer = higher Z
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

    private static IReadOnlyList<RenderLayer> BuildRenderLayers(List<TriangleData> triangles)
    {
        return triangles
            .GroupBy(tri => tri.RenderOrder)
            .OrderBy(group => group.Key)
            .Select(group => new RenderLayer(
                group.Where(tri => tri.AlphaRenderMode != NifAlphaRenderMode.Blend)
                    .OrderBy(tri => tri.AvgZ)
                    .ToArray(),
                group.Where(tri => tri.AlphaRenderMode == NifAlphaRenderMode.Blend)
                    .OrderBy(tri => tri.AvgZ)
                    .ToArray()))
            .ToArray();
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

            var alphaState = NifAlphaClassifier.Classify(submesh, texture);

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
                    HasAlphaBlend = alphaState.HasAlphaBlend,
                    HasAlphaTest = alphaState.HasAlphaTest,
                    AlphaTestThreshold = alphaState.AlphaTestThreshold,
                    AlphaTestFunction = alphaState.AlphaTestFunction,
                    SrcBlendMode = alphaState.SrcBlendMode,
                    DstBlendMode = alphaState.DstBlendMode,
                    MaterialAlpha = alphaState.MaterialAlpha,
                    AlphaRenderMode = alphaState.RenderMode,
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
                        tri.U0 = uvs[uv0];
                        tri.V0 = uvs[uv0 + 1];
                        tri.U1 = uvs[uv1];
                        tri.V1 = uvs[uv1 + 1];
                        tri.U2 = uvs[uv2];
                        tri.V2 = uvs[uv2 + 1];
                        tri.Texture = texture;
                        tri.NormalMap = normalMap;
                    }
                }

                // Per-vertex normals for smooth shading
                var hasVertexNormals = false;
                if (nrm != null && i0 + 2 < nrm.Length && i1 + 2 < nrm.Length && i2 + 2 < nrm.Length)
                {
                    tri.Nx0 = nrm[i0];
                    tri.Ny0 = nrm[i0 + 1];
                    tri.Nz0 = nrm[i0 + 2];
                    tri.Nx1 = nrm[i1];
                    tri.Ny1 = nrm[i1 + 1];
                    tri.Nz1 = nrm[i1 + 2];
                    tri.Nx2 = nrm[i2];
                    tri.Ny2 = nrm[i2 + 1];
                    tri.Nz2 = nrm[i2 + 2];
                    tri.HasVertexNormals = true;
                    hasVertexNormals = true;
                }

                // Per-vertex tangents/bitangents for bump mapping
                if (hasVertexNormals && tan != null && bitan != null &&
                    i0 + 2 < tan.Length && i1 + 2 < tan.Length && i2 + 2 < tan.Length &&
                    i0 + 2 < bitan.Length && i1 + 2 < bitan.Length && i2 + 2 < bitan.Length)
                {
                    tri.Tx0 = tan[i0];
                    tri.Ty0 = tan[i0 + 1];
                    tri.Tz0 = tan[i0 + 2];
                    tri.Tx1 = tan[i1];
                    tri.Ty1 = tan[i1 + 1];
                    tri.Tz1 = tan[i1 + 2];
                    tri.Tx2 = tan[i2];
                    tri.Ty2 = tan[i2 + 1];
                    tri.Tz2 = tan[i2 + 2];
                    tri.Bx0 = bitan[i0];
                    tri.By0 = bitan[i0 + 1];
                    tri.Bz0 = bitan[i0 + 2];
                    tri.Bx1 = bitan[i1];
                    tri.By1 = bitan[i1 + 1];
                    tri.Bz1 = bitan[i1 + 2];
                    tri.Bx2 = bitan[i2];
                    tri.By2 = bitan[i2 + 1];
                    tri.Bz2 = bitan[i2 + 2];
                    tri.HasTangents = true;
                }

                if (NifVertexColorPolicy.HasVertexColorData(submesh))
                {
                    var vertexIndex0 = tris[t];
                    var vertexIndex1 = tris[t + 1];
                    var vertexIndex2 = tris[t + 2];
                    var ci0 = vertexIndex0 * 4;
                    var ci1 = vertexIndex1 * 4;
                    var ci2 = vertexIndex2 * 4;
                    if (vcol != null &&
                        ci0 + 3 < vcol.Length &&
                        ci1 + 3 < vcol.Length &&
                        ci2 + 3 < vcol.Length)
                    {
                        var c0 = NifVertexColorPolicy.Read(submesh, vertexIndex0);
                        var c1 = NifVertexColorPolicy.Read(submesh, vertexIndex1);
                        var c2 = NifVertexColorPolicy.Read(submesh, vertexIndex2);
                        tri.R0 = c0.R;
                        tri.G0 = c0.G;
                        tri.B0 = c0.B;
                        tri.A0 = c0.A;
                        tri.R1 = c1.R;
                        tri.G1 = c1.G;
                        tri.B1 = c1.B;
                        tri.A1 = c1.A;
                        tri.R2 = c2.R;
                        tri.G2 = c2.G;
                        tri.B2 = c2.B;
                        tri.A2 = c2.A;
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
    ///         SKIN2000 lighting:
    ///         fresnel = dot(H, -L) * (1 - NdotH)^2
    ///         directional = min(PSLightColor * NdotL + PSLightColor * fresnel * 0.5, 1.0)
    ///         shade = directional + AmbientColor
    ///     </para>
    /// </summary>
    internal static float ComputeShade(float nx, float ny, float nz, bool twoSidedLighting = false)
    {
        // Hemisphere ambient: blend between ground and sky based on normal Y
        // Negated: view-space Y points down, so -ny maps "up" normals to sky
        var hemiBlend = -ny * 0.5f + 0.5f;
        var ambient = RenderLightingConstants.GroundAmbient +
                      (RenderLightingConstants.SkyAmbient - RenderLightingConstants.GroundAmbient) * hemiBlend;

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
        var fresnel = MathF.Max(0, RenderLightingConstants.HdotNegL) * oneMinusNdotH * oneMinusNdotH;

        // SKIN2000: min(lightColor * NdotL + lightColor * fresnel * 0.5, 1.0) + ambient
        var directional = MathF.Min(RenderLightingConstants.LightIntensity * NdotL +
                                    RenderLightingConstants.LightIntensity * fresnel * 0.5f, 1f);

        return Math.Clamp(directional + ambient, 0f, 1f);
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

    private sealed record RenderLayer(
        TriangleData[] OpaqueAndCutoutTriangles,
        TriangleData[] BlendedTriangles);
}

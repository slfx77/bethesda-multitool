using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Scanline triangle rasterizer with per-pixel Z-buffer, texture mapping, bump mapping,
///     alpha blending, and vertex color support. Extracted from <see cref="NifSpriteRenderer"/>.
/// </summary>
internal static class NifScanlineRasterizer
{
    /// <summary>
    ///     Rasterize a single filled triangle using scanline algorithm with per-pixel Z-buffer.
    ///     Supports per-vertex normal interpolation, texture mapping, bump mapping, and vertex colors.
    /// </summary>
    internal static void RasterizeTriangle(byte[] pixels, float[] depthBuffer, byte[] faceKind,
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
                    if (!NifSpriteRenderer.DisableBumpMapping && normalMap != null && tri.HasTangents && tex != null)
                    {
                        var u = tri.U0 * w0 + tri.U1 * w1 + tri.U2 * w2;
                        var v = tri.V0 * w0 + tri.V1 * w1 + tri.V2 * w2;
                        u -= MathF.Floor(u);
                        v -= MathF.Floor(v);

                        var (mnr, mng, mnb, _) = SampleTexture(normalMap, u, v);

                        // Decode from [0,255] to [-1,1]
                        // Y is negated: Bethesda normal maps use DirectX convention (Y-down)
                        // but NIF bitangent vectors point in UV V+ direction (Y-up).
                        var mapNx = (mnr / 127.5f - 1f) * NifSpriteRenderer.BumpStrength;
                        var mapNy = -(mng / 127.5f - 1f) * NifSpriteRenderer.BumpStrength;
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

                    shade = NifSpriteRenderer.ComputeShade(nx, ny, nz, tri.IsDoubleSided);

                    // Eye specular: approximate SLS2057.pso cubemap reflection as Blinn-Phong.
                    // The game uses EnvironmentCubeMap sampled at the reflection vector; we
                    // approximate with a focused specular highlight (shininess=16).
                    if (tri.IsEyeEnvmap)
                    {
                        var specNdotH = MathF.Max(0f,
                            nx * RenderLightingConstants.HalfVec.X +
                            ny * RenderLightingConstants.HalfVec.Y +
                            nz * RenderLightingConstants.HalfVec.Z);
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
                    var (r, g, b, a) = NifSpriteRenderer.DisableTextures ? ((byte)200, (byte)200, (byte)200, (byte)255) : SampleTexture(tex, u, v);

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
    ///     Bilinear texture sampling: interpolates between the 4 nearest texels
    ///     for smooth texture filtering instead of blocky nearest-neighbor.
    /// </summary>
    internal static (byte R, byte G, byte B, byte A) SampleTexture(DecodedTexture tex, float u, float v)
    {
        return NifSpriteRenderer.DisableBilinear ? SampleNearest(tex, u, v) : SampleBilinear(tex, u, v);
    }

    internal static (byte R, byte G, byte B, byte A) SampleNearest(DecodedTexture tex, float u, float v)
    {
        var x = ((int)(u * tex.Width) % tex.Width + tex.Width) % tex.Width;
        var y = ((int)(v * tex.Height) % tex.Height + tex.Height) % tex.Height;
        var i = (y * tex.Width + x) * 4;
        return (tex.Pixels[i], tex.Pixels[i + 1], tex.Pixels[i + 2], tex.Pixels[i + 3]);
    }

    internal static (byte R, byte G, byte B, byte A) SampleBilinear(DecodedTexture tex, float u, float v)
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
    ///     Resolve a D3D blend factor enum value to a multiplier.
    ///     Matches SetupGeometryAlphaBlending (VA 0x82AAD430) blend mode extraction.
    ///     Modes 2/3 (SRC_COLOR/INV_SRC_COLOR) approximated using alpha since we don't have
    ///     per-channel source color separately in the blend equation.
    /// </summary>
    internal static float ResolveBlendFactor(byte mode, float srcAlpha) => mode switch
    {
        0 => 1f,           // ONE
        1 => 0f,           // ZERO
        2 => srcAlpha,     // SRC_COLOR (approx: use alpha)
        3 => 1f - srcAlpha, // INV_SRC_COLOR (approx)
        6 => srcAlpha,     // SRC_ALPHA
        7 => 1f - srcAlpha, // INV_SRC_ALPHA
        _ => srcAlpha      // Fallback to SRC_ALPHA behavior
    };
}

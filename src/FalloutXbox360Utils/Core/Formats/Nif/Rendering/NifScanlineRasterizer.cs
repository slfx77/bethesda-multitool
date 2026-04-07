using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Scanline triangle rasterizer with per-pixel Z-buffer, texture mapping, bump mapping,
///     alpha blending, and vertex color support. Extracted from <see cref="NifSpriteRenderer" />.
/// </summary>
internal static class NifScanlineRasterizer
{
    private const float WireframeDepthEpsilon = 0.05f;

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
        var hasUvGradients = tri.Texture != null || tri.NormalMap != null;
        float duDx = 0f, duDy = 0f, dvDx = 0f, dvDy = 0f;
        if (hasUvGradients)
        {
            (duDx, duDy, dvDx, dvDy) = ComputeUvGradients(
                tri,
                sx0,
                sy0,
                sx1,
                sy1,
                sx2,
                sy2,
                invDenom);
        }

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

                var writesDepth = tri.AlphaRenderMode != NifAlphaRenderMode.Blend;

                // Per-pixel front-face priority is only used for opaque/cutout thin shells.
                // Blended surfaces are composited strictly by draw order with depth writes off.
                var passesDepthTest = z > depthBuffer[idx];
                var frontOverBack = writesDepth && !isBackFacing && faceKind[idx] == 2;
                var backBlockedByFront = writesDepth && isBackFacing && faceKind[idx] == 1;

                if (backBlockedByFront || (!passesDepthTest && !frontOverBack))
                {
                    continue;
                }

                var pIdx = idx * 4;

                // Emissive surfaces (BSShaderNoLightingProperty) are self-illuminated — no shading
                float shade;
                var specIntensity = 0f; // Blinn-Phong specular intensity (SM3001.pso: pow(NdotH, glossiness))
                var normalAlpha = 255f; // Normal map alpha = specular mask (SM3001.pso line 67,189)
                float nx = 0, ny = 0, nz = 1; // default normal (toward camera)

                if (tri.IsEmissive)
                {
                    shade = 1.0f;
                }
                else if (tri.HasVertexNormals)
                {
                    nx = tri.Nx0 * w0 + tri.Nx1 * w1 + tri.Nx2 * w2;
                    ny = tri.Ny0 * w0 + tri.Ny1 * w1 + tri.Ny2 * w2;
                    nz = tri.Nz0 * w0 + tri.Nz1 * w1 + tri.Nz2 * w2;

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

                        var (mnr, mng, mnb, mna) = SampleTexture(
                            normalMap,
                            u,
                            v,
                            duDx,
                            dvDx,
                            duDy,
                            dvDy);

                        // Normal map alpha = specular mask (SM3001.pso: mul r7.w, r3.w, c22.w)
                        normalAlpha = mna;

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
                            if (tLen > 0.001f)
                            {
                                tx /= tLen;
                                ty /= tLen;
                                tz /= tLen;
                            }

                            var bLen = MathF.Sqrt(bx * bx + by * by + bz * bz);
                            if (bLen > 0.001f)
                            {
                                bx /= bLen;
                                by /= bLen;
                                bz /= bLen;
                            }
                        }
                        else
                        {
                            // Fallback: derive tangent from cross product with stable axis
                            if (MathF.Abs(nx) < 0.9f)
                            {
                                tx = 0f;
                                ty = nz;
                                tz = -ny;
                            }
                            else
                            {
                                tx = -nz;
                                ty = 0f;
                                tz = nx;
                            }

                            var tLen = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
                            if (tLen > 0.001f)
                            {
                                tx /= tLen;
                                ty /= tLen;
                                tz /= tLen;
                            }

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

                    shade = tri.HasTintColor
                        ? NifSpriteRenderer.ComputeTintedShade(nx, ny, nz)
                        : NifSpriteRenderer.ComputeShade(nx, ny, nz, tri.IsDoubleSided);

                    // Eye specular: approximate SLS2057.pso cubemap reflection as Blinn-Phong.
                    // The game samples an EnvironmentCubeMap at the reflection vector; we
                    // approximate with a broad specular highlight. Low exponent (4) and
                    // reduced intensity create a subtle wet-eye gloss rather than a sharp hotspot.
                    if (tri.IsEyeEnvmap)
                    {
                        var specNdotH = MathF.Max(0f,
                            nx * RenderLightingConstants.HalfVec.X +
                            ny * RenderLightingConstants.HalfVec.Y +
                            nz * RenderLightingConstants.HalfVec.Z);
                        shade = MathF.Min(shade + MathF.Pow(specNdotH, 4f) * tri.EnvMapScale * 0.25f, 1f);
                    }
                    // General specular: Blinn-Phong from SM3001.pso disassembly.
                    // SM3001 lines 70-71: NdotH = dot(H, N); specPower = pow(NdotH, glossiness)
                    // SM3001 line 189: finalSpec *= normalMapAlpha (specular mask)
                    // SM3001 line 195: finalColor = diffuse * tex + specular (additive)
                    else if (tri.Glossiness > 2f && (tri.SpecR > 0f || tri.SpecG > 0f || tri.SpecB > 0f))
                    {
                        var specNdotH = MathF.Max(0f,
                            nx * RenderLightingConstants.HalfVec.X +
                            ny * RenderLightingConstants.HalfVec.Y +
                            nz * RenderLightingConstants.HalfVec.Z);
                        specIntensity = MathF.Pow(specNdotH, tri.Glossiness) * (normalAlpha / 255f);
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
                    var (r, g, b, a) = NifSpriteRenderer.DisableTextures
                        ? ((byte)200, (byte)200, (byte)200, (byte)255)
                        : SampleTexture(tex, u, v, duDx, dvDx, duDy, dvDy);

                    // Apply vertex alpha: multiply texture alpha by interpolated vertex alpha
                    if (tri.HasVertexColors)
                    {
                        var vca = tri.A0 * w0 + tri.A1 * w1 + tri.A2 * w2;
                        a = (byte)Math.Clamp(a * vca / 255f, 0, 255);
                    }

                    // Alpha test: apply comparison function from NiAlphaProperty bits 10-12.
                    // When both alpha test and alpha blend are enabled, the blend path handles
                    // transparency — the alpha test only serves as a pre-filter for fully
                    // transparent pixels (e.g., glass lenses have low alpha that must blend,
                    // not be discarded by the test threshold).
                    if (tri.HasAlphaTest && tri.AlphaRenderMode != NifAlphaRenderMode.Blend)
                    {
                        var pass = tri.AlphaTestFunction switch
                        {
                            0 => true, // ALWAYS
                            1 => a < tri.AlphaTestThreshold, // LESS
                            2 => a == tri.AlphaTestThreshold, // EQUAL
                            3 => a <= tri.AlphaTestThreshold, // LEQUAL
                            4 => a > tri.AlphaTestThreshold, // GREATER
                            5 => a != tri.AlphaTestThreshold, // NOTEQUAL
                            6 => a >= tri.AlphaTestThreshold, // GEQUAL
                            _ => false // NEVER
                        };
                        if (!pass) continue;
                    }
                    else if (tri.HasAlphaBlend && a == 0)
                    {
                        continue; // Skip fully transparent pixels on blended meshes
                    }

                    float fr, fg, fb;
                    if (tri.HasTintColor)
                    {
                        // Hair tint is an HCLR-driven uniform multiplier. Preserve vertex alpha,
                        // but do not modulate tinted RGB by raw mesh vertex colors; that produces
                        // blocky dark patches in profile views on hair/beard meshes.
                        var tintShadeR = 2f * tri.TintR;
                        var tintShadeG = 2f * tri.TintG;
                        var tintShadeB = 2f * tri.TintB;
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

                    // Add animated emissive color (e.g., Rex skull cap cyan glow)
                    if (tri.EmissiveR > 0f || tri.EmissiveG > 0f || tri.EmissiveB > 0f)
                    {
                        fr += tri.EmissiveR * 255f;
                        fg += tri.EmissiveG * 255f;
                        fb += tri.EmissiveB * 255f;
                    }

                    // Add Blinn-Phong specular highlight (SM3001.pso line 195: finalColor = diffuse*tex + spec)
                    // SM3001 multiplies by ToggleNumLights.w (runtime specular intensity, unknown value).
                    // NiMaterialProperty specular color provides per-channel tint; 0.15 global scale
                    // approximates the engine's subtle specular without knowing the exact runtime value.
                    if (specIntensity > 0f)
                    {
                        const float specScale = 0.06f;
                        fr += tri.SpecR * specIntensity * specScale * 255f;
                        fg += tri.SpecG * specIntensity * specScale * 255f;
                        fb += tri.SpecB * specIntensity * specScale * 255f;
                    }

                    var fk = isBackFacing ? (byte)2 : (byte)1;
                    if (tri.AlphaRenderMode != NifAlphaRenderMode.Blend)
                    {
                        // Opaque + alpha-tested cutout passes overwrite color and write depth.
                        depthBuffer[idx] = z;
                        faceKind[idx] = fk;
                        pixels[pIdx + 0] = (byte)Math.Clamp(fr, 0, 255);
                        pixels[pIdx + 1] = (byte)Math.Clamp(fg, 0, 255);
                        pixels[pIdx + 2] = (byte)Math.Clamp(fb, 0, 255);
                        pixels[pIdx + 3] = 255;
                    }
                    else
                    {
                        // Blended pass: preserve existing depth so later transparent layers can
                        // composite while still respecting opaque geometry already in the Z buffer.
                        var srcA = Math.Clamp(a * MathF.Min(tri.MaterialAlpha, 1f) / 255f, 0f, 1f);
                        if (srcA <= 0f)
                        {
                            continue;
                        }

                        // For emissive blended surfaces with additive blend mode (DstBlend=ONE,
                        // e.g., steam, energy FX), use actual additive blending via the
                        // standard blend path — the SrcBlendMode/DstBlendMode already encode
                        // the correct behavior.  For non-additive emissive surfaces (e.g.,
                        // Rex brain cage with standard SRC_ALPHA/INV_SRC_ALPHA), keep the
                        // screen blend at half intensity for an illuminated glass look.
                        if (tri.IsEmissive && tri.DstBlendMode != 0) // 0 = ONE (additive)
                        {
                            pixels[pIdx + 0] =
                                (byte)Math.Clamp(pixels[pIdx + 0] + fr * 0.5f - pixels[pIdx + 0] * fr * 0.5f / 255f, 0,
                                    255);
                            pixels[pIdx + 1] =
                                (byte)Math.Clamp(pixels[pIdx + 1] + fg * 0.5f - pixels[pIdx + 1] * fg * 0.5f / 255f, 0,
                                    255);
                            pixels[pIdx + 2] =
                                (byte)Math.Clamp(pixels[pIdx + 2] + fb * 0.5f - pixels[pIdx + 2] * fb * 0.5f / 255f, 0,
                                    255);
                        }
                        else
                        {
                            // Normalize source color for per-channel blend factors (SRC_COLOR / DST_COLOR)
                            var srcR = Math.Clamp(fr / 255f, 0f, 1f);
                            var srcG = Math.Clamp(fg / 255f, 0f, 1f);
                            var srcB = Math.Clamp(fb / 255f, 0f, 1f);
                            var dstR = pixels[pIdx + 0] / 255f;
                            var dstG = pixels[pIdx + 1] / 255f;
                            var dstB = pixels[pIdx + 2] / 255f;

                            pixels[pIdx + 0] = (byte)Math.Clamp(
                                fr * ResolveBlendFactor(tri.SrcBlendMode, srcA, srcR, srcG, srcB, dstR, dstG, dstB, 0) +
                                pixels[pIdx + 0] * ResolveBlendFactor(tri.DstBlendMode, srcA, srcR, srcG, srcB, dstR,
                                    dstG, dstB, 0), 0, 255);
                            pixels[pIdx + 1] = (byte)Math.Clamp(
                                fg * ResolveBlendFactor(tri.SrcBlendMode, srcA, srcR, srcG, srcB, dstR, dstG, dstB, 1) +
                                pixels[pIdx + 1] * ResolveBlendFactor(tri.DstBlendMode, srcA, srcR, srcG, srcB, dstR,
                                    dstG, dstB, 1), 0, 255);
                            pixels[pIdx + 2] = (byte)Math.Clamp(
                                fb * ResolveBlendFactor(tri.SrcBlendMode, srcA, srcR, srcG, srcB, dstR, dstG, dstB, 2) +
                                pixels[pIdx + 2] * ResolveBlendFactor(tri.DstBlendMode, srcA, srcR, srcG, srcB, dstR,
                                    dstG, dstB, 2), 0, 255);
                        }

                        // Additive blending (DstBlend=ONE) adds light without occluding
                        // what's behind — keep output alpha low so the effect looks
                        // translucent against transparent PNG backgrounds.
                        var outAlpha = tri.DstBlendMode == 0
                            ? (byte)Math.Clamp(srcA * 64f, 0f, 255f)
                            : (byte)Math.Clamp(srcA * 255f, 0f, 255f);
                        pixels[pIdx + 3] = Math.Max(pixels[pIdx + 3], outAlpha);
                    }
                }
                else
                {
                    // Grayscale shading fallback
                    var brightness = (byte)(shade * 220 + 35); // Range 35-255
                    var fk = isBackFacing ? (byte)2 : (byte)1;
                    var vertexAlpha = tri.HasVertexColors
                        ? Math.Clamp(tri.A0 * w0 + tri.A1 * w1 + tri.A2 * w2, 0f, 255f)
                        : 255f;

                    if (tri.HasAlphaTest)
                    {
                        var pass = tri.AlphaTestFunction switch
                        {
                            0 => true,
                            1 => vertexAlpha < tri.AlphaTestThreshold,
                            2 => MathF.Abs(vertexAlpha - tri.AlphaTestThreshold) < 0.5f,
                            3 => vertexAlpha <= tri.AlphaTestThreshold,
                            4 => vertexAlpha > tri.AlphaTestThreshold,
                            5 => MathF.Abs(vertexAlpha - tri.AlphaTestThreshold) >= 0.5f,
                            6 => vertexAlpha >= tri.AlphaTestThreshold,
                            _ => false
                        };
                        if (!pass)
                        {
                            continue;
                        }
                    }

                    float fr, fg, fb;
                    if (tri.HasTintColor)
                    {
                        var baseVal = brightness;
                        fr = Math.Clamp(baseVal * 2f * tri.TintR * shade, 0, 255);
                        fg = Math.Clamp(baseVal * 2f * tri.TintG * shade, 0, 255);
                        fb = Math.Clamp(baseVal * 2f * tri.TintB * shade, 0, 255);
                    }
                    else if (tri.HasVertexColors)
                    {
                        var vcr = tri.R0 * w0 + tri.R1 * w1 + tri.R2 * w2;
                        var vcg = tri.G0 * w0 + tri.G1 * w1 + tri.G2 * w2;
                        var vcb = tri.B0 * w0 + tri.B1 * w1 + tri.B2 * w2;
                        fr = Math.Clamp(vcr * shade, 0, 255);
                        fg = Math.Clamp(vcg * shade, 0, 255);
                        fb = Math.Clamp(vcb * shade, 0, 255);
                    }
                    else
                    {
                        fr = brightness;
                        fg = brightness;
                        fb = brightness;
                    }

                    if (tri.AlphaRenderMode != NifAlphaRenderMode.Blend)
                    {
                        depthBuffer[idx] = z;
                        faceKind[idx] = fk;
                        pixels[pIdx + 0] = (byte)fr;
                        pixels[pIdx + 1] = (byte)fg;
                        pixels[pIdx + 2] = (byte)fb;
                        pixels[pIdx + 3] = 255;
                    }
                    else
                    {
                        var srcA = Math.Clamp(vertexAlpha * MathF.Min(tri.MaterialAlpha, 1f) / 255f, 0f, 1f);
                        if (srcA <= 0f)
                        {
                            continue;
                        }

                        if (tri.IsEmissive)
                        {
                            pixels[pIdx + 0] =
                                (byte)Math.Clamp(pixels[pIdx + 0] + fr * 0.5f - pixels[pIdx + 0] * fr * 0.5f / 255f, 0,
                                    255);
                            pixels[pIdx + 1] =
                                (byte)Math.Clamp(pixels[pIdx + 1] + fg * 0.5f - pixels[pIdx + 1] * fg * 0.5f / 255f, 0,
                                    255);
                            pixels[pIdx + 2] =
                                (byte)Math.Clamp(pixels[pIdx + 2] + fb * 0.5f - pixels[pIdx + 2] * fb * 0.5f / 255f, 0,
                                    255);
                        }
                        else
                        {
                            var srcR2 = Math.Clamp(fr / 255f, 0f, 1f);
                            var srcG2 = Math.Clamp(fg / 255f, 0f, 1f);
                            var srcB2 = Math.Clamp(fb / 255f, 0f, 1f);
                            var dstR2 = pixels[pIdx + 0] / 255f;
                            var dstG2 = pixels[pIdx + 1] / 255f;
                            var dstB2 = pixels[pIdx + 2] / 255f;

                            pixels[pIdx + 0] = (byte)Math.Clamp(
                                fr * ResolveBlendFactor(tri.SrcBlendMode, srcA, srcR2, srcG2, srcB2, dstR2, dstG2,
                                    dstB2, 0) +
                                pixels[pIdx + 0] * ResolveBlendFactor(tri.DstBlendMode, srcA, srcR2, srcG2, srcB2,
                                    dstR2, dstG2, dstB2, 0), 0, 255);
                            pixels[pIdx + 1] = (byte)Math.Clamp(
                                fg * ResolveBlendFactor(tri.SrcBlendMode, srcA, srcR2, srcG2, srcB2, dstR2, dstG2,
                                    dstB2, 1) +
                                pixels[pIdx + 1] * ResolveBlendFactor(tri.DstBlendMode, srcA, srcR2, srcG2, srcB2,
                                    dstR2, dstG2, dstB2, 1), 0, 255);
                            pixels[pIdx + 2] = (byte)Math.Clamp(
                                fb * ResolveBlendFactor(tri.SrcBlendMode, srcA, srcR2, srcG2, srcB2, dstR2, dstG2,
                                    dstB2, 2) +
                                pixels[pIdx + 2] * ResolveBlendFactor(tri.DstBlendMode, srcA, srcR2, srcG2, srcB2,
                                    dstR2, dstG2, dstB2, 2), 0, 255);
                        }

                        pixels[pIdx + 3] = Math.Max(pixels[pIdx + 3], (byte)Math.Clamp(srcA * 255f, 0f, 255f));
                    }
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

    internal static (byte R, byte G, byte B, byte A) SampleTexture(
        DecodedTexture tex,
        float u,
        float v,
        float duDx,
        float dvDx,
        float duDy,
        float dvDy)
    {
        var mipLevel = SelectMipLevel(tex, duDx, dvDx, duDy, dvDy);
        return NifSpriteRenderer.DisableBilinear
            ? SampleNearest(tex.GetMipLevel(mipLevel), u, v)
            : SampleBilinear(tex.GetMipLevel(mipLevel), u, v);
    }

    internal static int SelectMipLevel(
        DecodedTexture tex,
        float duDx,
        float dvDx,
        float duDy,
        float dvDy)
    {
        if (tex.MipCount <= 1)
        {
            return 0;
        }

        var rhoX = MathF.Sqrt(
            duDx * duDx * tex.Width * tex.Width +
            dvDx * dvDx * tex.Height * tex.Height);
        var rhoY = MathF.Sqrt(
            duDy * duDy * tex.Width * tex.Width +
            dvDy * dvDy * tex.Height * tex.Height);
        var rho = MathF.Max(rhoX, rhoY);

        if (rho <= 1f)
        {
            return 0;
        }

        var lod = MathF.Log2(rho);
        return Math.Clamp((int)MathF.Round(lod), 0, tex.MipCount - 1);
    }

    internal static (float DuDx, float DuDy, float DvDx, float DvDy) ComputeUvGradients(
        TriangleData tri,
        float sx0,
        float sy0,
        float sx1,
        float sy1,
        float sx2,
        float sy2,
        float invDenom)
    {
        var duDx = (
            tri.U0 * (sy1 - sy2) +
            tri.U1 * (sy2 - sy0) +
            tri.U2 * (sy0 - sy1)) * invDenom;
        var duDy = (
            tri.U0 * (sx2 - sx1) +
            tri.U1 * (sx0 - sx2) +
            tri.U2 * (sx1 - sx0)) * invDenom;
        var dvDx = (
            tri.V0 * (sy1 - sy2) +
            tri.V1 * (sy2 - sy0) +
            tri.V2 * (sy0 - sy1)) * invDenom;
        var dvDy = (
            tri.V0 * (sx2 - sx1) +
            tri.V1 * (sx0 - sx2) +
            tri.V2 * (sx1 - sx0)) * invDenom;

        return (duDx, duDy, dvDx, dvDy);
    }

    internal static (byte R, byte G, byte B, byte A) SampleNearest(DecodedTexture tex, float u, float v)
    {
        return SampleNearest(tex.GetMipLevel(0), u, v);
    }

    internal static (byte R, byte G, byte B, byte A) SampleBilinear(DecodedTexture tex, float u, float v)
    {
        return SampleBilinear(tex.GetMipLevel(0), u, v);
    }

    internal static void DrawTriangleWireframeOverlay(
        byte[] pixels,
        float[] depthBuffer,
        int width,
        int height,
        TriangleData tri,
        float ppu,
        float offsetX,
        float offsetY)
    {
        var sx0 = tri.X0 * ppu + offsetX;
        var sy0 = tri.Y0 * ppu + offsetY;
        var sx1 = tri.X1 * ppu + offsetX;
        var sy1 = tri.Y1 * ppu + offsetY;
        var sx2 = tri.X2 * ppu + offsetX;
        var sy2 = tri.Y2 * ppu + offsetY;

        var color = ResolveWireframeColor(tri.RenderOrder);

        DrawWireframeEdge(
            pixels,
            depthBuffer,
            width,
            height,
            sx0,
            sy0,
            tri.Z0,
            sx1,
            sy1,
            tri.Z1,
            color.R,
            color.G,
            color.B);
        DrawWireframeEdge(
            pixels,
            depthBuffer,
            width,
            height,
            sx1,
            sy1,
            tri.Z1,
            sx2,
            sy2,
            tri.Z2,
            color.R,
            color.G,
            color.B);
        DrawWireframeEdge(
            pixels,
            depthBuffer,
            width,
            height,
            sx2,
            sy2,
            tri.Z2,
            sx0,
            sy0,
            tri.Z0,
            color.R,
            color.G,
            color.B);
    }

    private static (byte R, byte G, byte B, byte A) SampleNearest(
        DecodedTextureMipLevel mipLevel,
        float u,
        float v)
    {
        var x = ((int)(u * mipLevel.Width) % mipLevel.Width + mipLevel.Width) % mipLevel.Width;
        var y = ((int)(v * mipLevel.Height) % mipLevel.Height + mipLevel.Height) % mipLevel.Height;
        var i = (y * mipLevel.Width + x) * 4;
        return (
            mipLevel.Pixels[i],
            mipLevel.Pixels[i + 1],
            mipLevel.Pixels[i + 2],
            mipLevel.Pixels[i + 3]);
    }

    private static (byte R, byte G, byte B, byte A) SampleBilinear(
        DecodedTextureMipLevel mipLevel,
        float u,
        float v)
    {
        var fx = u * mipLevel.Width - 0.5f;
        var fy = v * mipLevel.Height - 0.5f;

        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var fracX = fx - x0;
        var fracY = fy - y0;

        // Wrap coordinates for tiling
        var x1 = x0 + 1;
        x0 = (x0 % mipLevel.Width + mipLevel.Width) % mipLevel.Width;
        x1 = (x1 % mipLevel.Width + mipLevel.Width) % mipLevel.Width;
        y0 = (y0 % mipLevel.Height + mipLevel.Height) % mipLevel.Height;
        var y1 = (y0 + 1) % mipLevel.Height;

        // Fetch 4 texels
        var i00 = (y0 * mipLevel.Width + x0) * 4;
        var i10 = (y0 * mipLevel.Width + x1) * 4;
        var i01 = (y1 * mipLevel.Width + x0) * 4;
        var i11 = (y1 * mipLevel.Width + x1) * 4;

        var p = mipLevel.Pixels;
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
    ///     Resolve a D3D blend factor enum value to a per-channel multiplier.
    ///     Matches SetupGeometryAlphaBlending (VA 0x82AAD430) blend mode extraction.
    ///     <paramref name="channel" />: 0=R, 1=G, 2=B for SRC_COLOR/DST_COLOR modes.
    /// </summary>
    internal static float ResolveBlendFactor(
        byte mode, float srcAlpha,
        float srcR, float srcG, float srcB,
        float dstR, float dstG, float dstB,
        int channel)
    {
        return mode switch
        {
            0 => 1f, // ONE
            1 => 0f, // ZERO
            2 => channel switch { 0 => srcR, 1 => srcG, _ => srcB }, // SRC_COLOR
            3 => channel switch { 0 => 1f - srcR, 1 => 1f - srcG, _ => 1f - srcB }, // INV_SRC_COLOR
            4 => channel switch { 0 => dstR, 1 => dstG, _ => dstB }, // DST_COLOR
            5 => channel switch { 0 => 1f - dstR, 1 => 1f - dstG, _ => 1f - dstB }, // INV_DST_COLOR
            6 => srcAlpha, // SRC_ALPHA
            7 => 1f - srcAlpha, // INV_SRC_ALPHA
            8 => Math.Min(srcAlpha, 1f - srcAlpha), // DST_ALPHA (approx)
            9 => 1f - Math.Min(srcAlpha, 1f - srcAlpha), // INV_DST_ALPHA (approx)
            _ => srcAlpha // Fallback to SRC_ALPHA behavior
        };
    }

    private static void DrawWireframeEdge(
        byte[] pixels,
        float[] depthBuffer,
        int width,
        int height,
        float x0,
        float y0,
        float z0,
        float x1,
        float y1,
        float z1,
        byte r,
        byte g,
        byte b)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var steps = Math.Max(1, (int)MathF.Ceiling(MathF.Max(MathF.Abs(dx), MathF.Abs(dy))));

        for (var i = 0; i <= steps; i++)
        {
            var t = i / (float)steps;
            var px = (int)MathF.Round(x0 + dx * t);
            var py = (int)MathF.Round(y0 + dy * t);
            var z = z0 + (z1 - z0) * t;

            PlotWireframePixel(pixels, depthBuffer, width, height, px, py, z, r, g, b);
        }
    }

    private static void PlotWireframePixel(
        byte[] pixels,
        float[] depthBuffer,
        int width,
        int height,
        int px,
        int py,
        float z,
        byte r,
        byte g,
        byte b)
    {
        for (var oy = -1; oy <= 1; oy++)
        {
            for (var ox = -1; ox <= 1; ox++)
            {
                var tx = px + ox;
                var ty = py + oy;
                if ((uint)tx >= (uint)width || (uint)ty >= (uint)height)
                {
                    continue;
                }

                if (!IsNearFrontmostDepth(depthBuffer, width, height, tx, ty, z))
                {
                    continue;
                }

                BlendWireframePixel(pixels, width, tx, ty, r, g, b);
            }
        }
    }

    private static bool IsNearFrontmostDepth(
        float[] depthBuffer,
        int width,
        int height,
        int px,
        int py,
        float z)
    {
        var maxDepth = float.MinValue;

        for (var oy = -1; oy <= 1; oy++)
        {
            var ty = py + oy;
            if ((uint)ty >= (uint)height)
            {
                continue;
            }

            for (var ox = -1; ox <= 1; ox++)
            {
                var tx = px + ox;
                if ((uint)tx >= (uint)width)
                {
                    continue;
                }

                var depth = depthBuffer[ty * width + tx];
                if (depth > maxDepth)
                {
                    maxDepth = depth;
                }
            }
        }

        return maxDepth is float.MinValue || z >= maxDepth - WireframeDepthEpsilon;
    }

    private static void BlendWireframePixel(
        byte[] pixels,
        int width,
        int px,
        int py,
        byte r,
        byte g,
        byte b)
    {
        var idx = (py * width + px) * 4;
        const float overlay = 0.85f;
        const float baseWeight = 1f - overlay;

        pixels[idx + 0] = (byte)Math.Clamp(pixels[idx + 0] * baseWeight + r * overlay, 0f, 255f);
        pixels[idx + 1] = (byte)Math.Clamp(pixels[idx + 1] * baseWeight + g * overlay, 0f, 255f);
        pixels[idx + 2] = (byte)Math.Clamp(pixels[idx + 2] * baseWeight + b * overlay, 0f, 255f);
        pixels[idx + 3] = 255;
    }

    private static (byte R, byte G, byte B) ResolveWireframeColor(int renderOrder)
    {
        return renderOrder switch
        {
            2 => (0, 255, 255),
            1 => (255, 220, 0),
            _ => (0, 255, 0)
        };
    }
}

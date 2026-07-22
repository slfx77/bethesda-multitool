namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Rasterization;

/// <summary>
///     Scanline triangle rasterizer with per-pixel Z-buffer, texture mapping, bump mapping,
///     alpha blending, and vertex color support. Extracted from <see cref="NifSpriteRenderer" />.
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
        // Project to screen coordinates (top-down: X->screenX, Y->screenY)
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

        // Precompute edge function denominators
        var denom = (sy1 - sy2) * (sx0 - sx2) + (sx2 - sx1) * (sy0 - sy2);
        if (MathF.Abs(denom) < 0.0001f)
        {
            return; // Degenerate triangle
        }

        // Backface culling
        var isBackFacing = denom > 0;
        if (isBackFacing && !tri.IsDoubleSided)
        {
            return;
        }

        var invDenom = 1f / denom;
        var windingSign = denom > 0f ? 1f : -1f;
        var edge0IsTopLeft = IsTopLeftEdge(sx1, sy1, sx2, sy2, windingSign);
        var edge1IsTopLeft = IsTopLeftEdge(sx2, sy2, sx0, sy0, windingSign);
        var edge2IsTopLeft = IsTopLeftEdge(sx0, sy0, sx1, sy1, windingSign);
        var tex = tri.Texture;
        var normalMap = tri.NormalMap;
        var hasUvGradients = tri.Texture != null || tri.NormalMap != null;
        float duDx = 0f, duDy = 0f, dvDx = 0f, dvDy = 0f;
        if (hasUvGradients)
        {
            (duDx, duDy, dvDx, dvDy) = NifTextureSampler.ComputeUvGradients(
                tri, sx0, sy0, sx1, sy1, sx2, sy2, invDenom);
        }

        // Rasterize using barycentric coordinates
        for (var py = minPy; py <= maxPy; py++)
        {
            for (var px = minPx; px <= maxPx; px++)
            {
                var cx = px + 0.5f;
                var cy = py + 0.5f;

                // Apply the hardware top-left fill convention before normalizing the edge
                // functions. Adjacent alpha-blended triangles otherwise both shade samples on
                // their shared edge, producing a visibly darker/denser diagonal through cards.
                var edge0 = (sy1 - sy2) * (cx - sx2) + (sx2 - sx1) * (cy - sy2);
                var edge1 = (sy2 - sy0) * (cx - sx2) + (sx0 - sx2) * (cy - sy2);
                var edge2 = (sy0 - sy1) * (cx - sx1) + (sx1 - sx0) * (cy - sy1);

                if (!CoversSample(edge0 * windingSign, edge0IsTopLeft) ||
                    !CoversSample(edge1 * windingSign, edge1IsTopLeft) ||
                    !CoversSample(edge2 * windingSign, edge2IsTopLeft))
                {
                    continue;
                }

                var w0 = edge0 * invDenom;
                var w1 = edge1 * invDenom;
                var w2 = edge2 * invDenom;

                // Interpolate Z for depth test
                var z = tri.Z0 * w0 + tri.Z1 * w1 + tri.Z2 * w2;
                var idx = py * width + px;

                var writesDepth = tri.AlphaRenderMode != NifAlphaRenderMode.Blend;

                var passesDepthTest = z > depthBuffer[idx];
                var frontOverBack = writesDepth && !isBackFacing && faceKind[idx] == 2;
                var backBlockedByFront = writesDepth && isBackFacing && faceKind[idx] == 1;

                if (backBlockedByFront || (!passesDepthTest && !frontOverBack))
                {
                    continue;
                }

                var pIdx = idx * 4;

                float shade;
                var specIntensity = 0f;
                var normalAlpha = 255f;
                float nx = 0, ny = 0, nz = 1;

                if (tri.IsEmissive)
                {
                    shade = 1.0f;
                }
                else if (tri.HasVertexNormals)
                {
                    nx = tri.Nx0 * w0 + tri.Nx1 * w1 + tri.Nx2 * w2;
                    ny = tri.Ny0 * w0 + tri.Ny1 * w1 + tri.Ny2 * w2;
                    nz = tri.Nz0 * w0 + tri.Nz1 * w1 + tri.Nz2 * w2;

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

                        var (mnr, mng, mnb, mna) = NifTextureSampler.SampleTexture(
                            normalMap, u, v, duDx, dvDx, duDy, dvDy,
                            tri.ClampTextureU, tri.ClampTextureV);

                        normalAlpha = mna;

                        var mapNx = (mnr / 127.5f - 1f) * NifSpriteRenderer.BumpStrength;
                        // Bethesda DDS normals and the retained NIF TBN share DirectX convention.
                        // Recovered FNV SLS1009 consumes green directly; only glTF export flips it.
                        var mapNy = (mng / 127.5f - 1f) * NifSpriteRenderer.BumpStrength;
                        var mapNz = mnb / 127.5f - 1f;

                        float tx, ty, tz, bx, by, bz;
                        if (tri.HasTangents)
                        {
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

                    if (tri.IsEyeEnvmap)
                    {
                        var specNdotH = MathF.Max(0f,
                            nx * RenderLightingConstants.HalfVec.X +
                            ny * RenderLightingConstants.HalfVec.Y +
                            nz * RenderLightingConstants.HalfVec.Z);
                        shade = MathF.Min(shade + MathF.Pow(specNdotH, 4f) * tri.EnvMapScale * 0.25f, 1f);
                    }
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
                    RasterizeTexturedPixel(
                        pixels, depthBuffer, faceKind,
                        tri, w0, w1, w2, z, idx, pIdx, px, py,
                        shade, specIntensity, isBackFacing,
                        duDx, dvDx, duDy, dvDy);
                }
                else
                {
                    RasterizeUntexturedPixel(
                        pixels, depthBuffer, faceKind,
                        tri, w0, w1, w2, z, idx, pIdx, px, py,
                        shade, isBackFacing);
                }

                emissiveMask[idx] = tri.IsEmissive;
            }
        }
    }

    private static bool CoversSample(float normalizedEdge, bool isTopLeft)
    {
#pragma warning disable S1244 // top-left fill rule: an exactly-zero edge function marks the on-edge sample; exact comparison intended
        return normalizedEdge > 0f || (normalizedEdge == 0f && isTopLeft);
#pragma warning restore S1244
    }

    private static bool IsTopLeftEdge(
        float ax,
        float ay,
        float bx,
        float by,
        float windingSign)
    {
        // Canonicalize both windings first. In the renderer's Y-down screen space, top edges
        // run left-to-right and left edges run bottom-to-top.
        var dx = (bx - ax) * windingSign;
        var dy = (by - ay) * windingSign;
#pragma warning disable S1244 // top-left fill rule: an exactly-horizontal edge is the dy == 0 case; exact comparison intended
        return dy < 0f || (dy == 0f && dx > 0f);
#pragma warning restore S1244
    }

    private static void RasterizeTexturedPixel(
        byte[] pixels, float[] depthBuffer, byte[] faceKind,
        TriangleData tri, float w0, float w1, float w2,
        float z, int idx, int pIdx, int px, int py,
        float shade, float specIntensity, bool isBackFacing,
        float duDx, float dvDx, float duDy, float dvDy)
    {
        var tex = tri.Texture!;
        var u = tri.U0 * w0 + tri.U1 * w1 + tri.U2 * w2;
        var v = tri.V0 * w0 + tri.V1 * w1 + tri.V2 * w2;

        var (r, g, b, a) = NifSpriteRenderer.DisableTextures
            ? ((byte)200, (byte)200, (byte)200, (byte)255)
            : NifTextureSampler.SampleTexture(
                tex, u, v, duDx, dvDx, duDy, dvDy,
                tri.ClampTextureU, tri.ClampTextureV);

        if (tri.HasVertexColors)
        {
            var vca = tri.A0 * w0 + tri.A1 * w1 + tri.A2 * w2;
            a = (byte)Math.Clamp(a * vca / 255f, 0, 255);
        }

        if (tri.AlphaRenderMode == NifAlphaRenderMode.AlphaToCoverage)
        {
            // Stochastic alpha test: pass = opaque write, fail = discard. The Bayer 2x2 dither
            // gives 4 distinct thresholds per output pixel under SsaaFactor=2 supersampling,
            // matching what GPU 4x MSAA + A2C does at the hardware level.
            if (a <= StochasticAlphaThreshold(px, py)) return;
        }
        else if (tri.HasAlphaTest && tri.AlphaRenderMode != NifAlphaRenderMode.Blend)
        {
            var pass = tri.AlphaTestFunction switch
            {
                0 => true,
                1 => a < tri.AlphaTestThreshold,
                2 => a == tri.AlphaTestThreshold,
                3 => a <= tri.AlphaTestThreshold,
                4 => a > tri.AlphaTestThreshold,
                5 => a != tri.AlphaTestThreshold,
                6 => a >= tri.AlphaTestThreshold,
                _ => false
            };
            if (!pass) return;
        }
        else if (tri.HasAlphaBlend && a == 0)
        {
            return;
        }

        float fr, fg, fb;
        if (tri.HasTintColor)
        {
            var tintShadeR = 2f * tri.TintR;
            var tintShadeG = 2f * tri.TintG;
            var tintShadeB = 2f * tri.TintB;
            fr = r * tintShadeR * shade;
            fg = g * tintShadeG * shade;
            fb = b * tintShadeB * shade;
        }
        else
        {
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

        if (tri.HasEffectTint)
        {
            fr *= tri.EffectTintR;
            fg *= tri.EffectTintG;
            fb *= tri.EffectTintB;
        }

        if (tri.EmissiveR > 0f || tri.EmissiveG > 0f || tri.EmissiveB > 0f)
        {
            fr += tri.EmissiveR * 255f;
            fg += tri.EmissiveG * 255f;
            fb += tri.EmissiveB * 255f;
        }

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
            depthBuffer[idx] = z;
            faceKind[idx] = fk;
            pixels[pIdx + 0] = (byte)Math.Clamp(fr, 0, 255);
            pixels[pIdx + 1] = (byte)Math.Clamp(fg, 0, 255);
            pixels[pIdx + 2] = (byte)Math.Clamp(fb, 0, 255);
            pixels[pIdx + 3] = 255;
        }
        else
        {
            var srcA = ComputeBlendedOpacity(a, tri.MaterialAlpha);
            if (srcA <= 0f)
            {
                return;
            }

            // Emissive changes the source lighting term, not the fixed-function blend state.  The old
            // CPU path replaced every emissive blend with an invented half-strength screen operation,
            // making CPU previews disagree with both the authored NiAlphaProperty and the GPU renderers.
            var srcR = Math.Clamp(fr / 255f, 0f, 1f);
            var srcG = Math.Clamp(fg / 255f, 0f, 1f);
            var srcB = Math.Clamp(fb / 255f, 0f, 1f);
            var dstR = pixels[pIdx + 0] / 255f;
            var dstG = pixels[pIdx + 1] / 255f;
            var dstB = pixels[pIdx + 2] / 255f;
            var dstA = pixels[pIdx + 3] / 255f;

            pixels[pIdx + 0] = (byte)Math.Clamp(
                fr * NifTextureSampler.ResolveBlendFactor(tri.SrcBlendMode, srcA, dstA, srcR, srcG, srcB, dstR, dstG,
                    dstB, 0) +
                pixels[pIdx + 0] * NifTextureSampler.ResolveBlendFactor(tri.DstBlendMode, srcA, dstA, srcR, srcG, srcB,
                    dstR, dstG, dstB, 0), 0, 255);
            pixels[pIdx + 1] = (byte)Math.Clamp(
                fg * NifTextureSampler.ResolveBlendFactor(tri.SrcBlendMode, srcA, dstA, srcR, srcG, srcB, dstR, dstG,
                    dstB, 1) +
                pixels[pIdx + 1] * NifTextureSampler.ResolveBlendFactor(tri.DstBlendMode, srcA, dstA, srcR, srcG, srcB,
                    dstR, dstG, dstB, 1), 0, 255);
            pixels[pIdx + 2] = (byte)Math.Clamp(
                fb * NifTextureSampler.ResolveBlendFactor(tri.SrcBlendMode, srcA, dstA, srcR, srcG, srcB, dstR, dstG,
                    dstB, 2) +
                pixels[pIdx + 2] * NifTextureSampler.ResolveBlendFactor(tri.DstBlendMode, srcA, dstA, srcR, srcG, srcB,
                    dstR, dstG, dstB, 2), 0, 255);

            var outAlpha = tri.DstBlendMode == 0
                ? (byte)Math.Clamp(srcA * 64f, 0f, 255f)
                : (byte)Math.Clamp(srcA * 255f, 0f, 255f);
            pixels[pIdx + 3] = Math.Max(pixels[pIdx + 3], outAlpha);
        }
    }

    private static void RasterizeUntexturedPixel(
        byte[] pixels, float[] depthBuffer, byte[] faceKind,
        TriangleData tri, float w0, float w1, float w2,
        float z, int idx, int pIdx, int px, int py,
        float shade, bool isBackFacing)
    {
        var brightness = (byte)(shade * 220 + 35);
        var fk = isBackFacing ? (byte)2 : (byte)1;
        var vertexAlpha = tri.HasVertexColors
            ? Math.Clamp(tri.A0 * w0 + tri.A1 * w1 + tri.A2 * w2, 0f, 255f)
            : 255f;

        if (tri.AlphaRenderMode == NifAlphaRenderMode.AlphaToCoverage)
        {
            if (vertexAlpha <= StochasticAlphaThreshold(px, py)) return;
        }
        else if (tri.HasAlphaTest)
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
                return;
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
            var srcA = ComputeBlendedOpacity(vertexAlpha, tri.MaterialAlpha);
            if (srcA <= 0f)
            {
                return;
            }

            var srcR2 = Math.Clamp(fr / 255f, 0f, 1f);
            var srcG2 = Math.Clamp(fg / 255f, 0f, 1f);
            var srcB2 = Math.Clamp(fb / 255f, 0f, 1f);
            var dstR2 = pixels[pIdx + 0] / 255f;
            var dstG2 = pixels[pIdx + 1] / 255f;
            var dstB2 = pixels[pIdx + 2] / 255f;
            var dstA2 = pixels[pIdx + 3] / 255f;

            pixels[pIdx + 0] = (byte)Math.Clamp(
                fr * NifTextureSampler.ResolveBlendFactor(tri.SrcBlendMode, srcA, dstA2, srcR2, srcG2, srcB2, dstR2, dstG2,
                    dstB2, 0) +
                pixels[pIdx + 0] * NifTextureSampler.ResolveBlendFactor(tri.DstBlendMode, srcA, dstA2, srcR2, srcG2, srcB2,
                    dstR2, dstG2, dstB2, 0), 0, 255);
            pixels[pIdx + 1] = (byte)Math.Clamp(
                fg * NifTextureSampler.ResolveBlendFactor(tri.SrcBlendMode, srcA, dstA2, srcR2, srcG2, srcB2, dstR2, dstG2,
                    dstB2, 1) +
                pixels[pIdx + 1] * NifTextureSampler.ResolveBlendFactor(tri.DstBlendMode, srcA, dstA2, srcR2, srcG2, srcB2,
                    dstR2, dstG2, dstB2, 1), 0, 255);
            pixels[pIdx + 2] = (byte)Math.Clamp(
                fb * NifTextureSampler.ResolveBlendFactor(tri.SrcBlendMode, srcA, dstA2, srcR2, srcG2, srcB2, dstR2, dstG2,
                    dstB2, 2) +
                pixels[pIdx + 2] * NifTextureSampler.ResolveBlendFactor(tri.DstBlendMode, srcA, dstA2, srcR2, srcG2, srcB2,
                    dstR2, dstG2, dstB2, 2), 0, 255);

            pixels[pIdx + 3] = Math.Max(pixels[pIdx + 3], (byte)Math.Clamp(srcA * 255f, 0f, 255f));
        }
    }

    /// <summary>
    ///     Apply authored material alpha before saturating the blend factor. Effect materials may
    ///     deliberately author alpha above one; clipping the material first prevented it from
    ///     amplifying a partially transparent texture/vertex envelope.
    /// </summary>
    internal static float ComputeBlendedOpacity(float sourceAlphaByte, float materialAlpha) =>
        Math.Clamp(sourceAlphaByte * materialAlpha / 255f, 0f, 1f);

    /// <summary>
    ///     Bayer 2x2 dither thresholds (half-centered, bytes). Each of the four supersamples
    ///     within an output pixel (under <see cref="RenderLightingConstants.SsaaFactor" />=2)
    ///     gets a distinct threshold, so a fragment's alpha translates into one of five
    ///     coverage levels after the box downsample — matching what hardware 4x MSAA + A2C
    ///     produces on the GPU path. Pattern is screen-space-stable so the noise stays still
    ///     between frames.
    /// </summary>
    private static readonly byte[] BayerThresholds2X2 = [32, 159, 223, 96];

    private static byte StochasticAlphaThreshold(int px, int py)
    {
        return BayerThresholds2X2[((py & 1) << 1) | (px & 1)];
    }
}

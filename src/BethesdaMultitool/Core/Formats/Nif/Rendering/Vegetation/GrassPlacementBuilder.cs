using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Vegetation;

/// <summary>
///     Builds the synthetic NIF placements used to render LAND/GRAS grass. The layout follows
///     <c>BGSGrassManager::AddCellGrass/CreateGrass</c>: four LAND quadrants, regularly spaced
///     evaluation chunks, 3x3 texture-presence density maps, one jittered candidate per bin,
///     water/slope gates, generation-specific orientation, fit-to-slope, and GRAS height-range
///     scaling.
/// </summary>
internal static class GrassPlacementBuilder
{
    private const int LandGridSize = 33;
    private const int QuadrantLastVertex = 16;
    private const int MaxGrassParametersPerChunk = 16;
    private const byte GrassFlagUniformScale = 0x02;
    private const byte GrassFlagFitToSlope = 0x04;
    private const float FnvPackedNormalComponentMaximum = 0.94f;
    private const float FnvHeightPackingMultiplier = 100f;
    private const float FnvHeightScaleStep = 0.01f;

    // Fallout 4 first rejects an alpha layer unless one of the surrounding 3x3 percentage bytes is
    // > 0x19. This is independent of fTexturePctThreshold (which defaults to zero).
    private const float AlphaLayerActivationThreshold = 25f / 255f;

    internal static IReadOnlyList<RenderableReference> Build(
        CellRecord cell,
        float[] heights,
        IReadOnlyDictionary<uint, LandscapeTextureRecord> landTextures,
        IReadOnlyDictionary<uint, GrassRecord> grasses,
        BethesdaGame game,
        float? effectiveWaterHeight)
    {
        var profile = GrassScatterProfile.ForGame(game);
        var layers = cell.LandVisualData?.TextureLayers;
        if (!profile.Supported || heights.Length != LandGridSize * LandGridSize ||
            layers is not { Count: > 0 } || cell.GridX is not int gx || cell.GridY is not int gy ||
            landTextures.Count == 0 || grasses.Count == 0)
        {
            return Array.Empty<RenderableReference>();
        }

        var cellSize = cell.CellWorldSize > 0f ? cell.CellWorldSize : WorldGridConstants.CellSize;
        var spacing = cellSize / (LandGridSize - 1);
        var originX = gx * cellSize;
        var originY = gy * cellSize;
        var weights = CellLayerWeightTable.Build(LandGridSize, layers);
        if (weights is null) return Array.Empty<RenderableReference>();

        var result = new List<RenderableReference>(512);
        var alphaLayers = new List<LandTextureLayer>(8);
        var quadrantLayers = new List<(LandTextureLayer Layer, bool IsAlpha)>(9);
        Span<float> textureWeights = stackalloc float[9];
        Span<float> density = stackalloc float[9];
        // FO3 and FNV share TallGrassShader.cpp (Platforms\Common) and every shipped GRAS record in
        // both games authors the same DATA flags (0x06), so both take the engine placement
        // transform and the recovered lighting bake.
        var useFnvPlacementTransform =
            game is BethesdaGame.FalloutNewVegas or BethesdaGame.Fallout3;
        // Authored LAND inputs for the lighting bake (see FnvGrassLighting). Missing VCLR means the
        // engine's white default; missing VNML falls back to the sampled heightmap normal.
        var landVertexColors = cell.LandVisualData?.VertexColors;
        var landVertexNormals = cell.LandVisualData?.VertexNormals;

        for (var quadrant = 0; quadrant < 4; quadrant++)
        {
            var baseLayer = QuadrantLayerSelector.SelectBaseAndAlphas(layers, quadrant, alphaLayers);
            quadrantLayers.Clear();
            if (baseLayer is not null) quadrantLayers.Add((baseLayer, false));
            for (var i = 0; i < alphaLayers.Count; i++) quadrantLayers.Add((alphaLayers[i], true));

            if (quadrantLayers.Count == 0) continue;

            // Manager constructor: iGrassEvalRadius = TESObjectLAND::iGrassEvalSize;
            // uiEvalChunkRadius = iGrassEvalRadius << 7. AddCellGrass starts at the radius and steps
            // by twice it while the quadrant-local coordinate is < 16.
            var evalRadius = profile.EvalRadius;
            var chunkRadius = evalRadius * spacing;
            var chunkSpan = chunkRadius * 2f;
            for (var qx = evalRadius; qx < QuadrantLastVertex; qx += evalRadius * 2)
            {
                for (var qy = evalRadius; qy < QuadrantLastVertex; qy += evalRadius * 2)
                {
                    var centerX = ((quadrant & 1) != 0 ? QuadrantLastVertex : 0) + qx;
                    var centerY = ((quadrant & 2) != 0 ? QuadrantLastVertex : 0) + qy;
                    var chunkOriginX = originX + centerX * spacing - chunkRadius;
                    var chunkOriginY = originY + centerY * spacing - chunkRadius;
                    var grassParameterCount = 0;

                    for (var layerIndex = 0;
                         layerIndex < quadrantLayers.Count && grassParameterCount < MaxGrassParametersPerChunk;
                         layerIndex++)
                    {
                        var (layer, isAlpha) = quadrantLayers[layerIndex];
                        if (!landTextures.TryGetValue(layer.TextureFormId, out var landTexture) ||
                            landTexture.GrassFormIds.Count == 0)
                        {
                            continue;
                        }

                        PopulateTextureWeights(weights, layer.TextureFormId, centerX, centerY, textureWeights);
                        if (isAlpha && !AnyGreaterThan(textureWeights, AlphaLayerActivationThreshold))
                        {
                            continue;
                        }

                        var grassEntriesRemaining = profile.MaxGrassEntriesPerTexture;
                        for (var grassIndex = 0;
                             grassIndex < landTexture.GrassFormIds.Count &&
                             grassEntriesRemaining > 0 &&
                             grassParameterCount < MaxGrassParametersPerChunk;
                             grassIndex++)
                        {
                            if (!grasses.TryGetValue(landTexture.GrassFormIds[grassIndex], out var grass) ||
                                grass.Data is not { } data || string.IsNullOrWhiteSpace(grass.ModelPath))
                            {
                                continue;
                            }

                            var authoredDensity = data.Density * 0.01f;
                            var densitySum = 0f;
                            for (var sample = 0; sample < density.Length; sample++)
                            {
                                // GetGrassParameters uses texture percentage as a binary gate; it does
                                // not multiply density by the texture weight.
                                var value = textureWeights[sample] > profile.TexturePercentageThreshold
                                    ? authoredDensity
                                    : 0f;
                                density[sample] = value;
                                densitySum += value;
                            }

                            if ((densitySum / density.Length) < profile.TexturePercentageThreshold)
                            {
                                continue;
                            }

                            AppendGrassType(
                                result,
                                cell,
                                heights,
                                grass,
                                data,
                                density,
                                profile,
                                cellSize,
                                spacing,
                                originX,
                                originY,
                                chunkOriginX,
                                chunkOriginY,
                                chunkSpan,
                                effectiveWaterHeight,
                                quadrant,
                                centerX,
                                centerY,
                                layer.TextureFormId,
                                useFnvPlacementTransform,
                                useFnvPlacementTransform
                                    ? FnvTallGrassWind.SanitizeWaveMultiplier(data.WavePeriod)
                                    : 0f,
                                landVertexColors,
                                landVertexNormals);
                            grassEntriesRemaining--;
                            grassParameterCount++;
                        }
                    }
                }
            }
        }

        return result.Count == 0 ? Array.Empty<RenderableReference>() : result;
    }

    private static void AppendGrassType(
        List<RenderableReference> destination,
        CellRecord cell,
        float[] heights,
        GrassRecord grass,
        GrassData data,
        ReadOnlySpan<float> density,
        GrassScatterProfile profile,
        float cellSize,
        float spacing,
        float originX,
        float originY,
        float chunkOriginX,
        float chunkOriginY,
        float chunkSpan,
        float? effectiveWaterHeight,
        int quadrant,
        int centerX,
        int centerY,
        uint textureFormId,
        bool useFnvPlacementTransform,
        float grassWaveMultiplier,
        byte[]? landVertexColors,
        byte[]? landVertexNormals)
    {
        var positionRangeCount = data.PositionRange > 0f
            ? (int)(chunkSpan / data.PositionRange)
            : int.MaxValue;
        var minimumSizeCount = (int)(chunkSpan / profile.MinGrassSize);
        var countPerAxis = Math.Min(positionRangeCount, minimumSizeCount);
        if (countPerAxis <= 0) return;

        var inverseCount = 1f / countPerAxis;
        var binSize = chunkSpan * inverseCount;
        var halfBin = binSize * 0.5f;
        var minSlopeCos = MathF.Cos(data.MinSlope * (MathF.PI / 180f));
        var maxSlopeCos = MathF.Cos(data.MaxSlope * (MathF.PI / 180f));
        var modelPath = grass.ModelPath!.Trim();
        var meshId = RenderableReference.ComputeMeshId(modelPath);

        for (var xIndex = 0; xIndex < countPerAxis; xIndex++)
        {
            var densityX = xIndex + 0.5f;
            for (var yIndex = 0; yIndex < countPerAxis; yIndex++)
            {
                var densityY = yIndex + 0.5f;
                var chance = SampleDensity(density, densityX * 2f * inverseCount, densityY * 2f * inverseCount);

                var candidateSeed = Seed(
                    cell.GridX!.Value,
                    cell.GridY!.Value,
                    quadrant,
                    centerX,
                    centerY,
                    textureFormId,
                    grass.FormId,
                    xIndex,
                    yIndex);
                if (chance <= 0f || Random01(ref candidateSeed) >= chance) continue;

                var x = chunkOriginX + densityX * binSize + (Random01(ref candidateSeed) * 2f - 1f) * halfBin;
                var y = chunkOriginY + densityY * binSize + (Random01(ref candidateSeed) * 2f - 1f) * halfBin;
                GrassPositionQuantizer.Quantize(
                    ref x,
                    ref y,
                    cell.GridX!.Value,
                    cell.GridY!.Value,
                    cellSize,
                    profile.PositionQuantization);
                if (!TrySampleTerrain(
                        heights,
                        x,
                        y,
                        originX,
                        originY,
                        cellSize,
                        spacing,
                        profile.TerrainTopology,
                        out var height,
                        out var normal))
                {
                    continue;
                }

                if (!PassesWaterGate(height, effectiveWaterHeight, data.UnitsFromWaterAmount,
                        data.UnitsFromWaterType))
                {
                    continue;
                }

                // Engine condition: cos(maxSlope) <= normal.z <= cos(minSlope).
                if (normal.Z < maxSlopeCos || normal.Z > minSlopeCos) continue;

                var uniformScale = (data.Flags & GrassFlagUniformScale) != 0;
                var fitToSlope = (data.Flags & GrassFlagFitToSlope) != 0;
                var quantizedHeight =
                    profile.PositionQuantization == GrassPositionQuantization.HalfRelativeToTwelveCellBlock
                        ? (float)(Half)height
                        : height;
                var instanceHeight = profile.FloorSampledHeight ? MathF.Floor(height) : quantizedHeight;
                var position = new Vector3(x, y, instanceHeight);
                float heightScale;
                Matrix4x4 world;
                if (useFnvPlacementTransform)
                {
                    // FO3/FNV's TallGrassShader receives one packed float4 per instance. AddGrass
                    // puts floor(signedRandom * HeightRange * 100) in W's integer component and
                    // every GRASS2000-2007 vertex permutation applies 1 + 0.01 * W * ScaleMask.
                    // Unlike the recovered Skyrim/FO4 CreateGrass paths, no FO3/FNV permutation
                    // consumes or applies a yaw value.
                    heightScale = ComputeFnvHeightScale(Random01(ref candidateSeed), data.HeightRange);

                    // W's FRACTIONAL component is the baked terrain light the GRASS vertex shaders
                    // remap to L = 0.25 + 0.75 * frac(W); the packed XYZ fractions carry the terrain
                    // normal the sun term is lit by. Both ride this instance's world-matrix w-lanes
                    // (see FnvGrassLighting + reference_grass_fnv.vert.hlsl). This random draw is
                    // APPENDED after the four existing draws (chance, x jitter, y jitter, height) so
                    // every previously placed blade keeps its exact position, height and basis.
                    var bakedLight = FnvGrassLighting.ComputeBakedLight(
                        Random01(ref candidateSeed),
                        data.ColorRange,
                        FnvGrassLighting.SampleLandLuminance(
                            landVertexColors, x - originX, y - originY, spacing));

                    // The engine lights with the LOADED land normal (authored VNML); the
                    // heightmap-derived normal remains the placement/slope-gate input so blade
                    // morphology is untouched.
                    var lightingNormal = FnvGrassLighting.SampleLandNormal(
                        landVertexNormals, x - originX, y - originY, spacing) ?? normal;

                    world = ComposeFnvWorldMatrix(
                        position, normal, fitToSlope, heightScale, uniformScale, lightingNormal, bakedLight);
                }
                else
                {
                    var yawCos = Random01(ref candidateSeed) * 2f - 1f;
                    var yawSin = MathF.Sqrt(MathF.Max(0f, 1f - yawCos * yawCos));
                    var heightVariation = (Random01(ref candidateSeed) * 2f - 1f) * data.HeightRange;
                    heightScale = MathF.Max(0.01f, 1f + heightVariation);
                    world = ComposeWorldMatrix(
                        position,
                        fitToSlope ? normal : Vector3.UnitZ,
                        yawCos,
                        yawSin,
                        heightScale,
                        uniformScale);
                }

                var authoredRadius = grass.ModelBound is > 0f ? grass.ModelBound.Value : 64f;
                var boundsScale = uniformScale ? heightScale : MathF.Max(1f, heightScale);
                destination.Add(new RenderableReference(
                    FormId: grass.FormId,
                    WorldMatrix: world,
                    ModelPath: modelPath,
                    BoundsCenter: new Vector3(x, y, instanceHeight),
                    BoundsRadius: MathF.Max(16f, authoredRadius * boundsScale),
                    MeshId: meshId,
                    IsInitiallyDisabled: false,
                    IsMarker: false,
                    IsImposter: false,
                    Category: PlacedObjectCategory.Plants,
                    AlternateTextures: null,
                    IsGrass: true,
                    GrassWaveMultiplier: grassWaveMultiplier));
            }
        }
    }

    private static void PopulateTextureWeights(
        CellLayerWeightTable weights,
        uint textureFormId,
        int centerX,
        int centerY,
        Span<float> destination)
    {
        var index = 0;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                // CellLayerWeightTable's row zero is north; LAND qy/world y grows north.
                var tableY = (LandGridSize - 1) - (centerY + dy);
                destination[index++] = WeightFor(weights.At(centerX + dx, tableY), textureFormId);
            }
        }
    }

    private static float WeightFor(VertexWeights weights, uint formId)
    {
        if (weights.Count > 0 && weights.E0.FormId == formId) return weights.E0.Weight;
        if (weights.Count > 1 && weights.E1.FormId == formId) return weights.E1.Weight;
        if (weights.Count > 2 && weights.E2.FormId == formId) return weights.E2.Weight;
        if (weights.Count > 3 && weights.E3.FormId == formId) return weights.E3.Weight;
        if (weights.Overflow is null) return 0f;
        var count = weights.Count - 4;
        for (var i = 0; i < count; i++)
        {
            if (weights.Overflow[i].FormId == formId) return weights.Overflow[i].Weight;
        }
        return 0f;
    }

    private static bool AnyGreaterThan(ReadOnlySpan<float> values, float threshold)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] > threshold) return true;
        }
        return false;
    }

    private static float SampleDensity(ReadOnlySpan<float> density, float x, float y)
    {
        var ix = Math.Clamp((int)x, 0, 1);
        var iy = Math.Clamp((int)y, 0, 1);
        var fx = x - ix;
        var fy = y - iy;
        var row = iy * 3 + ix;
        var top = density[row] * (1f - fx) + density[row + 1] * fx;
        var bottom = density[row + 3] * (1f - fx) + density[row + 4] * fx;
        return top * (1f - fy) + bottom * fy;
    }

    private static bool TrySampleTerrain(
        float[] heights,
        float worldX,
        float worldY,
        float originX,
        float originY,
        float cellSize,
        float spacing,
        TerrainTriangleTopology? terrainTopology,
        out float height,
        out Vector3 normal)
    {
        var localX = worldX - originX;
        var localY = worldY - originY;
        if (localX < 0f || localY < 0f || localX > cellSize || localY > cellSize)
        {
            height = 0f;
            normal = Vector3.UnitZ;
            return false;
        }

        if (terrainTopology is { } topology)
        {
            return TerrainSurfaceTopology.TrySampleTriangle(
                heights,
                LandGridSize,
                localX,
                localY,
                spacing,
                topology,
                out height,
                out normal);
        }

        var vx = localX / spacing;
        var vy = localY / spacing;
        var x0 = Math.Min((int)vx, LandGridSize - 2);
        var y0 = Math.Min((int)vy, LandGridSize - 2);
        var fx = vx - x0;
        var fy = vy - y0;
        var h00 = heights[y0 * LandGridSize + x0];
        var h10 = heights[y0 * LandGridSize + x0 + 1];
        var h01 = heights[(y0 + 1) * LandGridSize + x0];
        var h11 = heights[(y0 + 1) * LandGridSize + x0 + 1];
        var south = h00 * (1f - fx) + h10 * fx;
        var north = h01 * (1f - fx) + h11 * fx;
        height = south * (1f - fy) + north * fy;

        var dzdx = ((h10 - h00) * (1f - fy) + (h11 - h01) * fy) / spacing;
        var dzdy = ((h01 - h00) * (1f - fx) + (h11 - h10) * fx) / spacing;
        normal = Vector3.Normalize(new Vector3(-dzdx, -dzdy, 1f));
        return true;
    }

    private static bool PassesWaterGate(float height, float? waterHeight, ushort amount, uint waterState)
    {
        if (waterHeight is not (> -1e6f and < 1e6f)) return true;
        var water = waterHeight.Value;
        var distance = (float)amount;
        return waterState switch
        {
            // Direct transcription of Fallout 4 CreateGrass' six handled GWS cases.
            0 => height >= water - distance,
            1 => height >= water && height <= water + distance,
            2 => height <= water - distance,
            3 => height <= water && height >= water - distance,
            4 => height >= water + distance || height <= water - distance,
            5 => height <= water + distance && height >= water - distance,
            _ => true,
        };
    }

    private static Matrix4x4 ComposeWorldMatrix(
        Vector3 position,
        Vector3 up,
        float yawCos,
        float yawSin,
        float heightScale,
        bool uniformScale)
    {
        up = Vector3.Normalize(up);
        var helper = MathF.Abs(up.Z) < 0.999f ? Vector3.UnitZ : Vector3.UnitX;
        var tangent = Vector3.Normalize(Vector3.Cross(helper, up));
        var bitangent = Vector3.Normalize(Vector3.Cross(up, tangent));
        var xAxis = tangent * yawCos + bitangent * yawSin;
        var yAxis = tangent * -yawSin + bitangent * yawCos;

        var xyScale = uniformScale ? heightScale : 1f;
        xAxis *= xyScale;
        yAxis *= xyScale;
        up *= heightScale;
        return new Matrix4x4(
            xAxis.X, xAxis.Y, xAxis.Z, 0f,
            yAxis.X, yAxis.Y, yAxis.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            position.X, position.Y, position.Z, 1f);
    }

    /// <summary>
    ///     Replays the integer height component packed by FO3/FNV's
    ///     <c>TallGrassShaderProperty::AddGrass</c>. The fractional color/terrain-light component of
    ///     InstanceData.w is baked separately by <see cref="FnvGrassLighting.ComputeBakedLight" />
    ///     and rides the world matrix's w-lanes.
    /// </summary>
    internal static float ComputeFnvHeightScale(float random01, float heightRange)
    {
        var signedRandom = random01 * 2f - 1f;
        var packedHeight = MathF.Floor(signedRandom * heightRange * FnvHeightPackingMultiplier);
        return 1f + packedHeight * FnvHeightScaleStep;
    }

    /// <summary>
    ///     CPU equivalent of the fit-to-slope transform in FNV's GRASS2002/2003/2006/2007 vertex
    ///     shaders. CreateGrass stores each normal component as min((N + 1) / 2, 0.97) in the
    ///     fractional XYZ packet, so the shader decodes min(N, 0.94). It then chooses the axis whose
    ///     cross product is safest, normalizes only T, and maps local XYZ to B/T/N. There is no yaw.
    /// </summary>
    internal static Matrix4x4 ComposeFnvWorldMatrix(
        Vector3 position,
        Vector3 terrainNormal,
        bool fitToSlope,
        float heightScale,
        bool uniformScale,
        Vector3 lightingNormal,
        float bakedLight)
    {
        var xAxis = Vector3.UnitX;
        var yAxis = Vector3.UnitY;
        var zAxis = Vector3.UnitZ;
        if (fitToSlope)
        {
            var normal = new Vector3(
                MathF.Min(terrainNormal.X, FnvPackedNormalComponentMaximum),
                MathF.Min(terrainNormal.Y, FnvPackedNormalComponentMaximum),
                MathF.Min(terrainNormal.Z, FnvPackedNormalComponentMaximum));
            var absolute = Vector3.Abs(normal);

            // GRASS2002 uses sge(abs.yz, abs.x), including ties. When X is the smallest
            // component it selects X cross N; otherwise it selects N cross Y.
            var tangent = absolute.Y >= absolute.X && absolute.Z >= absolute.X
                ? new Vector3(0f, -normal.Z, normal.Y)
                : new Vector3(-normal.Z, 0f, normal.X);
            tangent = Vector3.Normalize(tangent);

            zAxis = normal;
            yAxis = tangent;
            xAxis = Vector3.Cross(tangent, normal);
        }

        var xyScale = uniformScale ? heightScale : 1f;
        xAxis *= xyScale;
        yAxis *= xyScale;
        zAxis *= heightScale;

        // The engine's per-instance lighting payload rides the otherwise-unused w-lanes: the
        // terrain normal clamped exactly as CreateGrass packs it (min(N, 0.94) after the shader's
        // decode) plus the baked light in [0, 0.99]. reference_grass_fnv.vert.hlsl reads these as
        // HLSL world[3]; every other consumer only reads the basis and translation, and the shared
        // vertex shaders force worldPos.w = 1.0 so the transform stays affine.
        var payload = new Vector3(
            MathF.Min(lightingNormal.X, FnvPackedNormalComponentMaximum),
            MathF.Min(lightingNormal.Y, FnvPackedNormalComponentMaximum),
            MathF.Min(lightingNormal.Z, FnvPackedNormalComponentMaximum));
        var light = float.IsFinite(bakedLight)
            ? Math.Clamp(bakedLight, 0f, FnvGrassLighting.BakedLightMaximum)
            : 0f;

        return new Matrix4x4(
            xAxis.X, xAxis.Y, xAxis.Z, payload.X,
            yAxis.X, yAxis.Y, yAxis.Z, payload.Y,
            zAxis.X, zAxis.Y, zAxis.Z, payload.Z,
            position.X, position.Y, position.Z, light);
    }

    private static uint Seed(
        int cellX,
        int cellY,
        int quadrant,
        int centerX,
        int centerY,
        uint textureFormId,
        uint grassFormId,
        int candidateX,
        int candidateY)
    {
        var value = 0x9E3779B9u;
        Mix(ref value, unchecked((uint)cellX));
        Mix(ref value, unchecked((uint)cellY));
        Mix(ref value, (uint)quadrant);
        Mix(ref value, (uint)centerX);
        Mix(ref value, (uint)centerY);
        Mix(ref value, textureFormId);
        Mix(ref value, grassFormId);
        Mix(ref value, (uint)candidateX);
        Mix(ref value, (uint)candidateY);
        return value;
    }

    private static void Mix(ref uint state, uint value)
    {
        state ^= value + 0x9E3779B9u + (state << 6) + (state >> 2);
        state ^= state >> 16;
        state *= 0x7FEB352Du;
        state ^= state >> 15;
    }

    private static float Random01(ref uint state)
    {
        // The engine uses a process MT for the density decision and a 512-entry seeded float table
        // for transforms. This stable integer mixer preserves their independent uniform distributions
        // without making grass change whenever unrelated viewer work consumes a global RNG.
        state += 0x9E3779B9u;
        var z = state;
        z = (z ^ (z >> 16)) * 0x21F0AAADu;
        z = (z ^ (z >> 15)) * 0x735A2D97u;
        z ^= z >> 15;
        return (z >> 8) * (1f / 16777216f);
    }
}

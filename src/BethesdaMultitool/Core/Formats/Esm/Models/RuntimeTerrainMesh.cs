using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Terrain;

namespace BethesdaMultitool.Core.Formats.Esm.Models;

/// <summary>
///     Runtime terrain mesh data extracted from TESObjectLAND::LoadedLandData heap pointers.
///     Contains up to 33x33 = 1089 vertices per cell (standard Gamebryo terrain grid).
/// </summary>
public record RuntimeTerrainMesh
{
    /// <summary>Terrain grid dimension (33 vertices per side).</summary>
    public const int GridSize = TerrainConstants.LandGridSize;

    /// <summary>Total vertex count per terrain cell (33x33 = 1089).</summary>
    public const int VertexCount = TerrainConstants.LandVertexCount;

    internal const float AxisBucketSize = 32f;
    private const float MaxTerrainCoordinate = 200_000f;
    private const float MaxTerrainHeight = 20_000f;
    internal const float MinTerrainOutlierWindow = 1_024f;
    internal const float MinTerrainSpan = 1_000f;
    internal const float MaxTerrainSpan = 10_000f;
    internal const float TerrainCellWorldSize = TerrainConstants.LandCellWorldSize;
    internal const float TerrainVertexSpacing = TerrainConstants.LandVertexSpacing;
    internal static readonly int[] CandidateGridSizes = [33, 17, 16, 9, 8, 5, 4];

    /// <summary>Vertex positions as flat [x,y,z, x,y,z, ...] array.</summary>
    public required float[] Vertices { get; init; }

    /// <summary>Vertex normals as flat [x,y,z, ...] array, or null if not available.</summary>
    public float[]? Normals { get; init; }

    /// <summary>Vertex colors as flat [r,g,b,a, ...] array, or null if not available.</summary>
    public float[]? Colors { get; init; }

    /// <summary>File offset where vertex data was read from.</summary>
    public long VertexDataOffset { get; init; }

    /// <summary>
    ///     Parent CELL FormID from the recovered LAND hierarchy. Conversion planning requires an
    ///     exact parent match before synthesizing LAND from this mesh.
    /// </summary>
    internal uint? SourceParentCellFormId { get; set; }

    /// <summary>Number of garbage Z vertices repaired during sanitization (0 if unsanitized).</summary>
    public int SanitizedZCount { get; init; }

    /// <summary>
    ///     Per-vertex mask indicating which vertices were sanitized (true = was garbage, now interpolated).
    ///     Null if no sanitization was performed. Length = VertexCount (1089).
    /// </summary>
    public bool[]? SanitizedMask { get; init; }

    /// <summary>
    ///     Source index repaired specifically because an unmasked exact (0,0,0)
    ///     sample was indistinguishable from an empty destination slot.
    /// </summary>
    internal int? SanitizedZeroOriginIndex { get; init; }

    /// <summary>Whether normals were successfully extracted.</summary>
    public bool HasNormals => Normals != null;

    /// <summary>Whether vertex colors were successfully extracted.</summary>
    public bool HasColors => Colors != null;

    /// <summary>
    ///     Exact source occupancy for canonical runtime meshes. When present, this distinguishes an
    ///     authored vertex at local (0, 0, 0) from an unfilled zero-initialized destination slot.
    /// </summary>
    internal bool[]? SourceVertexMask { get; init; }

    /// <summary>
    ///     Detect the LOD level of this terrain mesh from valid XY samples.
    ///     Supports both vertex-count grids (33, 17, 9, 5) and observed quad-count captures (16, 8, 4).
    /// </summary>
    public (int Level, int VerticesPerRow, float Spacing) DetectLodLevel()
    {
        var reconstruction = TryReconstructHeightGrid();
        if (reconstruction == null)
        {
            return (-1, 0, 0f);
        }

        return (TerrainGridReconstructor.GridSizeToLodLevel(reconstruction.SourceGridSize),
            reconstruction.SourceGridSize, reconstruction.SourceSpacing);
    }

    /// <summary>
    ///     Reconstruct a canonical 33x33 height grid from runtime terrain vertices.
    ///     Valid source samples are mapped by XY position, so dense lower-LOD captures and strided 33-slot captures
    ///     both reconstruct without relying on fixed buffer indices.
    /// </summary>
    internal TerrainGridReconstruction? TryReconstructHeightGrid()
    {
        var samples = CollectValidSamples();
        if (samples.Count < 12)
        {
            return null;
        }

        var bounds = TerrainBounds.FromSamples(samples);
        if (!bounds.IsPlausible)
        {
            return null;
        }

        var localReconstruction = TerrainGridReconstructor.TryReconstructLocalCanonicalGrid(samples, bounds);
        if (localReconstruction != null)
        {
            return localReconstruction;
        }

        TerrainGridCandidate? bestCandidate = null;
        foreach (var gridSize in CandidateGridSizes)
        {
            var candidate = TerrainGridReconstructor.TryBuildCandidate(samples, bounds, gridSize);
            if (candidate == null)
            {
                continue;
            }

            if (bestCandidate == null || candidate.Score > bestCandidate.Score)
            {
                bestCandidate = candidate;
            }
        }

        if (bestCandidate == null)
        {
            return null;
        }

        var sourceHeights = TerrainGridReconstructor.BuildSourceGrid(bestCandidate);
        var heights = TerrainGridReconstructor.InterpolateToCanonicalGrid(sourceHeights, bestCandidate.SourceGridSize);

        return new TerrainGridReconstruction
        {
            Heights = heights,
            SourceGridSize = bestCandidate.SourceGridSize,
            SourceSampleCount = bestCandidate.OccupiedCount,
            SourceCoveragePercent = bestCandidate.CoveragePercent,
            SourceCoverageMask = TerrainGridReconstructor.BuildCanonicalCoverageMask(bestCandidate),
            SourceSpacing = (bestCandidate.SpacingX + bestCandidate.SpacingY) * 0.5f,
            AverageFitError = bestCandidate.AverageFitError,
            MaxFitError = bestCandidate.MaxFitError,
            MinX = bounds.MinX,
            MaxX = bounds.MaxX,
            MinY = bounds.MinY,
            MaxY = bounds.MaxY
        };
    }

    /// <summary>
    ///     Return the canonical 33x33 source sample occupancy mask used for runtime terrain reconstruction.
    ///     True entries came from actual mesh vertices; false entries were interpolated or filled.
    /// </summary>
    internal bool[,]? TryGetCanonicalSourceCoverageMask()
    {
        return TryReconstructHeightGrid()?.SourceCoverageMask;
    }

    /// <summary>
    ///     Create a sanitized copy of this mesh with garbage Z values repaired.
    ///     Handles both extreme outliers (|Z| &gt; maxAbsZ, NaN, Infinity) and
    ///     unmapped memory zeros (Z == 0.0 when &gt; 20% of vertices are zero).
    ///     Uses iterative neighbor interpolation to fill gaps from the edges inward.
    /// </summary>
    public RuntimeTerrainMesh SanitizeVertices(float maxAbsZ = MaxTerrainHeight)
    {
        var sanitized = (float[])Vertices.Clone();
        var vertexCount = Math.Min(VertexCount, sanitized.Length / 3);
        int? sanitizedZeroOriginIndex = null;

        // Build a boolean grid of which Z values are bad.
        var isBad = new bool[VertexCount];

        for (var i = 0; i < vertexCount; i++)
        {
            var z = sanitized[i * 3 + 2];
            if (MathF.Abs(z) > maxAbsZ || float.IsNaN(z) || float.IsInfinity(z))
            {
                isBad[i] = true;
            }
        }

        var zeroCount = 0;
        for (var i = 0; i < vertexCount; i++)
        {
            if (!isBad[i] && MathF.Abs(sanitized[i * 3 + 2]) < 0.001f)
            {
                zeroCount++;
            }
        }

        var zeroPercent = vertexCount > 0 ? zeroCount * 100.0f / vertexCount : 0f;
        if (zeroPercent > 20.0f)
        {
            var hasAuthoritativeSourceMask = SourceVertexMask is { Length: >= VertexCount };
            for (var i = 0; i < vertexCount; i++)
            {
                var x = sanitized[i * 3];
                var y = sanitized[i * 3 + 1];
                if (!isBad[i] &&
                    MathF.Abs(sanitized[i * 3 + 2]) < 0.001f &&
                    MathF.Abs(x) < 0.001f &&
                    MathF.Abs(y) < 0.001f &&
                    (!hasAuthoritativeSourceMask || !SourceVertexMask![i]))
                {
                    isBad[i] = true;
                    sanitizedZeroOriginIndex = i;
                }
            }
        }

        var totalBad = 0;
        for (var i = 0; i < vertexCount; i++)
        {
            if (isBad[i])
            {
                totalBad++;
            }
        }

        if (totalBad == 0)
        {
            return this;
        }

        var originalBadMask = (bool[])isBad.Clone();

        bool madeProgress;
        do
        {
            madeProgress = false;
            for (var row = 0; row < GridSize; row++)
            {
                for (var col = 0; col < GridSize; col++)
                {
                    var idx = row * GridSize + col;
                    if (idx >= vertexCount || !isBad[idx])
                    {
                        continue;
                    }

                    var sum = 0f;
                    var count = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0)
                            {
                                continue;
                            }

                            var ny = row + dy;
                            var nx = col + dx;
                            if (ny < 0 || ny >= GridSize || nx < 0 || nx >= GridSize)
                            {
                                continue;
                            }

                            var nIdx = ny * GridSize + nx;
                            if (nIdx < vertexCount && !isBad[nIdx])
                            {
                                sum += sanitized[nIdx * 3 + 2];
                                count++;
                            }
                        }
                    }

                    if (count >= 2)
                    {
                        sanitized[idx * 3 + 2] = sum / count;
                        isBad[idx] = false;
                        madeProgress = true;
                    }
                }
            }
        } while (madeProgress);

        var validSum = 0f;
        var validCount = 0;
        for (var i = 0; i < vertexCount; i++)
        {
            if (!isBad[i])
            {
                validSum += sanitized[i * 3 + 2];
                validCount++;
            }
        }

        var fillValue = validCount > 0 ? validSum / validCount : 0f;
        for (var i = 0; i < vertexCount; i++)
        {
            if (isBad[i])
            {
                sanitized[i * 3 + 2] = fillValue;
            }
        }

        return this with
        {
            Vertices = sanitized,
            SanitizedZCount = totalBad,
            SanitizedMask = originalBadMask,
            SanitizedZeroOriginIndex = sanitizedZeroOriginIndex,
        };
    }

    /// <summary>
    ///     Count the number of garbage Z vertices (|Z| &gt; maxAbsZ, NaN, or Infinity).
    /// </summary>
    public int CountGarbageZ(float maxAbsZ = MaxTerrainHeight)
    {
        var count = 0;
        var vertexCount = Math.Min(VertexCount, Vertices.Length / 3);
        for (var i = 0; i < vertexCount; i++)
        {
            var z = Vertices[i * 3 + 2];
            if (MathF.Abs(z) > maxAbsZ || float.IsNaN(z) || float.IsInfinity(z))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    ///     Analyze raw vertex Z data quality for diagnostic purposes.
    ///     Detects partial captures, flat/corrupt data, and the reconstructed source LOD.
    /// </summary>
    public TerrainMeshDiagnostic DiagnoseQuality(int cellX = 0, int cellY = 0, uint formId = 0, float baseHeight = 0f)
    {
        var reconstruction = TryReconstructHeightGrid();
        var analysisHeights = TerrainHeightmapEncoder.ApplyHeightOffset(
            reconstruction?.Heights ?? ExtractDenseHeightsForDiagnostics(),
            baseHeight);
        var zValues = TerrainHeightmapEncoder.FlattenHeights(analysisHeights);

        var minZ = zValues.Min();
        var maxZ = zValues.Max();
        var zRange = maxZ - minZ;
        var mean = zValues.Average();
        var variance = zValues.Sum(z => (z - mean) * (z - mean)) / zValues.Length;
        var stdDev = MathF.Sqrt(variance);
        var uniqueZCount = zValues.Select(z => MathF.Round(z, 2)).Distinct().Count();
        var zeroZCount = zValues.Count(z => MathF.Abs(z) < 0.001f);
        var dominantGroup = zValues
            .GroupBy(z => MathF.Round(z, 1))
            .OrderByDescending(g => g.Count())
            .First();
        var dominantZPercent = dominantGroup.Count() * 100.0f / zValues.Length;

        var rowRanges = new float[GridSize];
        for (var y = 0; y < GridSize; y++)
        {
            var rowMin = float.MaxValue;
            var rowMax = float.MinValue;
            for (var x = 0; x < GridSize; x++)
            {
                var z = analysisHeights[y, x];
                if (z < rowMin) rowMin = z;
                if (z > rowMax) rowMax = z;
            }

            rowRanges[y] = rowMax - rowMin;
        }

        var lastActiveRow = 0;
        for (var y = GridSize - 1; y >= 0; y--)
        {
            if (rowRanges[y] > 1.0f)
            {
                lastActiveRow = y;
                break;
            }
        }

        var discontinuities = 0;
        for (var y = 1; y < GridSize; y++)
        {
            var prev = rowRanges[y - 1];
            var curr = rowRanges[y];
            if (prev > 1.0f && curr < prev * 0.2f)
            {
                discontinuities++;
            }
        }

        var garbageZCount = SanitizedZCount > 0 ? SanitizedZCount : CountGarbageZ();
        var sanitizedPercent = SanitizedZCount * 100.0f / VertexCount;
        var encodedRoundTripMaxError = reconstruction != null
            ? TerrainHeightmapEncoder.EncodeHeightmap(analysisHeights, VertexDataOffset).EncodedRoundTripMaxError
            : 0f;
        var inferredCell = reconstruction != null
            ? TerrainGridReconstructor.InferCellCoordinates(
                reconstruction.MinX, reconstruction.MaxX, reconstruction.MinY, reconstruction.MaxY)
            : null;

        string classification;
        if (reconstruction == null)
        {
            classification = "Corrupt";
        }
        else if (zRange < 0.1f)
        {
            classification = "Flat";
        }
        else if (dominantZPercent > 95.0f)
        {
            classification = "FewPixels";
        }
        else if (reconstruction.SourceCoveragePercent < 99.0f)
        {
            classification = "Partial";
        }
        else
        {
            classification = "Complete";
        }

        return new TerrainMeshDiagnostic
        {
            CellX = cellX,
            CellY = cellY,
            FormId = formId,
            MeshMinX = reconstruction?.MinX,
            MeshMaxX = reconstruction?.MaxX,
            MeshMinY = reconstruction?.MinY,
            MeshMaxY = reconstruction?.MaxY,
            MeshInferredCellX = inferredCell?.X,
            MeshInferredCellY = inferredCell?.Y,
            MinZ = minZ,
            MaxZ = maxZ,
            ZRange = zRange,
            ZStdDev = stdDev,
            UniqueZCount = uniqueZCount,
            ZeroZCount = zeroZCount,
            DominantZPercent = dominantZPercent,
            LastActiveRow = lastActiveRow,
            RowDiscontinuities = discontinuities,
            GarbageZCount = garbageZCount,
            SanitizedPercent = sanitizedPercent,
            HeightSource = reconstruction != null ? "RuntimeMESH" : "None",
            DetectedGridSize = reconstruction?.SourceGridSize ?? 0,
            DetectedLodLevel =
                reconstruction != null ? TerrainGridReconstructor.GridSizeToLodLevel(reconstruction.SourceGridSize) : -1,
            SourceSampleCount = reconstruction?.SourceSampleCount ?? 0,
            SourceCoveragePercent = reconstruction?.SourceCoveragePercent ?? 0f,
            EncodedRoundTripMaxError = encodedRoundTripMaxError,
            HasRuntimeVertexColors = HasColors,
            Classification = classification
        };
    }

    internal (int X, int Y)? TryInferCellCoordinates()
    {
        var reconstruction = TryReconstructHeightGrid();
        return reconstruction == null
            ? null
            : TerrainGridReconstructor.InferCellCoordinates(
                reconstruction.MinX, reconstruction.MaxX, reconstruction.MinY, reconstruction.MaxY);
    }

    /// <summary>
    ///     Convert runtime vertex heights to a LandHeightmap (VHGT-compatible format).
    ///     Lower LOD grids are reconstructed by XY position and bilinear interpolation.
    /// </summary>
    public LandHeightmap ToLandHeightmap(float baseHeight = 0f)
    {
        var reconstruction = TryReconstructHeightGrid();
        if (reconstruction == null)
        {
            throw new InvalidOperationException(
                "Runtime terrain mesh does not contain a reconstructable terrain grid.");
        }

        return TerrainHeightmapEncoder.EncodeHeightmap(
            TerrainHeightmapEncoder.ApplyHeightOffset(reconstruction.Heights, baseHeight),
            VertexDataOffset);
    }

    /// <summary>
    ///     Reconstruct VCLR bytes from runtime vertex colors using the same detected terrain grid as height data.
    ///     Alpha is ignored; output is RGB triplets in canonical 33x33 LAND vertex order.
    /// </summary>
    public byte[]? ToLandVertexColorBytes()
    {
        if (Colors is null || Colors.Length < 3)
        {
            return null;
        }

        var reconstruction = TryReconstructHeightGrid();
        if (reconstruction == null)
        {
            return null;
        }

        var samples = CollectValidSamples();
        if (samples.Count == 0)
        {
            return null;
        }

        var sourceGridSize = reconstruction.SourceGridSize;
        if (reconstruction.UsesCanonicalLocalFrame)
        {
            return ToLandVertexColorBytesFromCanonicalLocalSamples(samples);
        }

        var spacingX = (reconstruction.MaxX - reconstruction.MinX) / (sourceGridSize - 1);
        var spacingY = (reconstruction.MaxY - reconstruction.MinY) / (sourceGridSize - 1);
        if (spacingX <= 0f || spacingY <= 0f)
        {
            return null;
        }

        var cells = new TerrainColorCell?[sourceGridSize, sourceGridSize];
        var tolerance = Math.Max(24f, Math.Max(spacingX, spacingY) * 0.28f);
        var colorStride = Colors.Length >= VertexCount * 4 ? 4 : 3;

        foreach (var sample in samples)
        {
            var colorOffset = sample.Index * colorStride;
            if (colorOffset + 2 >= Colors.Length)
            {
                continue;
            }

            var r = Colors[colorOffset];
            var g = Colors[colorOffset + 1];
            var b = Colors[colorOffset + 2];
            if (!IsFinite(r) || !IsFinite(g) || !IsFinite(b))
            {
                continue;
            }

            var gridX = (int)MathF.Round((sample.X - reconstruction.MinX) / spacingX);
            var gridY = (int)MathF.Round((sample.Y - reconstruction.MinY) / spacingY);
            if (gridX < 0 || gridX >= sourceGridSize || gridY < 0 || gridY >= sourceGridSize)
            {
                continue;
            }

            var expectedX = reconstruction.MinX + gridX * spacingX;
            var expectedY = reconstruction.MinY + gridY * spacingY;
            var dx = sample.X - expectedX;
            var dy = sample.Y - expectedY;
            var fitError = MathF.Sqrt(dx * dx + dy * dy);
            if (fitError > tolerance)
            {
                continue;
            }

            var existing = cells[gridY, gridX];
            if (existing == null || fitError < existing.FitError)
            {
                cells[gridY, gridX] = new TerrainColorCell(r, g, b, fitError);
            }
        }

        var sourceColors = TerrainColorGridBuilder.BuildSourceColorGrid(cells, sourceGridSize);
        if (sourceColors == null)
        {
            return null;
        }

        return TerrainColorGridBuilder.InterpolateColorsToVclr(sourceColors, sourceGridSize);
    }

    private byte[]? ToLandVertexColorBytesFromCanonicalLocalSamples(List<TerrainVertexSample> samples)
    {
        var cells = new TerrainColorCell?[GridSize, GridSize];
        var colorStride = Colors!.Length >= VertexCount * 4 ? 4 : 3;
        foreach (var sample in samples)
        {
            var mapped = TerrainGridReconstructor.TryMapLocalSampleToCanonicalCell(sample);
            if (mapped == null)
            {
                continue;
            }

            var colorOffset = sample.Index * colorStride;
            if (colorOffset + 2 >= Colors.Length)
            {
                continue;
            }

            var r = Colors[colorOffset];
            var g = Colors[colorOffset + 1];
            var b = Colors[colorOffset + 2];
            if (!IsFinite(r) || !IsFinite(g) || !IsFinite(b))
            {
                continue;
            }

            var (gridX, gridY, fitError) = mapped.Value;
            var existing = cells[gridY, gridX];
            if (existing == null || fitError < existing.FitError)
            {
                cells[gridY, gridX] = new TerrainColorCell(r, g, b, fitError);
            }
        }

        var sourceColors = TerrainColorGridBuilder.BuildSourceColorGrid(cells, GridSize);
        return sourceColors == null ? null : TerrainColorGridBuilder.InterpolateColorsToVclr(sourceColors, GridSize);
    }

    /// <summary>
    ///     Reconstruct VNML bytes from runtime vertex normals using the same detected terrain grid as VCLR.
    ///     Returns null when Normals is missing, the canonical grid can't be reconstructed, or no valid
    ///     normal samples land on the canonical local frame. Components are encoded as sbyte (clamp(-127..127)
    ///     of <c>component * 127</c>) in canonical 33×33 LAND vertex order. Caller can fall back to
    ///     height-derived normals via <c>LandEncoder.BuildVnml</c> when this returns null.
    /// </summary>
    public byte[]? ToLandVertexNormalBytes()
    {
        if (Normals is null || Normals.Length < 3)
        {
            return null;
        }

        var reconstruction = TryReconstructHeightGrid();
        if (reconstruction == null)
        {
            return null;
        }

        var samples = CollectValidSamples();
        if (samples.Count == 0)
        {
            return null;
        }

        // World-frame VNML projection is omitted in v1 — cells whose runtime mesh doesn't sit
        // on the canonical local frame fall back to LandEncoder.BuildVnml's height-derived
        // normals, which is the historical behavior. Add the world-frame path here if a future
        // capture shows non-canonical meshes with meaningful normals worth preserving.
        if (!reconstruction.UsesCanonicalLocalFrame)
        {
            return null;
        }

        return ToLandVertexNormalBytesFromCanonicalLocalSamples(samples);
    }

    private byte[]? ToLandVertexNormalBytesFromCanonicalLocalSamples(List<TerrainVertexSample> samples)
    {
        var cells = new TerrainNormalCell?[GridSize, GridSize];
        foreach (var sample in samples)
        {
            var mapped = TerrainGridReconstructor.TryMapLocalSampleToCanonicalCell(sample);
            if (mapped == null)
            {
                continue;
            }

            var normalOffset = sample.Index * 3;
            if (normalOffset + 2 >= Normals!.Length)
            {
                continue;
            }

            var nx = Normals[normalOffset];
            var ny = Normals[normalOffset + 1];
            var nz = Normals[normalOffset + 2];
            if (!IsFinite(nx) || !IsFinite(ny) || !IsFinite(nz))
            {
                continue;
            }

            var (gridX, gridY, fitError) = mapped.Value;
            var existing = cells[gridY, gridX];
            if (existing == null || fitError < existing.FitError)
            {
                cells[gridY, gridX] = new TerrainNormalCell(nx, ny, nz, fitError);
            }
        }

        var sourceNormals = TerrainNormalGridBuilder.BuildSourceNormalGrid(cells, GridSize);
        return sourceNormals == null ? null : TerrainNormalGridBuilder.ProjectSourceNormalsToVnml(sourceNormals);
    }

    private List<TerrainVertexSample> CollectValidSamples()
    {
        var samples = new List<TerrainVertexSample>();
        var vertexCount = Math.Min(VertexCount, Vertices.Length / 3);
        var hasSourceMask = SourceVertexMask is { Length: >= VertexCount };

        for (var i = 0; i < vertexCount; i++)
        {
            if (hasSourceMask && !SourceVertexMask![i])
            {
                continue;
            }

            if (SanitizedMask != null && i < SanitizedMask.Length && SanitizedMask[i])
            {
                continue;
            }

            var x = Vertices[i * 3];
            var y = Vertices[i * 3 + 1];
            var z = Vertices[i * 3 + 2];

            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z))
            {
                continue;
            }

            if (MathF.Abs(x) > MaxTerrainCoordinate || MathF.Abs(y) > MaxTerrainCoordinate ||
                MathF.Abs(z) > MaxTerrainHeight)
            {
                continue;
            }

            if (!hasSourceMask &&
                MathF.Abs(x) < 0.001f && MathF.Abs(y) < 0.001f && MathF.Abs(z) < 0.001f)
            {
                continue;
            }

            samples.Add(new TerrainVertexSample(i, x, y, z));
        }

        return TerrainGridReconstructor.FilterZOutliers(samples);
    }

    private float[,] ExtractDenseHeightsForDiagnostics()
    {
        var heights = new float[GridSize, GridSize];
        var vertexCount = Math.Min(VertexCount, Vertices.Length / 3);
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                var index = y * GridSize + x;
                var z = index < vertexCount ? Vertices[index * 3 + 2] : 0f;
                heights[y, x] = IsFinite(z) && MathF.Abs(z) <= MaxTerrainHeight ? z : 0f;
            }
        }

        return heights;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    internal sealed record TerrainGridReconstruction
    {
        public required float[,] Heights { get; init; }
        public int SourceGridSize { get; init; }
        public int SourceSampleCount { get; init; }
        public float SourceCoveragePercent { get; init; }
        public bool[,]? SourceCoverageMask { get; init; }
        public float SourceSpacing { get; init; }
        public float AverageFitError { get; init; }
        public float MaxFitError { get; init; }
        public float MinX { get; init; }
        public float MaxX { get; init; }
        public float MinY { get; init; }
        public float MaxY { get; init; }
        public bool UsesCanonicalLocalFrame { get; init; }
    }
}

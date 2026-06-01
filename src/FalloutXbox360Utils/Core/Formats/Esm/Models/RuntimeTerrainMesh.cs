using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Terrain;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

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

    private const float AxisBucketSize = 32f;
    private const float MaxTerrainCoordinate = 200_000f;
    private const float MaxTerrainHeight = 20_000f;
    private const float MinTerrainOutlierWindow = 1_024f;
    private const float MinTerrainSpan = 1_000f;
    private const float MaxTerrainSpan = 10_000f;
    private const float TerrainCellWorldSize = TerrainConstants.LandCellWorldSize;
    private const float TerrainVertexSpacing = TerrainConstants.LandVertexSpacing;
    private static readonly int[] CandidateGridSizes = [33, 17, 16, 9, 8, 5, 4];

    /// <summary>Vertex positions as flat [x,y,z, x,y,z, ...] array.</summary>
    public required float[] Vertices { get; init; }

    /// <summary>Vertex normals as flat [x,y,z, ...] array, or null if not available.</summary>
    public float[]? Normals { get; init; }

    /// <summary>Vertex colors as flat [r,g,b,a, ...] array, or null if not available.</summary>
    public float[]? Colors { get; init; }

    /// <summary>File offset where vertex data was read from.</summary>
    public long VertexDataOffset { get; init; }

    /// <summary>Number of garbage Z vertices repaired during sanitization (0 if unsanitized).</summary>
    public int SanitizedZCount { get; init; }

    /// <summary>
    ///     Per-vertex mask indicating which vertices were sanitized (true = was garbage, now interpolated).
    ///     Null if no sanitization was performed. Length = VertexCount (1089).
    /// </summary>
    public bool[]? SanitizedMask { get; init; }

    /// <summary>Whether normals were successfully extracted.</summary>
    public bool HasNormals => Normals != null;

    /// <summary>Whether vertex colors were successfully extracted.</summary>
    public bool HasColors => Colors != null;

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

        return (GridSizeToLodLevel(reconstruction.SourceGridSize), reconstruction.SourceGridSize,
            reconstruction.SourceSpacing);
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

        var localReconstruction = TryReconstructLocalCanonicalGrid(samples, bounds);
        if (localReconstruction != null)
        {
            return localReconstruction;
        }

        TerrainGridCandidate? bestCandidate = null;
        foreach (var gridSize in CandidateGridSizes)
        {
            var candidate = TryBuildCandidate(samples, bounds, gridSize);
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

        var sourceHeights = BuildSourceGrid(bestCandidate);
        var heights = InterpolateToCanonicalGrid(sourceHeights, bestCandidate.SourceGridSize);

        return new TerrainGridReconstruction
        {
            Heights = heights,
            SourceGridSize = bestCandidate.SourceGridSize,
            SourceSampleCount = bestCandidate.OccupiedCount,
            SourceCoveragePercent = bestCandidate.CoveragePercent,
            SourceCoverageMask = BuildCanonicalCoverageMask(bestCandidate),
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
            for (var i = 0; i < vertexCount; i++)
            {
                var x = sanitized[i * 3];
                var y = sanitized[i * 3 + 1];
                if (!isBad[i] &&
                    MathF.Abs(sanitized[i * 3 + 2]) < 0.001f &&
                    MathF.Abs(x) < 0.001f &&
                    MathF.Abs(y) < 0.001f)
                {
                    isBad[i] = true;
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
            SanitizedMask = originalBadMask
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
        var analysisHeights = ApplyHeightOffset(
            reconstruction?.Heights ?? ExtractDenseHeightsForDiagnostics(),
            baseHeight);
        var zValues = FlattenHeights(analysisHeights);

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
            ? EncodeHeightmap(analysisHeights).EncodedRoundTripMaxError
            : 0f;
        var inferredCell = reconstruction != null
            ? InferCellCoordinates(reconstruction.MinX, reconstruction.MaxX, reconstruction.MinY, reconstruction.MaxY)
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
            DetectedLodLevel = reconstruction != null ? GridSizeToLodLevel(reconstruction.SourceGridSize) : -1,
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
            : InferCellCoordinates(reconstruction.MinX, reconstruction.MaxX, reconstruction.MinY, reconstruction.MaxY);
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

        return EncodeHeightmap(ApplyHeightOffset(reconstruction.Heights, baseHeight));
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

        var sourceColors = BuildSourceColorGrid(cells, sourceGridSize);
        if (sourceColors == null)
        {
            return null;
        }

        return InterpolateColorsToVclr(sourceColors, sourceGridSize);
    }

    private byte[]? ToLandVertexColorBytesFromCanonicalLocalSamples(List<TerrainVertexSample> samples)
    {
        var cells = new TerrainColorCell?[GridSize, GridSize];
        var colorStride = Colors!.Length >= VertexCount * 4 ? 4 : 3;
        foreach (var sample in samples)
        {
            var mapped = TryMapLocalSampleToCanonicalCell(sample);
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

        var sourceColors = BuildSourceColorGrid(cells, GridSize);
        return sourceColors == null ? null : InterpolateColorsToVclr(sourceColors, GridSize);
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
            var mapped = TryMapLocalSampleToCanonicalCell(sample);
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

        var sourceNormals = BuildSourceNormalGrid(cells, GridSize);
        return sourceNormals == null ? null : ProjectSourceNormalsToVnml(sourceNormals);
    }

    private static (float Nx, float Ny, float Nz)[,]? BuildSourceNormalGrid(
        TerrainNormalCell?[,] cells,
        int gridSize)
    {
        var source = new (float Nx, float Ny, float Nz)[gridSize, gridSize];
        var hasValue = new bool[gridSize, gridSize];
        var sumX = 0f;
        var sumY = 0f;
        var sumZ = 0f;
        var count = 0;

        for (var y = 0; y < gridSize; y++)
        {
            for (var x = 0; x < gridSize; x++)
            {
                var cell = cells[y, x];
                if (cell == null)
                {
                    continue;
                }

                source[y, x] = (cell.Nx, cell.Ny, cell.Nz);
                hasValue[y, x] = true;
                sumX += cell.Nx;
                sumY += cell.Ny;
                sumZ += cell.Nz;
                count++;
            }
        }

        if (count == 0)
        {
            return null;
        }

        // Fallback is the renormalized average normal across known cells — preserves "up-ish"
        // orientation when most of the cell is unsampled.
        var fallback = NormalizeOrUp(sumX / count, sumY / count, sumZ / count);
        bool madeProgress;
        do
        {
            madeProgress = false;
            for (var y = 0; y < gridSize; y++)
            {
                for (var x = 0; x < gridSize; x++)
                {
                    if (hasValue[y, x])
                    {
                        continue;
                    }

                    var nx = 0f;
                    var ny = 0f;
                    var nz = 0f;
                    var neighborCount = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0)
                            {
                                continue;
                            }

                            var nXi = x + dx;
                            var nYi = y + dy;
                            if (nXi < 0 || nXi >= gridSize || nYi < 0 || nYi >= gridSize || !hasValue[nYi, nXi])
                            {
                                continue;
                            }

                            nx += source[nYi, nXi].Nx;
                            ny += source[nYi, nXi].Ny;
                            nz += source[nYi, nXi].Nz;
                            neighborCount++;
                        }
                    }

                    if (neighborCount >= 2)
                    {
                        source[y, x] = NormalizeOrUp(nx / neighborCount, ny / neighborCount, nz / neighborCount);
                        hasValue[y, x] = true;
                        madeProgress = true;
                    }
                }
            }
        } while (madeProgress);

        for (var y = 0; y < gridSize; y++)
        {
            for (var x = 0; x < gridSize; x++)
            {
                if (!hasValue[y, x])
                {
                    source[y, x] = fallback;
                }
            }
        }

        return source;
    }

    private static byte[] ProjectSourceNormalsToVnml((float Nx, float Ny, float Nz)[,] source)
    {
        var bytes = new byte[VertexCount * 3];
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                var (nx, ny, nz) = source[y, x];
                var idx = (y * GridSize + x) * 3;
                bytes[idx + 0] = NormalComponentToByte(nx);
                bytes[idx + 1] = NormalComponentToByte(ny);
                bytes[idx + 2] = NormalComponentToByte(nz);
            }
        }

        return bytes;
    }

    private static (float Nx, float Ny, float Nz) NormalizeOrUp(float nx, float ny, float nz)
    {
        var length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (length <= 0.0001f)
        {
            return (0f, 0f, 1f);
        }

        return (nx / length, ny / length, nz / length);
    }

    private static byte NormalComponentToByte(float value)
    {
        var scaled = Math.Clamp((int)MathF.Round(value * 127f), sbyte.MinValue, sbyte.MaxValue);
        return unchecked((byte)(sbyte)scaled);
    }

    private sealed record TerrainNormalCell(float Nx, float Ny, float Nz, float FitError);

    private List<TerrainVertexSample> CollectValidSamples()
    {
        var samples = new List<TerrainVertexSample>();
        var vertexCount = Math.Min(VertexCount, Vertices.Length / 3);

        for (var i = 0; i < vertexCount; i++)
        {
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

            if (MathF.Abs(x) < 0.001f && MathF.Abs(y) < 0.001f && MathF.Abs(z) < 0.001f)
            {
                continue;
            }

            samples.Add(new TerrainVertexSample(i, x, y, z));
        }

        return FilterZOutliers(samples);
    }

    private static List<TerrainVertexSample> FilterZOutliers(List<TerrainVertexSample> samples)
    {
        if (samples.Count < 64)
        {
            return samples;
        }

        var heights = samples.Select(sample => sample.Z).Order().ToArray();
        var q1 = Percentile(heights, 0.25f);
        var q3 = Percentile(heights, 0.75f);
        var iqr = q3 - q1;
        var window = Math.Max(MinTerrainOutlierWindow, iqr * 3f);
        var minZ = q1 - window;
        var maxZ = q3 + window;

        var filtered = samples
            .Where(sample => sample.Z >= minZ && sample.Z <= maxZ)
            .ToList();

        return filtered.Count >= 12 ? filtered : samples;
    }

    private static float Percentile(float[] sortedValues, float percentile)
    {
        if (sortedValues.Length == 1)
        {
            return sortedValues[0];
        }

        var index = Math.Clamp(percentile, 0f, 1f) * (sortedValues.Length - 1);
        var lower = (int)MathF.Floor(index);
        var upper = Math.Min(lower + 1, sortedValues.Length - 1);
        var fraction = index - lower;
        return sortedValues[lower] * (1f - fraction) + sortedValues[upper] * fraction;
    }

    private static TerrainGridCandidate? TryBuildCandidate(
        List<TerrainVertexSample> samples,
        TerrainBounds bounds,
        int gridSize)
    {
        var spacingX = bounds.RangeX / (gridSize - 1);
        var spacingY = bounds.RangeY / (gridSize - 1);
        if (spacingX <= 0f || spacingY <= 0f)
        {
            return null;
        }

        var cells = new TerrainGridCell?[gridSize, gridSize];
        var tolerance = Math.Max(24f, Math.Max(spacingX, spacingY) * 0.28f);

        foreach (var sample in samples)
        {
            var gridX = (int)MathF.Round((sample.X - bounds.MinX) / spacingX);
            var gridY = (int)MathF.Round((sample.Y - bounds.MinY) / spacingY);
            if (gridX < 0 || gridX >= gridSize || gridY < 0 || gridY >= gridSize)
            {
                continue;
            }

            var expectedX = bounds.MinX + gridX * spacingX;
            var expectedY = bounds.MinY + gridY * spacingY;
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
                cells[gridY, gridX] = new TerrainGridCell(sample.Z, fitError);
            }
        }

        var occupied = 0;
        var fitSum = 0f;
        var maxFit = 0f;
        for (var y = 0; y < gridSize; y++)
        {
            for (var x = 0; x < gridSize; x++)
            {
                var cell = cells[y, x];
                if (cell == null)
                {
                    continue;
                }

                occupied++;
                fitSum += cell.FitError;
                maxFit = Math.Max(maxFit, cell.FitError);
            }
        }

        var coveragePercent = occupied * 100.0f / (gridSize * gridSize);
        if (coveragePercent < RequiredCoveragePercent(gridSize) || !HasAdequateEdgeCoverage(cells, gridSize))
        {
            return null;
        }

        var averageFit = occupied > 0 ? fitSum / occupied : 0f;
        var score = occupied * 1000f + gridSize * 10f - averageFit;

        return new TerrainGridCandidate
        {
            SourceGridSize = gridSize,
            Cells = cells,
            OccupiedCount = occupied,
            CoveragePercent = coveragePercent,
            SpacingX = spacingX,
            SpacingY = spacingY,
            AverageFitError = averageFit,
            MaxFitError = maxFit,
            Score = score
        };
    }

    private static TerrainGridReconstruction? TryReconstructLocalCanonicalGrid(
        List<TerrainVertexSample> samples,
        TerrainBounds bounds)
    {
        if (!UsesLocalCellCoordinates(bounds))
        {
            return null;
        }

        var cells = new TerrainGridCell?[GridSize, GridSize];
        var fitSum = 0f;
        var maxFit = 0f;
        foreach (var sample in samples)
        {
            var mapped = TryMapLocalSampleToCanonicalCell(sample);
            if (mapped == null)
            {
                continue;
            }

            var (gridX, gridY, fitError) = mapped.Value;
            var existing = cells[gridY, gridX];
            if (existing == null || fitError < existing.FitError)
            {
                cells[gridY, gridX] = new TerrainGridCell(sample.Z, fitError);
            }
        }

        var occupied = 0;
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                var cell = cells[y, x];
                if (cell == null)
                {
                    continue;
                }

                occupied++;
                fitSum += cell.FitError;
                maxFit = Math.Max(maxFit, cell.FitError);
            }
        }

        if (occupied < 12)
        {
            return null;
        }

        var heights = BuildSourceGrid(new TerrainGridCandidate
        {
            SourceGridSize = GridSize,
            Cells = cells,
            OccupiedCount = occupied,
            CoveragePercent = occupied * 100.0f / VertexCount,
            SpacingX = TerrainVertexSpacing,
            SpacingY = TerrainVertexSpacing,
            AverageFitError = occupied > 0 ? fitSum / occupied : 0f,
            MaxFitError = maxFit,
            Score = occupied
        });

        return new TerrainGridReconstruction
        {
            Heights = heights,
            SourceGridSize = EstimateSourceGridSize(cells),
            SourceSampleCount = occupied,
            SourceCoveragePercent = occupied * 100.0f / VertexCount,
            SourceCoverageMask = BuildCanonicalCoverageMask(cells),
            SourceSpacing = TerrainVertexSpacing,
            AverageFitError = occupied > 0 ? fitSum / occupied : 0f,
            MaxFitError = maxFit,
            MinX = bounds.MinX,
            MaxX = bounds.MaxX,
            MinY = bounds.MinY,
            MaxY = bounds.MaxY,
            UsesCanonicalLocalFrame = true
        };
    }

    private static bool[,] BuildCanonicalCoverageMask(TerrainGridCandidate candidate)
    {
        var mask = new bool[GridSize, GridSize];
        if (candidate.SourceGridSize <= 1)
        {
            return mask;
        }

        for (var y = 0; y < candidate.SourceGridSize; y++)
        {
            for (var x = 0; x < candidate.SourceGridSize; x++)
            {
                if (candidate.Cells[y, x] == null)
                {
                    continue;
                }

                var canonicalX = (int)MathF.Round(x * (GridSize - 1) / (float)(candidate.SourceGridSize - 1));
                var canonicalY = (int)MathF.Round(y * (GridSize - 1) / (float)(candidate.SourceGridSize - 1));
                mask[canonicalY, canonicalX] = true;
            }
        }

        return mask;
    }

    private static bool[,] BuildCanonicalCoverageMask(TerrainGridCell?[,] cells)
    {
        var mask = new bool[GridSize, GridSize];
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                mask[y, x] = cells[y, x] != null;
            }
        }

        return mask;
    }

    private static bool UsesLocalCellCoordinates(TerrainBounds bounds)
    {
        return TerrainCoordinateMapper.IsWithinLocalCellBounds(
            bounds.MinX,
            bounds.MaxX,
            bounds.MinY,
            bounds.MaxY);
    }

    private static (int X, int Y, float FitError)? TryMapLocalSampleToCanonicalCell(TerrainVertexSample sample)
    {
        var mapped = TerrainCoordinateMapper.TryMapLocalVertexToCanonicalCell(sample.X, sample.Y);
        return mapped == null
            ? null
            : (mapped.Value.X, mapped.Value.Y, mapped.Value.FitError);
    }

    private static int EstimateSourceGridSize(TerrainGridCell?[,] cells)
    {
        var xCount = 0;
        var yCount = 0;
        for (var x = 0; x < GridSize; x++)
        {
            for (var y = 0; y < GridSize; y++)
            {
                if (cells[y, x] != null)
                {
                    xCount++;
                    break;
                }
            }
        }

        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                if (cells[y, x] != null)
                {
                    yCount++;
                    break;
                }
            }
        }

        var observed = Math.Max(xCount, yCount);
        return CandidateGridSizes
            .OrderBy(size => Math.Abs(size - observed))
            .ThenByDescending(size => size)
            .First();
    }

    private static float[,] BuildSourceGrid(TerrainGridCandidate candidate)
    {
        var gridSize = candidate.SourceGridSize;
        var source = new float[gridSize, gridSize];
        var hasValue = new bool[gridSize, gridSize];
        var sum = 0f;
        var count = 0;

        for (var y = 0; y < gridSize; y++)
        {
            for (var x = 0; x < gridSize; x++)
            {
                var cell = candidate.Cells[y, x];
                if (cell == null)
                {
                    continue;
                }

                source[y, x] = cell.Z;
                hasValue[y, x] = true;
                sum += cell.Z;
                count++;
            }
        }

        var fallback = count > 0 ? sum / count : 0f;
        bool madeProgress;
        do
        {
            madeProgress = false;
            for (var y = 0; y < gridSize; y++)
            {
                for (var x = 0; x < gridSize; x++)
                {
                    if (hasValue[y, x])
                    {
                        continue;
                    }

                    var neighborSum = 0f;
                    var neighborCount = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0)
                            {
                                continue;
                            }

                            var nx = x + dx;
                            var ny = y + dy;
                            if (nx < 0 || nx >= gridSize || ny < 0 || ny >= gridSize || !hasValue[ny, nx])
                            {
                                continue;
                            }

                            neighborSum += source[ny, nx];
                            neighborCount++;
                        }
                    }

                    if (neighborCount >= 2)
                    {
                        source[y, x] = neighborSum / neighborCount;
                        hasValue[y, x] = true;
                        madeProgress = true;
                    }
                }
            }
        } while (madeProgress);

        for (var y = 0; y < gridSize; y++)
        {
            for (var x = 0; x < gridSize; x++)
            {
                if (!hasValue[y, x])
                {
                    source[y, x] = fallback;
                }
            }
        }

        return source;
    }

    private static float[,] InterpolateToCanonicalGrid(float[,] source, int sourceGridSize)
    {
        var heights = new float[GridSize, GridSize];
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                var srcX = x * (sourceGridSize - 1) / (float)(GridSize - 1);
                var srcY = y * (sourceGridSize - 1) / (float)(GridSize - 1);

                var x0 = (int)MathF.Floor(srcX);
                var y0 = (int)MathF.Floor(srcY);
                var x1 = Math.Min(x0 + 1, sourceGridSize - 1);
                var y1 = Math.Min(y0 + 1, sourceGridSize - 1);
                var fx = srcX - x0;
                var fy = srcY - y0;

                heights[y, x] = source[y0, x0] * (1 - fx) * (1 - fy)
                                + source[y0, x1] * fx * (1 - fy)
                                + source[y1, x0] * (1 - fx) * fy
                                + source[y1, x1] * fx * fy;
            }
        }

        return heights;
    }

    private static (float R, float G, float B)[,]? BuildSourceColorGrid(
        TerrainColorCell?[,] cells,
        int gridSize)
    {
        var source = new (float R, float G, float B)[gridSize, gridSize];
        var hasValue = new bool[gridSize, gridSize];
        var sumR = 0f;
        var sumG = 0f;
        var sumB = 0f;
        var count = 0;

        for (var y = 0; y < gridSize; y++)
        {
            for (var x = 0; x < gridSize; x++)
            {
                var cell = cells[y, x];
                if (cell == null)
                {
                    continue;
                }

                source[y, x] = (cell.R, cell.G, cell.B);
                hasValue[y, x] = true;
                sumR += cell.R;
                sumG += cell.G;
                sumB += cell.B;
                count++;
            }
        }

        if (count == 0)
        {
            return null;
        }

        var fallback = (R: sumR / count, G: sumG / count, B: sumB / count);
        bool madeProgress;
        do
        {
            madeProgress = false;
            for (var y = 0; y < gridSize; y++)
            {
                for (var x = 0; x < gridSize; x++)
                {
                    if (hasValue[y, x])
                    {
                        continue;
                    }

                    var r = 0f;
                    var g = 0f;
                    var b = 0f;
                    var neighborCount = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0)
                            {
                                continue;
                            }

                            var nx = x + dx;
                            var ny = y + dy;
                            if (nx < 0 || nx >= gridSize || ny < 0 || ny >= gridSize || !hasValue[ny, nx])
                            {
                                continue;
                            }

                            r += source[ny, nx].R;
                            g += source[ny, nx].G;
                            b += source[ny, nx].B;
                            neighborCount++;
                        }
                    }

                    if (neighborCount >= 2)
                    {
                        source[y, x] = (r / neighborCount, g / neighborCount, b / neighborCount);
                        hasValue[y, x] = true;
                        madeProgress = true;
                    }
                }
            }
        } while (madeProgress);

        for (var y = 0; y < gridSize; y++)
        {
            for (var x = 0; x < gridSize; x++)
            {
                if (!hasValue[y, x])
                {
                    source[y, x] = fallback;
                }
            }
        }

        return source;
    }

    private static byte[] InterpolateColorsToVclr((float R, float G, float B)[,] source, int sourceGridSize)
    {
        var bytes = new byte[VertexCount * 3];
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                var srcX = x * (sourceGridSize - 1) / (float)(GridSize - 1);
                var srcY = y * (sourceGridSize - 1) / (float)(GridSize - 1);

                var x0 = (int)MathF.Floor(srcX);
                var y0 = (int)MathF.Floor(srcY);
                var x1 = Math.Min(x0 + 1, sourceGridSize - 1);
                var y1 = Math.Min(y0 + 1, sourceGridSize - 1);
                var fx = srcX - x0;
                var fy = srcY - y0;

                var c00 = source[y0, x0];
                var c10 = source[y0, x1];
                var c01 = source[y1, x0];
                var c11 = source[y1, x1];

                var r = Bilinear(c00.R, c10.R, c01.R, c11.R, fx, fy);
                var g = Bilinear(c00.G, c10.G, c01.G, c11.G, fx, fy);
                var b = Bilinear(c00.B, c10.B, c01.B, c11.B, fx, fy);

                var index = (y * GridSize + x) * 3;
                bytes[index] = ToColorByte(r);
                bytes[index + 1] = ToColorByte(g);
                bytes[index + 2] = ToColorByte(b);
            }
        }

        return bytes;
    }

    private static float Bilinear(float c00, float c10, float c01, float c11, float fx, float fy)
    {
        return c00 * (1 - fx) * (1 - fy)
               + c10 * fx * (1 - fy)
               + c01 * (1 - fx) * fy
               + c11 * fx * fy;
    }

    private static byte ToColorByte(float value)
    {
        var scaled = value > 1.5f ? value : value * 255f;
        return (byte)Math.Clamp((int)MathF.Round(scaled), 0, 255);
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

    private static float[] FlattenHeights(float[,] heights)
    {
        var values = new float[VertexCount];
        var index = 0;
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                values[index++] = heights[y, x];
            }
        }

        return values;
    }

    private static float[,] ApplyHeightOffset(float[,] heights, float offset)
    {
        if (MathF.Abs(offset) < 0.001f)
        {
            return heights;
        }

        var adjusted = new float[GridSize, GridSize];
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                adjusted[y, x] = heights[y, x] + offset;
            }
        }

        return adjusted;
    }

    /// <summary>
    ///     Encode a 33x33 height grid as a VHGT-compatible LandHeightmap with cumulative sbyte deltas.
    /// </summary>
    private LandHeightmap EncodeHeightmap(float[,] heights)
    {
        var heightOffset = heights[0, 0] / 8.0f;
        var deltas = new sbyte[VertexCount];

        var decoderCol0 = heightOffset * 8.0f;
        for (var y = 0; y < GridSize; y++)
        {
            var runningHeight = decoderCol0;

            for (var x = 0; x < GridSize; x++)
            {
                var exactDelta = (heights[y, x] - runningHeight) / 8.0f;
                var clamped = Math.Clamp((int)Math.Round(exactDelta), sbyte.MinValue, sbyte.MaxValue);
                deltas[y * GridSize + x] = (sbyte)clamped;
                runningHeight += clamped * 8.0f;
            }

            decoderCol0 += deltas[y * GridSize] * 8.0f;
        }

        var decodedHeights = DecodeHeightmap(heightOffset, deltas);
        var maxError = 0f;
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                maxError = Math.Max(maxError, MathF.Abs(decodedHeights[y, x] - heights[y, x]));
            }
        }

        return new LandHeightmap
        {
            HeightOffset = heightOffset,
            HeightDeltas = deltas,
            Offset = VertexDataOffset,
            ExactHeights = heights,
            EncodedRoundTripMaxError = maxError
        };
    }

    private static float[,] DecodeHeightmap(float heightOffset, sbyte[] deltas)
    {
        var heights = new float[GridSize, GridSize];
        var rowStart = heightOffset * 8f;

        for (var y = 0; y < GridSize; y++)
        {
            var height = rowStart;
            for (var x = 0; x < GridSize; x++)
            {
                height += deltas[y * GridSize + x] * 8f;
                heights[y, x] = height;
            }

            rowStart = heights[y, 0];
        }

        return heights;
    }

    private static bool HasAdequateEdgeCoverage(TerrainGridCell?[,] cells, int gridSize)
    {
        var required = Math.Max(2, gridSize / 3);
        var top = 0;
        var bottom = 0;
        var left = 0;
        var right = 0;

        for (var i = 0; i < gridSize; i++)
        {
            if (cells[0, i] != null) top++;
            if (cells[gridSize - 1, i] != null) bottom++;
            if (cells[i, 0] != null) left++;
            if (cells[i, gridSize - 1] != null) right++;
        }

        return top >= required && bottom >= required && left >= required && right >= required;
    }

    private static float RequiredCoveragePercent(int gridSize)
    {
        return gridSize <= 5 ? 75f : 80f;
    }

    private static int GridSizeToLodLevel(int gridSize)
    {
        return gridSize switch
        {
            >= 33 => 0,
            >= 16 => 1,
            >= 8 => 2,
            >= 4 => 3,
            _ => -1
        };
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static (int X, int Y)? InferCellCoordinates(float minX, float maxX, float minY, float maxY)
    {
        var rangeX = maxX - minX;
        var rangeY = maxY - minY;
        if (rangeX < MinTerrainSpan || rangeY < MinTerrainSpan ||
            rangeX > MaxTerrainSpan || rangeY > MaxTerrainSpan)
        {
            return null;
        }

        var centerX = (minX + maxX) * 0.5f;
        var centerY = (minY + maxY) * 0.5f;
        if (MathF.Abs(centerX) < TerrainCellWorldSize && MathF.Abs(centerY) < TerrainCellWorldSize)
        {
            return null;
        }

        return (
            (int)MathF.Floor(centerX / TerrainCellWorldSize),
            (int)MathF.Floor(centerY / TerrainCellWorldSize));
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

    private sealed record TerrainVertexSample(int Index, float X, float Y, float Z);

    private sealed record TerrainGridCell(float Z, float FitError);

    private sealed record TerrainColorCell(float R, float G, float B, float FitError);

    private sealed record TerrainGridCandidate
    {
        public int SourceGridSize { get; init; }
        public required TerrainGridCell?[,] Cells { get; init; }
        public int OccupiedCount { get; init; }
        public float CoveragePercent { get; init; }
        public float SpacingX { get; init; }
        public float SpacingY { get; init; }
        public float AverageFitError { get; init; }
        public float MaxFitError { get; init; }
        public float Score { get; init; }
    }

    private readonly record struct TerrainBounds(float MinX, float MaxX, float MinY, float MaxY)
    {
        public float RangeX => MaxX - MinX;
        public float RangeY => MaxY - MinY;

        public bool IsPlausible =>
            RangeX >= MinTerrainSpan &&
            RangeY >= MinTerrainSpan &&
            RangeX <= MaxTerrainSpan &&
            RangeY <= MaxTerrainSpan;

        public static TerrainBounds FromSamples(List<TerrainVertexSample> samples)
        {
            var (minX, maxX) = AxisBounds(samples.Select(s => s.X));
            var (minY, maxY) = AxisBounds(samples.Select(s => s.Y));
            return new TerrainBounds(minX, maxX, minY, maxY);
        }

        private static (float Min, float Max) AxisBounds(IEnumerable<float> values)
        {
            var valueList = values.ToList();
            var buckets = new Dictionary<int, int>();
            foreach (var value in valueList)
            {
                var bucket = (int)MathF.Round(value / AxisBucketSize);
                buckets[bucket] = buckets.GetValueOrDefault(bucket) + 1;
            }

            var repeatedBuckets = buckets
                .Where(kv => kv.Value >= 3)
                .Select(kv => kv.Key)
                .OrderBy(bucket => bucket)
                .ToList();

            if (repeatedBuckets.Count >= 4)
            {
                return (repeatedBuckets[0] * AxisBucketSize, repeatedBuckets[^1] * AxisBucketSize);
            }

            valueList.Sort();
            return (valueList[0], valueList[^1]);
        }
    }
}

namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Runtime terrain mesh data extracted from TESObjectLAND::LoadedLandData heap pointers.
///     Contains 33×33 = 1089 vertices per cell (standard Gamebryo terrain grid).
/// </summary>
public record RuntimeTerrainMesh
{
    /// <summary>Terrain grid dimension (33 vertices per side).</summary>
    public const int GridSize = 33;

    /// <summary>Total vertex count per terrain cell (33×33 = 1089).</summary>
    public const int VertexCount = GridSize * GridSize;

    /// <summary>1089 vertex positions as flat [x,y,z, x,y,z, ...] array (3267 floats).</summary>
    public required float[] Vertices { get; init; }

    /// <summary>1089 vertex normals as flat [x,y,z, ...] array, or null if not available.</summary>
    public float[]? Normals { get; init; }

    /// <summary>1089 vertex colors as flat [r,g,b,a, ...] array, or null if not available.</summary>
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
    ///     Detect the LOD level of this terrain mesh based on vertex XY grid analysis.
    ///     The 1089-vertex buffer is shared across LOD levels:
    ///     LOD 0 = 33×33 at 128-unit spacing, LOD 1 = 17×17 at 256, LOD 2 = 9×9 at 512, LOD 3 = 5×5 at 1024.
    ///     Returns (lodLevel, verticesPerRow, measuredSpacing).
    /// </summary>
    public (int Level, int VerticesPerRow, float Spacing) DetectLodLevel()
    {
        // Collect valid X positions from vertices, bucketed to 64-unit resolution
        // to handle float imprecision while still distinguishing 128 vs 256 spacing.
        var xBuckets = new SortedSet<int>();
        for (var i = 0; i < VertexCount; i++)
        {
            var x = Vertices[i * 3];
            var y = Vertices[i * 3 + 1];

            // Skip garbage vertices near origin (unmapped memory)
            if (MathF.Abs(x) < 10f && MathF.Abs(y) < 10f)
            {
                continue;
            }

            // Skip extreme/invalid values
            if (MathF.Abs(x) > 100_000f || float.IsNaN(x) || float.IsInfinity(x))
            {
                continue;
            }

            // Bucket to nearest 64 units to group close-but-not-exact values
            xBuckets.Add((int)MathF.Round(x / 64f));
        }

        if (xBuckets.Count < 3)
        {
            return (-1, xBuckets.Count, 0f);
        }

        // Count unique X buckets — this directly indicates grid resolution.
        // Use the mode spacing between consecutive buckets to determine actual step size.
        var bucketList = xBuckets.ToList();
        var gapCounts = new Dictionary<int, int>();
        for (var i = 1; i < bucketList.Count; i++)
        {
            var gap = bucketList[i] - bucketList[i - 1];
            if (gap > 0)
            {
                gapCounts[gap] = gapCounts.GetValueOrDefault(gap) + 1;
            }
        }

        // Find the most frequent gap (mode)
        var modeGap = gapCounts.OrderByDescending(kv => kv.Value).First().Key;
        var modeSpacing = modeGap * 64f;

        // Count vertices that fall on the regular grid defined by the mode spacing.
        // The grid-consistent unique X count determines LOD level.
        var gridMinX = bucketList[0];
        var gridConsistentCount = 0;
        foreach (var bucket in bucketList)
        {
            var distFromGrid = (bucket - gridMinX) % modeGap;
            if (distFromGrid == 0)
            {
                gridConsistentCount++;
            }
        }

        // Map grid-consistent X count to LOD level:
        // LOD 0: ~33 unique X positions, LOD 1: ~17, LOD 2: ~9, LOD 3: ~5
        int level;
        if (gridConsistentCount >= 28)       // LOD 0: 33 (allow some missing)
        {
            level = 0;
        }
        else if (gridConsistentCount >= 14)  // LOD 1: 17
        {
            level = 1;
        }
        else if (gridConsistentCount >= 7)   // LOD 2: 9
        {
            level = 2;
        }
        else if (gridConsistentCount >= 4)   // LOD 3: 5
        {
            level = 3;
        }
        else
        {
            level = -1;
        }

        return (level, gridConsistentCount, modeSpacing);
    }

    /// <summary>
    ///     Create a sanitized copy of this mesh with garbage Z values repaired.
    ///     Handles both extreme outliers (|Z| > maxAbsZ, NaN, Infinity) and
    ///     unmapped memory zeros (Z == 0.0 when > 20% of vertices are zero).
    ///     Uses iterative neighbor interpolation to fill gaps from the edges inward.
    /// </summary>
    public RuntimeTerrainMesh SanitizeVertices(float maxAbsZ = 100_000f)
    {
        var sanitized = (float[])Vertices.Clone();

        // Build a boolean grid of which Z values are bad
        var isBad = new bool[VertexCount];
        var extremeCount = 0;

        // Phase 1: Mark extreme garbage (NaN, Infinity, out-of-range)
        for (var i = 0; i < VertexCount; i++)
        {
            var z = sanitized[i * 3 + 2];
            if (MathF.Abs(z) > maxAbsZ || float.IsNaN(z) || float.IsInfinity(z))
            {
                isBad[i] = true;
                extremeCount++;
            }
        }

        // Phase 2: Also mark Z == 0.0 as unmapped memory if the cell has many zeros.
        // Natural terrain rarely has > 20% of vertices at exactly 0.000.
        var zeroCount = 0;
        for (var i = 0; i < VertexCount; i++)
        {
            if (!isBad[i] && MathF.Abs(sanitized[i * 3 + 2]) < 0.001f)
            {
                zeroCount++;
            }
        }

        var zeroPercent = zeroCount * 100.0f / VertexCount;
        if (zeroPercent > 20.0f)
        {
            for (var i = 0; i < VertexCount; i++)
            {
                if (!isBad[i] && MathF.Abs(sanitized[i * 3 + 2]) < 0.001f)
                {
                    isBad[i] = true;
                }
            }
        }

        var totalBad = 0;
        for (var i = 0; i < VertexCount; i++)
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

        // Snapshot the original bad mask before interpolation modifies it.
        // This preserves which vertices were originally garbage for downstream analysis.
        var originalBadMask = (bool[])isBad.Clone();

        // Iterative neighbor interpolation: repeat passes until no more progress.
        // Each pass fills bad vertices that have >= 2 valid neighbors, gradually
        // expanding the valid region inward from the edges of good data.
        bool madeProgress;
        do
        {
            madeProgress = false;
            for (var row = 0; row < GridSize; row++)
            {
                for (var col = 0; col < GridSize; col++)
                {
                    var idx = row * GridSize + col;
                    if (!isBad[idx])
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
                            if (ny >= 0 && ny < GridSize && nx >= 0 && nx < GridSize)
                            {
                                var nIdx = ny * GridSize + nx;
                                if (!isBad[nIdx])
                                {
                                    sum += sanitized[nIdx * 3 + 2];
                                    count++;
                                }
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

        // Fill any remaining bad vertices (isolated with < 2 neighbors) with cell average
        var validSum = 0f;
        var validCount = 0;
        for (var i = 0; i < VertexCount; i++)
        {
            if (!isBad[i])
            {
                validSum += sanitized[i * 3 + 2];
                validCount++;
            }
        }

        var fillValue = validCount > 0 ? validSum / validCount : 0f;
        for (var i = 0; i < VertexCount; i++)
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
    ///     Count the number of garbage Z vertices (|Z| > maxAbsZ, NaN, or Infinity).
    /// </summary>
    public int CountGarbageZ(float maxAbsZ = 100_000f)
    {
        var count = 0;
        for (var i = 0; i < VertexCount; i++)
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
    ///     Detects partial captures, flat/corrupt data, and abrupt cutoffs.
    /// </summary>
    public TerrainMeshDiagnostic DiagnoseQuality(int cellX = 0, int cellY = 0, uint formId = 0)
    {
        // Extract all Z values
        var zValues = new float[VertexCount];
        for (var i = 0; i < VertexCount; i++)
        {
            zValues[i] = Vertices[i * 3 + 2];
        }

        var minZ = zValues.Min();
        var maxZ = zValues.Max();
        var zRange = maxZ - minZ;

        // Standard deviation
        var mean = zValues.Average();
        var variance = zValues.Sum(z => (z - mean) * (z - mean)) / VertexCount;
        var stdDev = MathF.Sqrt(variance);

        // Unique Z count (using small epsilon for float comparison)
        var uniqueZCount = zValues.Select(z => MathF.Round(z, 2)).Distinct().Count();

        // Zero Z count
        var zeroZCount = zValues.Count(z => MathF.Abs(z) < 0.001f);

        // Dominant Z value — most common Z (rounded to 1 decimal)
        var dominantGroup = zValues
            .GroupBy(z => MathF.Round(z, 1))
            .OrderByDescending(g => g.Count())
            .First();
        var dominantZPercent = dominantGroup.Count() * 100.0f / VertexCount;

        // Per-row analysis: find last row with meaningful variation and detect discontinuities
        var rowRanges = new float[GridSize];
        for (var y = 0; y < GridSize; y++)
        {
            var rowMin = float.MaxValue;
            var rowMax = float.MinValue;
            for (var x = 0; x < GridSize; x++)
            {
                var z = zValues[y * GridSize + x];
                if (z < rowMin) rowMin = z;
                if (z > rowMax) rowMax = z;
            }

            rowRanges[y] = rowMax - rowMin;
        }

        // Last active row: highest row with Z range > 1.0 (meaningful terrain variation)
        var lastActiveRow = 0;
        for (var y = GridSize - 1; y >= 0; y--)
        {
            if (rowRanges[y] > 1.0f)
            {
                lastActiveRow = y;
                break;
            }
        }

        // Row discontinuities: count transitions where row range drops by >80%
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

        // Classification
        string classification;
        if (zRange < 0.1f)
        {
            classification = "Flat";
        }
        else if (dominantZPercent > 95.0f)
        {
            classification = "FewPixels";
        }
        else if (lastActiveRow < 28 && discontinuities > 0)
        {
            classification = "Partial";
        }
        else if (uniqueZCount > 100 && lastActiveRow >= 28)
        {
            classification = "Complete";
        }
        else
        {
            classification = "Partial";
        }

        // Use pre-sanitization count if available, otherwise count current garbage
        var garbageZCount = SanitizedZCount > 0
            ? SanitizedZCount
            : zValues.Count(z => MathF.Abs(z) > 100_000f || float.IsNaN(z) || float.IsInfinity(z));

        return new TerrainMeshDiagnostic
        {
            CellX = cellX,
            CellY = cellY,
            FormId = formId,
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
            Classification = classification
        };
    }

    /// <summary>
    ///     Convert runtime vertex heights to a LandHeightmap (VHGT-compatible format).
    ///     Extracts Z values from the 33×33 vertex grid and reverse-encodes them as
    ///     cumulative sbyte deltas with a base height offset.
    /// </summary>
    public LandHeightmap ToLandHeightmap()
    {
        // Extract Z (height) values from vertex array into a 33×33 grid.
        // Vertex layout is [x,y,z, x,y,z, ...] so Z is at index i*3+2.
        var heights = new float[GridSize, GridSize];
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                heights[y, x] = Vertices[(y * GridSize + x) * 3 + 2];
            }
        }

        // Reverse-encode: convert absolute heights back to cumulative sbyte deltas.
        // The VHGT algorithm accumulates: height += delta * 8, with each row starting
        // from the reconstructed first column of the previous row.
        var heightOffset = heights[0, 0] / 8.0f;
        var deltas = new sbyte[VertexCount];

        // Track the decoder's reconstructed column-0 value (not original heights)
        // to prevent compounding row-start drift from rounding errors.
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

        return new LandHeightmap
        {
            HeightOffset = heightOffset,
            HeightDeltas = deltas,
            Offset = VertexDataOffset,
            ExactHeights = heights
        };
    }
}

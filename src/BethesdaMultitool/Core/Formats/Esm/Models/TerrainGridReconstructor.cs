using BethesdaMultitool.Core.Formats.Esm.Terrain;

namespace BethesdaMultitool.Core.Formats.Esm.Models;

/// <summary>
///     Stateless grid-fitting and interpolation algorithms used to reconstruct a canonical 33x33 terrain
///     height grid from runtime terrain vertex samples. Split out of <see cref="RuntimeTerrainMesh" /> so the
///     record retains its public surface while the heavy reconstruction logic lives in a cohesive helper.
/// </summary>
internal static class TerrainGridReconstructor
{
    private const int GridSize = RuntimeTerrainMesh.GridSize;
    private const int VertexCount = RuntimeTerrainMesh.VertexCount;

    internal static List<TerrainVertexSample> FilterZOutliers(List<TerrainVertexSample> samples)
    {
        if (samples.Count < 64)
        {
            return samples;
        }

        var heights = samples.Select(sample => sample.Z).Order().ToArray();
        var q1 = Percentile(heights, 0.25f);
        var q3 = Percentile(heights, 0.75f);
        var iqr = q3 - q1;
        var window = Math.Max(RuntimeTerrainMesh.MinTerrainOutlierWindow, iqr * 3f);
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

    internal static TerrainGridCandidate? TryBuildCandidate(
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

    internal static RuntimeTerrainMesh.TerrainGridReconstruction? TryReconstructLocalCanonicalGrid(
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
            SpacingX = RuntimeTerrainMesh.TerrainVertexSpacing,
            SpacingY = RuntimeTerrainMesh.TerrainVertexSpacing,
            AverageFitError = occupied > 0 ? fitSum / occupied : 0f,
            MaxFitError = maxFit,
            Score = occupied
        });

        return new RuntimeTerrainMesh.TerrainGridReconstruction
        {
            Heights = heights,
            SourceGridSize = EstimateSourceGridSize(cells),
            SourceSampleCount = occupied,
            SourceCoveragePercent = occupied * 100.0f / VertexCount,
            SourceCoverageMask = BuildCanonicalCoverageMask(cells),
            SourceSpacing = RuntimeTerrainMesh.TerrainVertexSpacing,
            AverageFitError = occupied > 0 ? fitSum / occupied : 0f,
            MaxFitError = maxFit,
            MinX = bounds.MinX,
            MaxX = bounds.MaxX,
            MinY = bounds.MinY,
            MaxY = bounds.MaxY,
            UsesCanonicalLocalFrame = true
        };
    }

    internal static bool[,] BuildCanonicalCoverageMask(TerrainGridCandidate candidate)
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

    internal static bool[,] BuildCanonicalCoverageMask(TerrainGridCell?[,] cells)
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

    internal static (int X, int Y, float FitError)? TryMapLocalSampleToCanonicalCell(TerrainVertexSample sample)
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
        return RuntimeTerrainMesh.CandidateGridSizes
            .OrderBy(size => Math.Abs(size - observed))
            .ThenByDescending(size => size)
            .First();
    }

    internal static float[,] BuildSourceGrid(TerrainGridCandidate candidate)
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

    internal static float[,] InterpolateToCanonicalGrid(float[,] source, int sourceGridSize)
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

    internal static int GridSizeToLodLevel(int gridSize)
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

    internal static (int X, int Y)? InferCellCoordinates(float minX, float maxX, float minY, float maxY)
    {
        var rangeX = maxX - minX;
        var rangeY = maxY - minY;
        if (rangeX < RuntimeTerrainMesh.MinTerrainSpan || rangeY < RuntimeTerrainMesh.MinTerrainSpan ||
            rangeX > RuntimeTerrainMesh.MaxTerrainSpan || rangeY > RuntimeTerrainMesh.MaxTerrainSpan)
        {
            return null;
        }

        var centerX = (minX + maxX) * 0.5f;
        var centerY = (minY + maxY) * 0.5f;
        if (MathF.Abs(centerX) < RuntimeTerrainMesh.TerrainCellWorldSize &&
            MathF.Abs(centerY) < RuntimeTerrainMesh.TerrainCellWorldSize)
        {
            return null;
        }

        return (
            (int)MathF.Floor(centerX / RuntimeTerrainMesh.TerrainCellWorldSize),
            (int)MathF.Floor(centerY / RuntimeTerrainMesh.TerrainCellWorldSize));
    }
}

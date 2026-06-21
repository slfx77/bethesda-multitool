namespace BethesdaMultitool.Core.Formats.Esm.Models;

/// <summary>A single valid runtime terrain vertex sample (buffer index plus XYZ position).</summary>
internal sealed record TerrainVertexSample(int Index, float X, float Y, float Z);

/// <summary>A reconstructed-grid height cell with the fit error of the sample that filled it.</summary>
internal sealed record TerrainGridCell(float Z, float FitError);

/// <summary>A reconstructed-grid vertex color cell with the fit error of the sample that filled it.</summary>
internal sealed record TerrainColorCell(float R, float G, float B, float FitError);

/// <summary>A reconstructed-grid vertex normal cell with the fit error of the sample that filled it.</summary>
internal sealed record TerrainNormalCell(float Nx, float Ny, float Nz, float FitError);

/// <summary>
///     A scored candidate source grid produced while fitting runtime terrain samples to a square grid.
/// </summary>
internal sealed record TerrainGridCandidate
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

/// <summary>
///     The XY bounds of a runtime terrain sample set, derived via repeated-bucket detection with a
///     min/max fallback. Used to decide whether the capture is plausible terrain and to space the grid.
/// </summary>
internal readonly record struct TerrainBounds(float MinX, float MaxX, float MinY, float MaxY)
{
    public float RangeX => MaxX - MinX;
    public float RangeY => MaxY - MinY;

    public bool IsPlausible =>
        RangeX >= RuntimeTerrainMesh.MinTerrainSpan &&
        RangeY >= RuntimeTerrainMesh.MinTerrainSpan &&
        RangeX <= RuntimeTerrainMesh.MaxTerrainSpan &&
        RangeY <= RuntimeTerrainMesh.MaxTerrainSpan;

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
            var bucket = (int)MathF.Round(value / RuntimeTerrainMesh.AxisBucketSize);
            buckets[bucket] = buckets.GetValueOrDefault(bucket) + 1;
        }

        var repeatedBuckets = buckets
            .Where(kv => kv.Value >= 3)
            .Select(kv => kv.Key)
            .OrderBy(bucket => bucket)
            .ToList();

        if (repeatedBuckets.Count >= 4)
        {
            return (repeatedBuckets[0] * RuntimeTerrainMesh.AxisBucketSize,
                repeatedBuckets[^1] * RuntimeTerrainMesh.AxisBucketSize);
        }

        valueList.Sort();
        return (valueList[0], valueList[^1]);
    }
}

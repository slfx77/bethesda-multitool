namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Process-wide gates for the geometry corruption diagnostics
///     (FALLOUT_VIEWER_GEOMETRY_VALIDATE / _SKIP_DRAW / _AUDIT). Read once into
///     <c>static readonly</c> fields so the JIT folds the disabled branches out of the per-draw hot
///     loop entirely — with everything unset this class costs one predictable branch per batch.
/// </summary>
internal static class GeometryArenaDiagnostics
{
    /// <summary>0 = off, 1 = liveness/window/packing/instance checks, 2 = + byte-hash compare.</summary>
    public static readonly int ValidateLevel = ReadLevel();

    /// <summary>True when any validation is requested (<see cref="ValidateLevel" /> &gt; 0).</summary>
    public static readonly bool Enabled = ValidateLevel > 0;

    /// <summary>Skip a draw whose validation failed (diagnosis lever, never a fix).</summary>
    public static readonly bool SkipDrawOnViolation =
        Enabled && EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.GeometryValidateSkipDraw);

    /// <summary>Emit arena alloc/free/evict audit events to the profiler JSONL trace.</summary>
    public static readonly bool AuditEnabled =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.GeometryAudit);

    /// <summary>FNV-1a 64-bit over <paramref name="bytes" /> — dependency-free content stamp used to
    /// compare the bytes a draw will read against the bytes uploaded for its submesh.</summary>
    public static ulong Fnv1a64(ReadOnlySpan<byte> bytes)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in bytes)
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash;
    }

    private static int ReadLevel()
    {
        var raw = EnvironmentVariables.Get(EnvironmentVariables.Viewer.GeometryValidate);
        return int.TryParse(raw, out var level) ? Math.Clamp(level, 0, 2) : 0;
    }
}

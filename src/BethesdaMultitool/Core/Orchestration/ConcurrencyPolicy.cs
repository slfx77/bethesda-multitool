namespace BethesdaMultitool.Core.Orchestration;

/// <summary>
///     The single way to express a degree of parallelism. Replaces the ad-hoc conventions scattered
///     across the codebase (<c>ProcessorCount</c>, <c>ProcessorCount - 1</c>,
///     <c>ProcessorCount * 2</c>, half-cores-clamped, hardcoded integers) with named presets plus a
///     uniform override chain.
///     <para>
///         Resolution precedence: explicit caller override (a CLI <c>--parallelism</c> flag) &gt;
///         environment variable &gt; preset. The result is always at least 1.
///     </para>
///     <example>
///         <code>
///         var dop = ConcurrencyPolicy.HalfCoresClamped(2, 8)
///             .WithEnvironmentOverride(EnvironmentVariables.Viewer.ReferenceDecodeConcurrency)
///             .Resolve();
///         </code>
///     </example>
/// </summary>
internal readonly record struct ConcurrencyPolicy
{
    private enum PresetKind
    {
        Fixed,
        FullCores,
        CoresMinusOne,
        DoubleCores,
        HalfCoresClamped,
    }

    private PresetKind Kind { get; init; }

    private int FixedValue { get; init; }

    private int ClampMin { get; init; }

    private int ClampMax { get; init; }

    private string? EnvironmentVariable { get; init; }

    private int EnvironmentMin { get; init; }

    private int EnvironmentMax { get; init; }

    private int? ExplicitOverride { get; init; }

    /// <summary>Degree 1 — sequential execution through the same code path.</summary>
    public static ConcurrencyPolicy Serial => Fixed(1);

    /// <summary>One worker per logical core. The default for CPU-bound batch work.</summary>
    public static ConcurrencyPolicy FullCores => new() { Kind = PresetKind.FullCores };

    /// <summary>One core left free for the UI/render thread.</summary>
    public static ConcurrencyPolicy CoresMinusOne => new() { Kind = PresetKind.CoresMinusOne };

    /// <summary>Twice the core count — for I/O-heavy work where workers block on reads.</summary>
    public static ConcurrencyPolicy DoubleCores => new() { Kind = PresetKind.DoubleCores };

    /// <summary>A fixed degree (e.g. a deliberate throttle).</summary>
    public static ConcurrencyPolicy Fixed(int degree)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(degree, 1);
        return new ConcurrencyPolicy { Kind = PresetKind.Fixed, FixedValue = degree };
    }

    /// <summary>
    ///     Half the cores, clamped to [<paramref name="min" />, <paramref name="max" />] — the
    ///     GC-aware default used by the streaming decode paths.
    /// </summary>
    public static ConcurrencyPolicy HalfCoresClamped(int min, int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(min, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, min);
        return new ConcurrencyPolicy { Kind = PresetKind.HalfCoresClamped, ClampMin = min, ClampMax = max };
    }

    /// <summary>
    ///     Lets the environment variable <paramref name="variableName" /> override the preset when
    ///     set to a positive integer (clamped to [<paramref name="min" />, <paramref name="max" />]).
    ///     Zero, negative, and unparseable values fall through to the preset — the legacy knob
    ///     semantics every existing <c>FALLOUT_*_CONCURRENCY</c> variable already has.
    /// </summary>
    public ConcurrencyPolicy WithEnvironmentOverride(string variableName, int min = 1, int max = 64) =>
        this with { EnvironmentVariable = variableName, EnvironmentMin = min, EnvironmentMax = max };

    /// <summary>Caller-supplied override (e.g. a CLI flag); ignored when null or below 1. Wins over everything.</summary>
    public ConcurrencyPolicy WithExplicitOverride(int? degree) =>
        this with { ExplicitOverride = degree is >= 1 ? degree : null };

    /// <summary>Resolves the effective degree of parallelism. Always at least 1.</summary>
    public int Resolve()
    {
        if (ExplicitOverride is { } explicitDegree)
        {
            return explicitDegree;
        }

        if (EnvironmentVariable is not null &&
            int.TryParse(
                EnvironmentVariables.Get(EnvironmentVariable),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var fromEnvironment) &&
            fromEnvironment >= 1)
        {
            return Math.Clamp(fromEnvironment, EnvironmentMin, EnvironmentMax);
        }

        var cores = Environment.ProcessorCount;
        return Kind switch
        {
            PresetKind.Fixed => Math.Max(1, FixedValue),
            PresetKind.FullCores => Math.Max(1, cores),
            PresetKind.CoresMinusOne => Math.Max(1, cores - 1),
            PresetKind.DoubleCores => Math.Max(1, cores * 2),
            PresetKind.HalfCoresClamped => Math.Clamp(cores / 2, ClampMin, ClampMax),
            _ => 1,
        };
    }
}

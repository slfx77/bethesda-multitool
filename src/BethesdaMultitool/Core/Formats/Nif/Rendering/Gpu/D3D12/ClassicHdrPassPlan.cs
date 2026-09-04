namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>The fixed draw kinds in the recovered classic HDR resolve.</summary>
internal enum ClassicHdrPassKind
{
    Downsample16,
    Adapt,
    BrightPass,
    BrightPassBlurVertical,
    BlurVertical,
    BlurHorizontal,
    Composite
}

/// <summary>Which recovered classic bloom graph consumes the retained first reduction.</summary>
internal enum ClassicHdrBloomTopology
{
    /// <summary>FO3/FNV and Oldrim: fused vertical bright-pass blur, then one horizontal blur.</summary>
    FusedBrightPassBlur = 0,

    /// <summary>TES4: separate bright pass, then N cumulative vertical/horizontal ping-pong pairs.</summary>
    Tes4SeparateBrightPassCumulative = 1
}

/// <summary>One source-to-target step in the recursive four-to-one reduction chain.</summary>
internal readonly record struct ClassicHdrReductionLevel(
    int SourceWidth,
    int SourceHeight,
    int TargetWidth,
    int TargetHeight);

internal enum HdrReductionDimensionRule
{
    FloorQuarter,
    CeilingQuarter
}

/// <summary>
///     Allocation-free description of the recovered classic HDR pass order. DownSample16 is repeated
///     until both dimensions reach one; the first result is also the bloom source. The bloom effect
///     graph is engine-family-specific: FO3/FNV and Oldrim contain one vertical BrightPassBlur draw
///     followed by one horizontal plain-blur draw, while TES4 runs a separate bright pass followed
///     by the configured number of cumulative two-axis ping-pong blur pairs.
/// </summary>
internal readonly record struct ClassicHdrPassPlan
{
    // D3D12's maximum 2-D texture dimension is 16384. Seven /4 steps reach 1; retain one spare level
    // so malformed or synthetic dimensions fail explicitly instead of overrunning descriptor arrays.
    public const int MaxReductionLevels = 8;

    // Retail accepts an integer Setting and loops its absolute value. Bound corrupt/synthetic input
    // before it becomes an unbounded command-list recording loop; shipped/default TES4 uses two.
    internal const int MaxBloomPairCount = 64;

    private ClassicHdrPassPlan(
        int sourceWidth,
        int sourceHeight,
        int downsampleDrawCount,
        bool bloomEnabled,
        HdrReductionDimensionRule dimensionRule,
        bool finalReductionInAdapt,
        ClassicHdrBloomTopology bloomTopology,
        int blurPairCount)
    {
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        DownsampleDrawCount = downsampleDrawCount;
        BloomEnabled = bloomEnabled;
        DimensionRule = dimensionRule;
        FinalReductionInAdapt = finalReductionInAdapt;
        BloomTopology = bloomTopology;
        BlurPairCount = blurPairCount;
    }

    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int DownsampleDrawCount { get; }
    public bool BloomEnabled { get; }
    public HdrReductionDimensionRule DimensionRule { get; }
    public bool FinalReductionInAdapt { get; }
    public ClassicHdrBloomTopology BloomTopology { get; }
    public int BlurPairCount { get; }
    public static int AdaptDrawCount => 1;
    public int BrightPassDrawCount =>
        BloomEnabled && BloomTopology == ClassicHdrBloomTopology.Tes4SeparateBrightPassCumulative ? 1 : 0;
    public int BrightPassBlurDrawCount =>
        BloomEnabled && BloomTopology == ClassicHdrBloomTopology.FusedBrightPassBlur ? 1 : 0;
    public int BlurDrawCount
    {
        get
        {
            if (!BloomEnabled)
            {
                return 0;
            }

            return BloomTopology == ClassicHdrBloomTopology.Tes4SeparateBrightPassCumulative
                ? BlurPairCount * 2
                : 1;
        }
    }
    public static int CompositeDrawCount => 1;

    public int TotalDrawCount =>
        DownsampleDrawCount + AdaptDrawCount + BrightPassDrawCount + BrightPassBlurDrawCount +
        BlurDrawCount + CompositeDrawCount;

    public static ClassicHdrPassPlan Create(
        int width,
        int height,
        bool bloomEnabled,
        float authoredBlurPasses)
    {
        _ = authoredBlurPasses;
        return CreateFloorQuarter(
            width,
            height,
            bloomEnabled,
            ClassicHdrBloomTopology.FusedBrightPassBlur,
            blurPairCount: 1);
    }

    /// <summary>
    ///     TES4's recovered HDR graph: one separate bright pass followed by the absolute, truncated
    ///     active blur count of cumulative two-axis pairs. The shipped active count is two.
    /// </summary>
    public static ClassicHdrPassPlan CreateTes4(
        int width,
        int height,
        bool bloomEnabled,
        float activeBlurPasses)
    {
        return CreateFloorQuarter(
            width,
            height,
            bloomEnabled,
            ClassicHdrBloomTopology.Tes4SeparateBrightPassCumulative,
            ResolveTes4BlurPairCount(activeBlurPasses));
    }

    internal static int ResolveTes4BlurPairCount(float activeBlurPasses)
    {
        if (!float.IsFinite(activeBlurPasses))
        {
            return 0;
        }

        var truncated = Math.Truncate((double)activeBlurPasses);
        var magnitude = Math.Abs(truncated);
        return (int)Math.Min(magnitude, MaxBloomPairCount);
    }

    private static ClassicHdrPassPlan CreateFloorQuarter(
        int width,
        int height,
        bool bloomEnabled,
        ClassicHdrBloomTopology bloomTopology,
        int blurPairCount)
    {
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);

        var targetWidth = width;
        var targetHeight = height;
        var levels = 0;
        do
        {
            targetWidth = Math.Max(targetWidth / 4, 1);
            targetHeight = Math.Max(targetHeight / 4, 1);
            levels++;
            if (levels > MaxReductionLevels)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(width),
                    $"Classic HDR reduction exceeds {MaxReductionLevels} levels ({width}x{height}).");
            }
        } while (targetWidth > 1 || targetHeight > 1);

        return new ClassicHdrPassPlan(
            width,
            height,
            levels,
            bloomEnabled,
            HdrReductionDimensionRule.FloorQuarter,
            false,
            bloomTopology,
            blurPairCount);
    }

    /// <summary>
    ///     Oldrim's recovered HDR chain (SSE remains unverified). Every target dimension is
    ///     max(ceil(source/4), 1). Once adaptation history exists, retail stops at the source of the
    ///     final 1x1 reduction and slot 9 fuses that last 16-tap reduction with the two-lane temporal
    ///     update. The retained slot-4 result is also the source of Oldrim's two-draw bloom effect.
    /// </summary>
    public static ClassicHdrPassPlan CreateSkyrim(
        int width,
        int height,
        bool historyAvailable,
        bool bloomEnabled)
    {
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);

        var targetWidth = CeilingQuarter(width);
        var targetHeight = CeilingQuarter(height);
        var levels = 1; // Slot 4 always renders the first retained reduction.
        while (targetWidth > 1 || targetHeight > 1)
        {
            var nextWidth = CeilingQuarter(targetWidth);
            var nextHeight = CeilingQuarter(targetHeight);
            if (historyAvailable && nextWidth == 1 && nextHeight == 1)
            {
                break;
            }

            targetWidth = nextWidth;
            targetHeight = nextHeight;
            levels++;
            if (levels > MaxReductionLevels)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(width),
                    $"Skyrim HDR reduction exceeds {MaxReductionLevels} levels ({width}x{height}).");
            }
        }

        return new ClassicHdrPassPlan(
            width,
            height,
            levels,
            bloomEnabled,
            HdrReductionDimensionRule.CeilingQuarter,
            historyAvailable,
            ClassicHdrBloomTopology.FusedBrightPassBlur,
            blurPairCount: 1);
    }

    public ClassicHdrReductionLevel GetReductionLevel(int index)
    {
        if ((uint)index >= (uint)DownsampleDrawCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var sourceWidth = SourceWidth;
        var sourceHeight = SourceHeight;
        for (var level = 0; level <= index; level++)
        {
            var targetWidth = ReduceDimension(sourceWidth);
            var targetHeight = ReduceDimension(sourceHeight);
            if (level == index)
            {
                return new ClassicHdrReductionLevel(
                    sourceWidth, sourceHeight, targetWidth, targetHeight);
            }

            sourceWidth = targetWidth;
            sourceHeight = targetHeight;
        }

        throw new InvalidOperationException("Unreachable classic HDR reduction level.");
    }

    private int ReduceDimension(int value)
    {
        return DimensionRule == HdrReductionDimensionRule.CeilingQuarter
            ? CeilingQuarter(value)
            : Math.Max(value / 4, 1);
    }

    private static int CeilingQuarter(int value)
    {
        return Math.Max(value / 4 + (value % 4 == 0 ? 0 : 1), 1);
    }

    public ClassicHdrPassKind GetPassKind(int index)
    {
        if ((uint)index >= (uint)TotalDrawCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (index < DownsampleDrawCount)
        {
            return ClassicHdrPassKind.Downsample16;
        }

        index -= DownsampleDrawCount;
        if (index-- == 0)
        {
            return ClassicHdrPassKind.Adapt;
        }

        if (BloomEnabled && index-- == 0)
        {
            return BloomTopology == ClassicHdrBloomTopology.Tes4SeparateBrightPassCumulative
                ? ClassicHdrPassKind.BrightPass
                : ClassicHdrPassKind.BrightPassBlurVertical;
        }

        if (BloomEnabled && index < BlurDrawCount)
        {
            if (BloomTopology == ClassicHdrBloomTopology.Tes4SeparateBrightPassCumulative
                && index % 2 == 0)
            {
                return ClassicHdrPassKind.BlurVertical;
            }

            return ClassicHdrPassKind.BlurHorizontal;
        }

        return ClassicHdrPassKind.Composite;
    }
}

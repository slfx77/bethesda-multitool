namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>The fixed draw kinds in the recovered classic HDR resolve.</summary>
internal enum ClassicHdrPassKind
{
    Downsample16,
    Adapt,
    BrightPassBlurVertical,
    BlurHorizontal,
    Composite
}

/// <summary>One source-to-target step in the recursive four-to-one reduction chain.</summary>
internal readonly record struct ClassicHdrReductionLevel(
    int SourceWidth,
    int SourceHeight,
    int TargetWidth,
    int TargetHeight);

/// <summary>
///     Allocation-free description of the recovered classic HDR pass order. DownSample16 is repeated
///     until both dimensions reach one; the first result is also the bloom source. The bloom effect
///     contains one vertical BrightPassBlur draw followed by one horizontal plain-blur draw. The
///     authored BlurPasses value is deliberately accepted but does not alter this topology.
/// </summary>
internal readonly record struct ClassicHdrPassPlan
{
    // D3D12's maximum 2-D texture dimension is 16384. Seven /4 steps reach 1; retain one spare level
    // so malformed or synthetic dimensions fail explicitly instead of overrunning descriptor arrays.
    public const int MaxReductionLevels = 8;

    private ClassicHdrPassPlan(
        int sourceWidth,
        int sourceHeight,
        int downsampleDrawCount,
        bool bloomEnabled)
    {
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        DownsampleDrawCount = downsampleDrawCount;
        BloomEnabled = bloomEnabled;
    }

    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int DownsampleDrawCount { get; }
    public bool BloomEnabled { get; }
    public static int AdaptDrawCount => 1;
    public int BrightPassBlurDrawCount => BloomEnabled ? 1 : 0;
    public int BlurDrawCount => BloomEnabled ? 1 : 0;
    public static int CompositeDrawCount => 1;

    public int TotalDrawCount =>
        DownsampleDrawCount + AdaptDrawCount + BrightPassBlurDrawCount + BlurDrawCount + CompositeDrawCount;

    public static ClassicHdrPassPlan Create(
        int width,
        int height,
        bool bloomEnabled,
        float authoredBlurPasses)
    {
        _ = authoredBlurPasses;
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

        return new ClassicHdrPassPlan(width, height, levels, bloomEnabled);
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
            var targetWidth = Math.Max(sourceWidth / 4, 1);
            var targetHeight = Math.Max(sourceHeight / 4, 1);
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
            return ClassicHdrPassKind.BrightPassBlurVertical;
        }

        if (BloomEnabled && index == 0)
        {
            return ClassicHdrPassKind.BlurHorizontal;
        }

        return ClassicHdrPassKind.Composite;
    }
}

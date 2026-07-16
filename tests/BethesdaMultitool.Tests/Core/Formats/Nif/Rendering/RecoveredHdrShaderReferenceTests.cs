using System.Numerics;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     CPU oracle vectors for the recovered FNV ISHDR shader math. FO3 currently shares the bounded
///     classic implementation, but remains a separate binary/capture identity gate. The helpers in this
///     file are intentionally test-only and do not call the renderer: literal results below remain an
///     independent reference when the HLSL or its constant packing changes.
/// </summary>
public sealed class RecoveredHdrShaderReferenceTests
{
    private static readonly float[][] RecoveredBrightPassKernels =
    [
        [0.106506720f, 0.786985755f, 0.106506720f],
        [0.0544885695f, 0.244201481f, 0.402619928f, 0.244201481f, 0.0544885695f],
        [0.0366327688f, 0.111280680f, 0.216745213f, 0.270682156f, 0.216745213f, 0.111280680f,
            0.0366327688f],
        [0.0276304893f, 0.0662821382f, 0.123831593f, 0.180173829f, 0.204163671f, 0.180173829f,
            0.123831593f, 0.0662821382f, 0.0276304893f],
        [0.0221905019f, 0.0455889553f, 0.0798113719f, 0.119064637f, 0.151360765f, 0.163967222f,
            0.151360765f, 0.119064637f, 0.0798113719f, 0.0455889553f, 0.0221905019f],
        [0.0185439810f, 0.0341669098f, 0.0563317202f, 0.0831085816f, 0.109719232f, 0.129617959f,
            0.137022808f, 0.129617959f, 0.109719232f, 0.0831085816f, 0.0563317202f, 0.0341669098f,
            0.0185439810f],
        [0.0159283616f, 0.0270778015f, 0.0424232148f, 0.0612547770f, 0.0815124661f, 0.0999668092f,
            0.112988554f, 0.117695801f, 0.112988554f, 0.0999668092f, 0.0815124661f, 0.0612547770f,
            0.0424232148f, 0.0270778015f, 0.0159283616f]
    ];

    [Flags]
    private enum CinematicOperation : uint
    {
        None = 0,
        Saturation = 1 << 0,
        Contrast = 1 << 1,
        Tint = 1 << 2,
        Brightness = 1 << 3,
        All = Saturation | Contrast | Tint | Brightness
    }

    [Fact]
    public void Adapt_CurrentWeightVector_MatchesIshdradapt()
    {
        var adapted = Adapt(
            previous: new Vector3(0.2f, 0.4f, 0.6f),
            current: new Vector3(1f, 0.5f, 0.25f),
            currentWeight: 0.25f,
            upperLumClamp: 1f);

        AssertVector(adapted, new Vector3(0.4f, 0.425f, 0.5125f));
    }

    [Fact]
    public void Adapt_AppliesVectorLengthClampAfterTemporalBlend()
    {
        var adapted = Adapt(
            previous: new Vector3(3f, 4f, 0f),
            current: new Vector3(3f, 4f, 0f),
            currentWeight: 0.4f,
            upperLumClamp: 1f);

        AssertVector(adapted, new Vector3(0.6f, 0.8f, 0f));
    }

    [Fact]
    public void Adapt_PreservesBlackAndSubPointZeroOneVectorLengths()
    {
        var black = Adapt(Vector3.Zero, Vector3.Zero, currentWeight: 0.5f, upperLumClamp: 1f);
        var nearBlack = Adapt(
            previous: new Vector3(0.001f, 0f, 0f),
            current: new Vector3(0.001f, 0f, 0f),
            currentWeight: 0.5f,
            upperLumClamp: 1f);

        AssertVector(black, Vector3.Zero);
        AssertVector(nearBlack, new Vector3(0.001f, 0f, 0f));
    }

    [Fact]
    public void Composite_UsesAdaptedRgbSumAndDoesNotBrightenBelowTarget()
    {
        var result = Composite(
            scene: new Vector3(0.8f, 0.6f, 0.4f),
            bloom: new Vector3(0.1f, 0.2f, 0.3f),
            adaptedAverage: new Vector3(0.4f, 0.3f, 0.2f),
            targetLum: 1.2f);

        AssertVector(result, new Vector3(0.8416667f, 0.6833333f, 0.525f));
    }

    [Fact]
    public void Composite_DarkensSceneWhenAdaptedSumExceedsTarget()
    {
        var result = Composite(
            scene: new Vector3(0.8f, 0.6f, 0.4f),
            bloom: new Vector3(0.1f, 0.2f, 0.3f),
            adaptedAverage: Vector3.One,
            targetLum: 1.2f);

        AssertVector(result, new Vector3(0.3366667f, 0.2733333f, 0.21f));
    }

    [Fact]
    public void BrightPassAlpha_RoutesFreshAdaptedSumAndCompositeConsumesIt()
    {
        var bloom = new Vector3(0.1f, 0.2f, 0.3f);
        var freshAdapted = new Vector3(0.8f, 0.7f, 0.6f);
        var staleAdapted = new Vector3(0.1f, 0.1f, 0.1f);
        var brightPassOutput = BrightPassOutput(bloom, freshAdapted);

        var active = CompositeFromBrightPass(
            scene: new Vector3(0.9f, 0.6f, 0.3f),
            brightPassOutput,
            freshAdaptedFallback: staleAdapted,
            targetLum: 1.2f,
            bloomEnabled: true);
        var disabled = CompositeFromBrightPass(
            scene: new Vector3(0.9f, 0.6f, 0.3f),
            brightPassOutput,
            freshAdaptedFallback: freshAdapted,
            targetLum: 1.2f,
            bloomEnabled: false);

        Assert.Equal(2.1f, brightPassOutput.W, 6);
        AssertVector(active, new Vector3(0.53809524f, 0.39047620f, 0.24285716f));
        AssertVector(disabled, new Vector3(0.51428574f, 0.34285715f, 0.17142858f));
    }

    [Fact]
    public void BrightPass_ThresholdsAndScalesEachTapBeforeFiltering()
    {
        var result = BrightPass(new Vector3(0.2f, 0.35f, 1.15f), clamp: 0.35f, scale: 1.5f);

        AssertVector(result, new Vector3(0f, 0f, 1.2f));
    }

    [Theory]
    [InlineData(-4f, 1)]
    [InlineData(0.5f, 1)]
    [InlineData(1.99f, 1)]
    [InlineData(3.9f, 3)]
    [InlineData(7.99f, 7)]
    [InlineData(8f, 7)]
    public void BrightPassKernel_TruncatesThenClampsRecoveredRadius(float authoredRadius, int expectedRadius)
    {
        Assert.Equal(expectedRadius, SelectBrightPassRadius(authoredRadius));
    }

    [Fact]
    public void BrightPassKernel_RecoveredRowsAreNormalizedWithoutRuntimeRenormalization()
    {
        for (var radius = 1; radius <= RecoveredBrightPassKernels.Length; radius++)
        {
            var kernel = RecoveredBrightPassKernels[radius - 1];
            Assert.Equal(radius * 2 + 1, kernel.Length);
            Assert.InRange(kernel.Sum(), 0.999999f, 1.000001f);
        }
    }

    [Fact]
    public void BrightPassKernel_UsesOneRecoveredDiagonalRowAndPerTapThreshold()
    {
        Vector3 Sample(int x, int y)
        {
            if (x != y)
                return new Vector3(100f); // A square Gaussian would incorrectly include these samples.

            return x switch
            {
                -2 => new Vector3(0.2f, 0.4f, 0.7f),
                -1 => new Vector3(0.35f, 0.5f, 0.9f),
                0 => new Vector3(0.8f, 0.3f, 0.45f),
                1 => new Vector3(1.2f, 0.6f, 0.1f),
                2 => new Vector3(0.5f, 1f, 0.35f),
                _ => Vector3.Zero
            };
        }

        var result = BrightPassBlurDiagonal(
            radius: 2,
            sample: Sample,
            clamp: 0.35f,
            scale: 1.5f);

        AssertVector(result, new Vector3(0.59538525f, 0.2037339f, 0.29046568f));
    }

    [Fact]
    public void Downsample16_OddSizeUsesFourBilinearFetchesNotSixteenPointCenters()
    {
        // For a 5x5 -> 1x1 reduction, the recovered +/-1 bilinear taps land exactly on source
        // texels (1,1), (3,1), (3,3), and (1,3). A center impulse is therefore absent. The former
        // sixteen-fetch approximation samples half-texel positions and leaks 1/16 of that impulse.
        var source = new float[5, 5];
        source[2, 2] = 16f;

        var recovered = DownsampleFourBilinear(source, targetX: 0, targetY: 0, targetWidth: 1, targetHeight: 1);
        var formerApproximation = DownsampleSixteenLinear(
            source, targetX: 0, targetY: 0, targetWidth: 1, targetHeight: 1);

        Assert.Equal(0f, recovered, 6);
        Assert.Equal(1f, formerApproximation, 6);
    }

    [Fact]
    public void Cinematic_ShippedCompositeIgnoresRetainedEnableMask()
    {
        var maskZero = Cinematic(
            color: new Vector3(0.8f, 0.4f, 0.2f),
            operations: CinematicOperation.None,
            saturation: 0.5f,
            contrastPivot: 0.125f,
            contrast: 1.2f,
            brightness: 0.9f,
            tint: new Vector3(0.6f, 0.5f, 0.4f),
            tintAmount: 0.25f);
        var maskAll = Cinematic(
            color: new Vector3(0.8f, 0.4f, 0.2f),
            operations: CinematicOperation.All,
            saturation: 0.5f,
            contrastPivot: 0.125f,
            contrast: 1.2f,
            brightness: 0.9f,
            tint: new Vector3(0.6f, 0.5f, 0.4f),
            tintAmount: 0.25f);

        // The manager expands the four bits into pfEnables, but neither shipped cinematic pixel
        // shader reads that constant. A zero mask and an all-bits mask therefore produce the same
        // fully-authored grade.
        var expected = new Vector3(0.5806856f, 0.405272f, 0.3108584f);
        AssertVector(maskZero, expected);
        AssertVector(maskAll, expected);
    }

    [Fact]
    public void Cinematic_AllOperationsUseRecoveredContrastBrightnessOrder()
    {
        var result = Cinematic(
            color: new Vector3(0.8f, 0.4f, 0.2f),
            operations: CinematicOperation.All,
            saturation: 1f,
            contrastPivot: 0.125f,
            contrast: 1.2f,
            brightness: 0.9f,
            tint: Vector3.One,
            tintAmount: 0f);

        // contrast * (brightness * color - pivot) + pivot. Reversing the two authored slots gives
        // (0.8765, 0.4445, 0.2285), so this vector catches the original port mismatch.
        AssertVector(result, new Vector3(0.839f, 0.407f, 0.191f));
    }

    private static Vector3 Adapt(
        Vector3 previous,
        Vector3 current,
        float currentWeight,
        float upperLumClamp)
    {
        var k = Math.Clamp(currentWeight, 0f, 1f);
        var adapted = previous * (1f - k) + current * k;
        var length = adapted.Length();
        var scale = MathF.Min(MathF.Max(length, 0.01f), upperLumClamp) / MathF.Max(length, 0.01f);
        return adapted * scale;
    }

    private static Vector3 Composite(
        Vector3 scene,
        Vector3 bloom,
        Vector3 adaptedAverage,
        float targetLum)
    {
        var denominator = MathF.Max(adaptedAverage.X + adaptedAverage.Y + adaptedAverage.Z, targetLum);
        return scene * (targetLum / denominator) + bloom * (0.5f / denominator);
    }

    private static Vector4 BrightPassOutput(Vector3 bloom, Vector3 freshAdaptedAverage) =>
        new(bloom, freshAdaptedAverage.X + freshAdaptedAverage.Y + freshAdaptedAverage.Z);

    private static Vector3 CompositeFromBrightPass(
        Vector3 scene,
        Vector4 brightPassOutput,
        Vector3 freshAdaptedFallback,
        float targetLum,
        bool bloomEnabled)
    {
        var adaptedSum = bloomEnabled
            ? brightPassOutput.W
            : freshAdaptedFallback.X + freshAdaptedFallback.Y + freshAdaptedFallback.Z;
        var denominator = MathF.Max(adaptedSum, targetLum);
        var bloom = bloomEnabled
            ? new Vector3(brightPassOutput.X, brightPassOutput.Y, brightPassOutput.Z)
            : Vector3.Zero;
        return scene * (targetLum / denominator) + bloom * (0.5f / denominator);
    }

    private static Vector3 BrightPass(Vector3 source, float clamp, float scale) =>
        Vector3.Max(source - new Vector3(clamp), Vector3.Zero) * scale;

    private static int SelectBrightPassRadius(float authoredRadius) =>
        Math.Clamp((int)authoredRadius, 1, 7);

    private static Vector3 BrightPassBlurDiagonal(
        int radius,
        Func<int, int, Vector3> sample,
        float clamp,
        float scale)
    {
        var kernel = RecoveredBrightPassKernels[radius - 1];
        var result = Vector3.Zero;
        for (var offset = -radius; offset <= radius; offset++)
            result += kernel[offset + radius] * BrightPass(sample(offset, offset), clamp, scale);
        return result;
    }

    private static float DownsampleFourBilinear(
        float[,] source,
        int targetX,
        int targetY,
        int targetWidth,
        int targetHeight)
    {
        var sourceHeight = source.GetLength(0);
        var sourceWidth = source.GetLength(1);
        var u = (targetX + 0.5f) / targetWidth;
        var v = (targetY + 0.5f) / targetHeight;
        var texelX = 1f / sourceWidth;
        var texelY = 1f / sourceHeight;
        return 0.25f * (
            SampleLinearClamp(source, u - texelX, v - texelY) +
            SampleLinearClamp(source, u + texelX, v - texelY) +
            SampleLinearClamp(source, u + texelX, v + texelY) +
            SampleLinearClamp(source, u - texelX, v + texelY));
    }

    private static float DownsampleSixteenLinear(
        float[,] source,
        int targetX,
        int targetY,
        int targetWidth,
        int targetHeight)
    {
        var sourceHeight = source.GetLength(0);
        var sourceWidth = source.GetLength(1);
        var u = (targetX + 0.5f) / targetWidth;
        var v = (targetY + 0.5f) / targetHeight;
        var sum = 0f;
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                sum += SampleLinearClamp(
                    source,
                    u + (x - 1.5f) / sourceWidth,
                    v + (y - 1.5f) / sourceHeight);
            }
        }
        return sum / 16f;
    }

    private static float SampleLinearClamp(float[,] source, float u, float v)
    {
        var height = source.GetLength(0);
        var width = source.GetLength(1);
        var x = u * width - 0.5f;
        var y = v * height - 0.5f;
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var tx = x - x0;
        var ty = y - y0;
        var x1 = x0 + 1;
        var y1 = y0 + 1;
        x0 = Math.Clamp(x0, 0, width - 1);
        x1 = Math.Clamp(x1, 0, width - 1);
        y0 = Math.Clamp(y0, 0, height - 1);
        y1 = Math.Clamp(y1, 0, height - 1);
        var top = source[y0, x0] * (1f - tx) + source[y0, x1] * tx;
        var bottom = source[y1, x0] * (1f - tx) + source[y1, x1] * tx;
        return top * (1f - ty) + bottom * ty;
    }

    private static Vector3 Cinematic(
        Vector3 color,
        CinematicOperation operations,
        float saturation,
        float contrastPivot,
        float contrast,
        float brightness,
        Vector3 tint,
        float tintAmount)
    {
        var luma = Vector3.Dot(color, new Vector3(0.299f, 0.587f, 0.114f));
        _ = operations; // Retained manager metadata is intentionally not consumed by the shipped PS.
        color = Vector3.Lerp(new Vector3(luma), color, saturation);
        color = Vector3.Lerp(color, luma * tint, tintAmount);
        return contrast * (brightness * color - new Vector3(contrastPivot))
               + new Vector3(contrastPivot);
    }

    private static void AssertVector(Vector3 actual, Vector3 expected)
    {
        Assert.InRange(actual.X, expected.X - 1e-5f, expected.X + 1e-5f);
        Assert.InRange(actual.Y, expected.Y - 1e-5f, expected.Y + 1e-5f);
        Assert.InRange(actual.Z, expected.Z - 1e-5f, expected.Z + 1e-5f);
    }
}

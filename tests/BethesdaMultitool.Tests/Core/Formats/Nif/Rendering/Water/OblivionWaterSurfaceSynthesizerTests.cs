using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Pins the executable-derived Oblivion FFT surface model and the one documented adaptation:
///     continuous retail evolution is sampled into the viewer's exact 32-frame loop.
/// </summary>
public sealed class OblivionWaterSurfaceSynthesizerTests
{
    [Fact]
    public void RecoveredDefaults_MatchExecutableAndIni()
    {
        Assert.Equal(32, OblivionWaterSurfaceSynthesizer.FrameCount);
        Assert.Equal(12, OblivionWaterSurfaceSynthesizer.FramesPerSecond);
        Assert.Equal(128, OblivionWaterSurfaceSynthesizer.LowResolutionTextureSize);
        Assert.Equal(256, OblivionWaterSurfaceSynthesizer.HighResolutionTextureSize);
        Assert.Equal(256, OblivionWaterSurfaceSynthesizer.TextureSize);
        Assert.True(OblivionWaterSurfaceSynthesizer.DefaultUseHighResolution);
        Assert.Equal(5f, OblivionWaterSurfaceSynthesizer.DefaultWindVelocity);
        Assert.Equal(90f, OblivionWaterSurfaceSynthesizer.DefaultWindDirectionDegrees);
        Assert.Equal(0.5f, OblivionWaterSurfaceSynthesizer.DefaultWaveAmplitude);
        Assert.Equal(1f, OblivionWaterSurfaceSynthesizer.DefaultWaveFrequency);
        Assert.Equal(9.81f, OblivionWaterSurfaceSynthesizer.Gravity);
        Assert.Equal(100000f, OblivionWaterSurfaceSynthesizer.AmplitudeDivisor);
        Assert.Equal(75f, OblivionWaterSurfaceSynthesizer.ShortWaveDampingDivisor);
        Assert.Equal(MathF.Tau / 128f, OblivionWaterSurfaceSynthesizer.WaveNumberStep, 7);
    }

    [Fact]
    public void SettingsKey_PinsRetailDefaultWaterAndIgnoresUnrelatedOptics()
    {
        // Oblivion.esm WATR 0x00000018 (DefaultWater) DATA. These override FUN_007E0ED0's
        // constructor defaults through FUN_00499570 whenever this material is selected.
        var defaultWater = WaterSurfaceParams.Default with
        {
            WindVelocity = 5f,
            WindDirection = 100f,
            WaveAmplitude = 0.15f,
            WaveFrequency = 0.7f
        };
        var unrelatedOpticalChange = defaultWater with { SunPower = 999f };
        var changedSpectrum = defaultWater with { WaveAmplitude = 0.16f };

        Assert.Equal(
            "n256-40A00000-42C80000-3E19999A-3F333333",
            OblivionWaterSurfaceSynthesizer.GetSettingsKey(defaultWater));
        Assert.Equal(
            OblivionWaterSurfaceSynthesizer.GetSettingsKey(defaultWater),
            OblivionWaterSurfaceSynthesizer.GetSettingsKey(unrelatedOpticalChange));
        Assert.NotEqual(
            OblivionWaterSurfaceSynthesizer.GetSettingsKey(defaultWater),
            OblivionWaterSurfaceSynthesizer.GetSettingsKey(changedSpectrum));
        Assert.NotEqual(
            OblivionWaterSurfaceSynthesizer.GetSettingsKey(defaultWater, useHighResolution: true),
            OblivionWaterSurfaceSynthesizer.GetSettingsKey(defaultWater, useHighResolution: false));
        Assert.Equal(
            OblivionWaterSurfaceSynthesizer.GetSettingsKey(defaultWater with { WaveAmplitude = 0f }),
            OblivionWaterSurfaceSynthesizer.GetSettingsKey(defaultWater with { WaveAmplitude = -0f }));
        var retailFrames = OblivionWaterSurfaceSynthesizer.GenerateFrames(defaultWater);
        Assert.Same(retailFrames,
            OblivionWaterSurfaceSynthesizer.GenerateFrames(unrelatedOpticalChange));
        Assert.NotEqual(
            OblivionWaterSurfaceSynthesizer.GenerateFrame(0),
            retailFrames[0]);
    }

    [Fact]
    public void WorldViewRoutesActiveWatrFieldsIntoSpectrumAndSyntheticTextureKey()
    {
        var host = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3D",
            "WorldView3DControl.Cells.cs");
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "WaterRenderer12.cs");
        var capture = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3D",
            "WorldView3DControl.SceneCapture.cs");

        Assert.Contains(".OblivionWaterSurfaceSynthesizer.GenerateFrames(",
            host, StringComparison.Ordinal);
        Assert.Contains("appearance?.Surface,", host, StringComparison.Ordinal);
        Assert.Contains("useHighResolution);", host, StringComparison.Ordinal);
        Assert.Contains(".OblivionWaterSurfaceSynthesizer.GetSettingsKey(",
            host, StringComparison.Ordinal);
        Assert.Contains(
            "$\"synthetic:oblivion-water-surface:rgba8-nomips:{settingsKey}:{i:D2}\"",
            host, StringComparison.Ordinal);
        Assert.Contains("GetOrCreateSyntheticBindlessIndex(",
            host, StringComparison.Ordinal);
        Assert.Contains("generateMips: false", host, StringComparison.Ordinal);
        Assert.Contains("OblivionWaterHighResolution", host, StringComparison.Ordinal);
        Assert.Contains("grid={1}x{1}, output=R8G8B8A8_UNorm, levels=1", host,
            StringComparison.Ordinal);
        Assert.Contains(
            "frameSource = LegacySurfaceFrameSource.OblivionFftSobel;",
            host, StringComparison.Ordinal);
        Assert.Contains("_water.SetLegacyAnimatedFrames(frames, source);",
            host, StringComparison.Ordinal);
        Assert.Contains("LastStats.WaterPipeline = LegacyWaterAnimation.TelemetryName(",
            renderer, StringComparison.Ordinal);
        Assert.Contains(
            "fields[\"waterPipeline\"] = waterStats?.WaterPipeline ?? \"not-rendered\";",
            capture, StringComparison.Ordinal);
    }

    [Fact]
    public void PhillipsSpectrum_MatchesRecoveredDirectionalEquation()
    {
        const int latticeX = 8;
        const int latticeY = 3;
        var kx = latticeX * OblivionWaterSurfaceSynthesizer.WaveNumberStep;
        var ky = latticeY * OblivionWaterSurfaceSynthesizer.WaveNumberStep;
        var kSquared = kx * kx + ky * ky;
        var largeWaveLength = 25f / 9.81f;
        var dampingLength = largeWaveLength / 75f;
        // The recovered 90-degree convention is sin(theta)*kx + cos(theta)*ky.
        var windDotK = MathF.Sin(MathF.PI / 2f) * kx + MathF.Cos(MathF.PI / 2f) * ky;
        var expected = (0.5f / 100000f) *
                       MathF.Exp(-1f / (kSquared * largeWaveLength * largeWaveLength)) *
                       windDotK * windDotK *
                       MathF.Exp(-kSquared * dampingLength * dampingLength) /
                       (kSquared * kSquared * kSquared);

        var actual = OblivionWaterSurfaceSynthesizer.EvaluatePhillipsSpectrum(latticeX, latticeY);

        Assert.InRange(actual, expected * 0.99999f, expected * 1.00001f);
        Assert.True(actual > 0f);
        Assert.Equal(0f, OblivionWaterSurfaceSynthesizer.EvaluatePhillipsSpectrum(-8, 0));
        Assert.Equal(0f, OblivionWaterSurfaceSynthesizer.EvaluatePhillipsSpectrum(8, 0,
            windDirectionDegrees: 0f));
        Assert.Equal(actual * 2f,
            OblivionWaterSurfaceSynthesizer.EvaluatePhillipsSpectrum(
                latticeX, latticeY, waveAmplitude: 1f), 7);
    }

    [Fact]
    public void PhillipsSpectrum_PopulatesThousandsOfModesThroughNyquistBand()
    {
        var activeModes = 0;
        var activeHighFrequencyModes = 0;
        for (var y = -64; y < 64; y++)
        {
            for (var x = -64; x < 64; x++)
            {
                if (OblivionWaterSurfaceSynthesizer.EvaluatePhillipsSpectrum(x, y) <= 0f)
                {
                    continue;
                }

                activeModes++;
                if (MathF.Sqrt(x * x + y * y) >= 32f)
                {
                    activeHighFrequencyModes++;
                }
            }
        }

        // The removed stand-in carried only 16 hand-selected modes. Retail's recovered FFT seed
        // populates a directional half-plane across the full 128² lattice, including the upper
        // spatial bands responsible for its fine, non-glyph normal structure.
        Assert.True(activeModes > 7000, $"Only {activeModes} recovered spectrum modes were active.");
        Assert.True(activeHighFrequencyModes > 5000,
            $"Only {activeHighFrequencyModes} high-frequency spectrum modes were active.");
    }

    [Fact]
    public void WaterHmap005Normal_UsesRecoveredAbsoluteHeightSobelKernel()
    {
        var actual = OblivionWaterSurfaceSynthesizer.ComputeNormal(
            northWest: -1f,
            north: -2f,
            northEast: -3f,
            west: -4f,
            east: 5f,
            southWest: 6f,
            south: 7f,
            southEast: 8f);
        const float expectedXGradient = 4.8f;
        const float expectedYGradient = 16f;
        var inverseLength = 1f / MathF.Sqrt(
            expectedXGradient * expectedXGradient + expectedYGradient * expectedYGradient + 1f);

        Assert.Equal(-expectedXGradient * inverseLength, actual.X, 6);
        Assert.Equal(expectedYGradient * inverseLength, actual.Y, 6);
        Assert.Equal(inverseLength, actual.Z, 6);
        Assert.Equal(0.8f, OblivionWaterSurfaceSynthesizer.DiagonalNormalWeight);
        Assert.Equal(1.6f, OblivionWaterSurfaceSynthesizer.AxialNormalWeight);
    }

    [Fact]
    public void HighResolutionFrameGenerationUsesTheStaSafeNonPumpingJoin()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Water",
            "OblivionWaterSurfaceSynthesizer.cs");

        Assert.Contains(
            "NonPumpingParallel.For(0, FrameCount, frame => frames[frame] = GenerateFrame(frame, seed));",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\n        Parallel.For(0, FrameCount", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.01f, 0.01f)]
    [InlineData(-0.01f, 0.99f)]
    [InlineData(1.65f, 1.65f)]
    [InlineData(-1.65f, -0.65f)]
    public void SpectrumSample_MatchesRecoveredNormalCdfTableBranches(float standardNormal, float expected)
    {
        // FUN_007DF580 selects +z above CDF .5 and 1-z below it; the executable's table pins
        // (.01,.504) and (1.65,.9505), making these branch results non-interpretive.
        Assert.Equal(expected,
            OblivionWaterSurfaceSynthesizer.TransformSpectrumSample(standardNormal), 6);
    }

    [Fact]
    public void HighResolutionSpectrum_UsesRecoveredSuppressedAndCopiedCorners()
    {
        Assert.True(OblivionWaterSurfaceSynthesizer.IsHighResolutionSuppressedSeedCoordinate(0, 0));
        Assert.True(OblivionWaterSurfaceSynthesizer.IsHighResolutionSuppressedSeedCoordinate(31, 31));
        Assert.False(OblivionWaterSurfaceSynthesizer.IsHighResolutionSuppressedSeedCoordinate(32, 31));
        Assert.False(OblivionWaterSurfaceSynthesizer.IsHighResolutionSuppressedSeedCoordinate(31, 32));

        Assert.Equal((0, 33),
            OblivionWaterSurfaceSynthesizer.GetHighResolutionSpectrumCopySource(0, 225)!.Value);
        Assert.Equal((33, 0),
            OblivionWaterSurfaceSynthesizer.GetHighResolutionSpectrumCopySource(225, 0)!.Value);
        Assert.Equal((63, 63),
            OblivionWaterSurfaceSynthesizer.GetHighResolutionSpectrumCopySource(255, 255)!.Value);
        Assert.Equal((64, 64),
            OblivionWaterSurfaceSynthesizer.GetHighResolutionSpectrumCopySource(256, 256)!.Value);
        Assert.Null(OblivionWaterSurfaceSynthesizer.GetHighResolutionSpectrumCopySource(31, 224));
        Assert.Null(OblivionWaterSurfaceSynthesizer.GetHighResolutionSpectrumCopySource(32, 225));
        Assert.Null(OblivionWaterSurfaceSynthesizer.GetHighResolutionSpectrumCopySource(225, 32));
        Assert.Equal(1, OblivionWaterSurfaceSynthesizer.ReverseBits(128, 8));
        Assert.Equal(1, OblivionWaterSurfaceSynthesizer.ReverseBits(64, 7));
        Assert.Equal((256, 256),
            OblivionWaterSurfaceSynthesizer.GetOppositeSeedCoordinate(0, 0, 256));
        Assert.Equal((1, 1),
            OblivionWaterSurfaceSynthesizer.GetOppositeSeedCoordinate(255, 255, 256));
    }

    [Fact]
    public void GenerateFrames_ProducesThirtyTwoHighResolutionRetailRgba8Frames()
    {
        var frames = OblivionWaterSurfaceSynthesizer.GenerateFrames();
        var lowResolutionFrames = OblivionWaterSurfaceSynthesizer.GenerateFrames(
            surface: null,
            useHighResolution: false);

        Assert.Equal(OblivionWaterSurfaceSynthesizer.FrameCount, frames.Length);
        Assert.All(frames, frame => Assert.Equal(
            OblivionWaterSurfaceSynthesizer.TextureSize * OblivionWaterSurfaceSynthesizer.TextureSize * 4,
            frame.Length));
        Assert.All(frames, frame =>
        {
            for (var pixel = 3; pixel < frame.Length; pixel += 4)
            {
                Assert.Equal(byte.MaxValue, frame[pixel]);
            }
        });
        Assert.Equal(OblivionWaterSurfaceSynthesizer.FrameCount, lowResolutionFrames.Length);
        Assert.All(lowResolutionFrames, frame => Assert.Equal(
            OblivionWaterSurfaceSynthesizer.LowResolutionTextureSize *
            OblivionWaterSurfaceSynthesizer.LowResolutionTextureSize * 4,
            frame.Length));
        Assert.NotSame(frames, lowResolutionFrames);
    }

    [Theory]
    [InlineData(-1f, 0)]
    [InlineData(0f, 128)]
    [InlineData(1f, 255)]
    public void NormalEncoding_UsesRetailEightBitTrueColorTarget(float component, int expected)
    {
        Assert.Equal(expected, (int)OblivionWaterSurfaceSynthesizer.EncodeUnorm8(component));
    }

    [Fact]
    public void FrameLoop_IsExactlySeamless()
    {
        Assert.Equal(
            OblivionWaterSurfaceSynthesizer.GenerateFrame(0),
            OblivionWaterSurfaceSynthesizer.GenerateFrame(OblivionWaterSurfaceSynthesizer.FrameCount));
        Assert.Equal(
            OblivionWaterSurfaceSynthesizer.GenerateFrame(-1),
            OblivionWaterSurfaceSynthesizer.GenerateFrame(OblivionWaterSurfaceSynthesizer.FrameCount - 1));
    }

    [Fact]
    public void Frames_ActuallyAnimateAndAreDeterministic()
    {
        var first = OblivionWaterSurfaceSynthesizer.GenerateFrame(0);
        var middle = OblivionWaterSurfaceSynthesizer.GenerateFrame(
            OblivionWaterSurfaceSynthesizer.FrameCount / 2);

        Assert.NotEqual(first, middle);
        Assert.Equal(first, OblivionWaterSurfaceSynthesizer.GenerateFrame(0));
    }
}

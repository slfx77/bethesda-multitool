using BethesdaRendererProfiler;
using Xunit;

namespace BethesdaMultitool.Tests.Profiler;

public sealed class RendererProfilerOptionsTests
{
    private static readonly string[] DocumentedCaptureOptions =
    [
        "--capture-topdown",
        "--capture-frame",
        "--capture-width",
        "--capture-height",
        "--capture-worldspace-name",
        "--capture-interior",
        "--capture-weather",
        "--capture-hour",
        "--capture-day",
        "--capture-animation-time",
        "--capture-pitch",
        "--capture-yaw",
        "--capture-settle-timeout-seconds",
        "--capture-cells",
        "--capture-worldspace",
        "--capture-center-x",
        "--capture-center-y",
        "--capture-z"
    ];

    [Fact]
    public void Usage_DocumentsEveryCaptureOption()
    {
        foreach (var option in DocumentedCaptureOptions)
        {
            Assert.Contains(option, RendererProfilerOptions.Usage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TryParse_PreservesPerspectiveDefaultsForCompatibility()
    {
        WithInput(input =>
        {
            Assert.True(RendererProfilerOptions.TryParse(["--input", input], out var options, out var error));
            Assert.Null(error);
            Assert.Equal(768, options.CaptureWidth);
            Assert.Equal(480, options.CaptureHeight);
            Assert.Null(options.CaptureYawDegrees);
            Assert.Equal(60, options.CaptureSettleTimeoutSeconds);
            Assert.Equal(0f, options.CaptureAnimationTimeSeconds);
        });
    }

    [Fact]
    public void TryParse_MapsDeterministicPerspectiveCaptureOptions()
    {
        WithInput(input =>
        {
            var capture = Path.Combine(Path.GetTempPath(), $"capture-{Guid.NewGuid():N}.png");
            var args = new[]
            {
                "--input", input,
                "--capture-frame", capture,
                "--capture-worldspace-name", "WastelandNV",
                "--capture-weather", "NVWastelandClear",
                "--capture-hour", "6.5",
                "--capture-day", "17",
                "--capture-animation-time", "42.125",
                "--capture-pitch", "-15.25",
                "--capture-yaw", "-90.5",
                "--capture-width", "1024",
                "--capture-height", "512",
                "--capture-settle-timeout-seconds", "90"
            };

            Assert.True(RendererProfilerOptions.TryParse(args, out var options, out var error));
            Assert.Null(error);
            Assert.Equal("WastelandNV", options.CaptureWorldspaceName);
            Assert.Equal("NVWastelandClear", options.CaptureWeatherName);
            Assert.Equal(6.5f, options.CaptureHour);
            Assert.Equal(17f, options.CaptureDay);
            Assert.Equal(42.125f, options.CaptureAnimationTimeSeconds);
            Assert.Equal(-15.25f, options.CapturePitchDegrees);
            Assert.Equal(-90.5f, options.CaptureYawDegrees!.Value);
            Assert.Equal(1024, options.CaptureWidth);
            Assert.Equal(512, options.CaptureHeight);
            Assert.Equal(90, options.CaptureSettleTimeoutSeconds);
        });
    }

    [Theory]
    [InlineData("--capture-width", "0", "positive integer")]
    [InlineData("--capture-height", "16385", "between 1 and 16384")]
    [InlineData("--capture-hour", "24.01", "between 0 and 24")]
    [InlineData("--capture-animation-time", "-0.01", "finite non-negative")]
    [InlineData("--capture-animation-time", "Infinity", "finite non-negative")]
    [InlineData("--capture-yaw", "NaN", "finite number")]
    [InlineData("--capture-settle-timeout-seconds", "0", "positive integer")]
    public void TryParse_RejectsInvalidCaptureValues(string option, string value, string expectedError)
    {
        WithInput(input =>
        {
            Assert.False(RendererProfilerOptions.TryParse(
                ["--input", input, option, value],
                out _,
                out var error));
            Assert.NotNull(error);
            Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static void WithInput(Action<string> assertion)
    {
        var input = Path.Combine(Path.GetTempPath(), $"profiler-options-{Guid.NewGuid():N}.esm");
        try
        {
            File.WriteAllBytes(input, []);
            assertion(input);
        }
        finally
        {
            File.Delete(input);
        }
    }
}

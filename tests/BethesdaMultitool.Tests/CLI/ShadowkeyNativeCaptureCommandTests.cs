using BethesdaMultitool.CLI.Commands.Render;
using System.CommandLine;
using Xunit;

namespace BethesdaMultitool.Tests.CLI;

/// <summary>Checks public command behavior and input/output admission without requiring native hardware.</summary>
public sealed class ShadowkeyNativeCaptureCommandTests
{
    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(4096, 0, 0)]
    [InlineData(int.MaxValue, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, -1)]
    public void InvalidStaticSelectionIsRejected(int slot, int frame, int skin)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ShadowkeyNativeCaptureOptions("models.huge", "capture.png", slot, frame, skin, false));
    }

    [Theory]
    [InlineData("fern.bin")]
    [InlineData("models.idx")]
    public void DisplayLabelsCannotReplaceTheRealPackPath(string path)
    {
        Assert.Throws<ArgumentException>(() =>
            new ShadowkeyNativeCaptureOptions(path, "capture.png", 0, 0, 0, false));
    }

    [Theory]
    [InlineData("capture.json")]
    [InlineData("models.huge")]
    public void OutputMustBeANewPngPath(string path)
    {
        Assert.Throws<ArgumentException>(() =>
            new ShadowkeyNativeCaptureOptions("models.huge", path, 0, 0, 0, false));
    }

    [Fact]
    public void CanonicalPathsAndExplicitSelectionArePreserved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "shadowkey-capture-" + Guid.NewGuid().ToString("N"));
        var options = new ShadowkeyNativeCaptureOptions(Path.Combine(directory, ".", "models.huge"),
            Path.Combine(directory, ".", "fern.png"), 175, 3, 2, true);
        Assert.Equal(Path.Combine(directory, "models.huge"), options.PackPath);
        Assert.Equal(Path.Combine(directory, "fern.png"), options.OutputPath);
        Assert.Equal(options.OutputPath + ".capture.json", options.ReceiptPath);
        Assert.Equal(175, options.Slot);
        Assert.Equal(3, options.Frame);
        Assert.Equal(2, options.Skin);
        Assert.True(options.MagentaKey);
        options.EnsureDestinationsAbsent();
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ExistingPngOrReceiptIsRefusedWithoutChangingIt(bool receipt, bool directory)
    {
        var root = Path.Combine(Path.GetTempPath(), "shadowkey-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new ShadowkeyNativeCaptureOptions(Path.Combine(root, "models.huge"),
                Path.Combine(root, "capture.png"), 0, 0, 0, false);
            var existing = receipt ? options.ReceiptPath : options.OutputPath;
            if (directory) Directory.CreateDirectory(existing);
            else File.WriteAllText(existing, "existing output sentinel");
            Assert.Throws<IOException>(options.EnsureDestinationsAbsent);
            if (directory) Assert.True(Directory.Exists(existing));
            else Assert.Equal("existing output sentinel", File.ReadAllText(existing));
            var absent = receipt ? options.OutputPath : options.ReceiptPath;
            Assert.False(File.Exists(absent));
            Assert.False(Directory.Exists(absent));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("--slot", "0")]
    [InlineData("--output", "capture.png")]
    public void SlotAndOutputAreRequired(string option, string value)
    {
        var parsed = ShadowkeyNativeCaptureCommand.Create().Parse(["models.huge", option, value]);
        Assert.NotEmpty(parsed.Errors);
    }

    [Fact]
    public void OrdinaryCaptureDefaultsToFrameZeroSkinZeroAndKeyOff()
    {
        var command = ShadowkeyNativeCaptureCommand.Create();
        var parsed = command.Parse("models.huge --slot 0 --output capture.png");
        Assert.Empty(parsed.Errors);
        var frame = Assert.IsType<Option<int>>(command.Options.Single(option => option.Name == "--frame"));
        var skin = Assert.IsType<Option<int>>(command.Options.Single(option => option.Name == "--skin"));
        var key = Assert.IsType<Option<bool>>(command.Options.Single(option => option.Name == "--magenta-key"));
        Assert.Equal(0, parsed.GetValue(frame));
        Assert.Equal(0, parsed.GetValue(skin));
        Assert.False(parsed.GetValue(key));
    }

    [Fact]
    public void ExplicitFrameSkinAndKeyAreNotSilentlyReplacedByDefaults()
    {
        var command = ShadowkeyNativeCaptureCommand.Create();
        var parsed = command.Parse("models.huge --slot 175 --output capture.png --frame 3 --skin 2 --magenta-key");
        Assert.Empty(parsed.Errors);
        var frame = Assert.IsType<Option<int>>(command.Options.Single(option => option.Name == "--frame"));
        var skin = Assert.IsType<Option<int>>(command.Options.Single(option => option.Name == "--skin"));
        var key = Assert.IsType<Option<bool>>(command.Options.Single(option => option.Name == "--magenta-key"));
        Assert.Equal(3, parsed.GetValue(frame));
        Assert.Equal(2, parsed.GetValue(skin));
        Assert.True(parsed.GetValue(key));
    }

    [Fact]
    public async Task PortableExecutionReportsUnavailableWithoutOpeningMissingInputsOrCreatingOutputs()
    {
        var root = Path.Combine(Path.GetTempPath(), "shadowkey-capture-" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "models.huge");
        var output = Path.Combine(root, "capture.png");
        var parsed = ShadowkeyNativeCaptureCommand.Create().Parse(
            [input, "--slot", "0", "--output", output]);
        Assert.Empty(parsed.Errors);
        Assert.Equal(2, await parsed.InvokeAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(input));
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".capture.json"));
        Assert.False(Directory.Exists(root));
    }
}

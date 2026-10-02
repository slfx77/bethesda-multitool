using BethesdaMultitool.CLI.Commands.Analysis;
using BethesdaMultitool.CLI.Shared;
using BethesdaMultitool.CLI.Show;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Presentation;

public sealed class ImageSpaceModifierDetailTests
{
    [Fact]
    public void FullShowIncludesOrderedTimelineTailAndRawSlots()
    {
        var modifier = new ImageSpaceModifierRecord
        {
            FormId = 0xC1CC, EditorId = "HallucinationProbe", Offset = 0x1234,
            Data = new() { AnimatableFlag = 1, Duration = 12.75f, RawPayload = [0x11223344] },
            Parameters = [new(ImageSpaceModifierParameter.BloomAlphaAddInterior,
                [new(0, 0.125f), new(12.75f, 91.375f)], [new(4.5f, -0.25f)])],
            TintColorTimeline = [new(1.5f, 0.25f, 0.5f, 0.75f, 1)],
            OrderedSubrecords = [new("\0IAD", [0xAB, 0xCD])]
        };
        var output = CliHelpers.CaptureSpectreOutput(console =>
        {
            console.Profile.Width = 180;
            Assert.True(ShowCommand.TryRender(new RecordCollection { ImageSpaceModifiers = [modifier] },
                FormIdResolver.Empty, modifier.FormId, null, new ShowRenderContext(console, true, false)));
        });
        Assert.Contains("Duration: 12.75", output);
        Assert.Contains("BloomAlphaAddInterior / Multiply", output);
        Assert.Contains("[1] t=12.75: 91.375", output);
        Assert.Contains("RGBA 0.25, 0.5, 0.75, 1", output);
        Assert.Contains("0x11223344", output);
        Assert.Contains("\\x00IAD", output);
        Assert.Contains("ABCD", output);
    }
}

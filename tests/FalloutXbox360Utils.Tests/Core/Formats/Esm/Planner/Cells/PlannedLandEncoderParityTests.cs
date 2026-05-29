using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Cells;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Cells;

public sealed class PlannedLandEncoderParityTests
{
    [Fact]
    public void EncodeRecord_Returns_Null_When_Legacy_Returns_Null()
    {
        // A flat heightmap — exercises the planner adapter's null/empty fallback.
        var heightmap = new LandHeightmap
        {
            HeightOffset = 0f,
            HeightDeltas = new sbyte[33 * 33], // All zeros.
        };
        var options = new PluginBuildOptions { CompressRecords = false };

        var legacy = LandEncoder.Encode(heightmap, visualData: null);
        var planner = PlannedLandEncoder.EncodeRecord(heightmap, visualData: null, landFormId: 0x01000800, options);

        if (legacy is null || legacy.Count == 0)
        {
            Assert.Null(planner);
            return;
        }

        var legacyBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
            "LAND", 0x01000800, 0u, legacy);
        Assert.Equal(legacyBytes, planner);
    }
}

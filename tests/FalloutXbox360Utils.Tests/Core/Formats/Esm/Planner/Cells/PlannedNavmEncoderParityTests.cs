using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Cells;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Nav;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     PlannedNavmEncoder wraps NavMeshByteRewriter.Rewrite + PluginRecordByteBuilder.
///     Parity: the wrapped output must equal direct calls to the same primitives.
/// </summary>
public sealed class PlannedNavmEncoderParityTests
{
    [Fact]
    public void EncodeRecord_Matches_Direct_Legacy_Calls()
    {
        var navm = new NavMeshRecord
        {
            FormId = 0xAA000001,
            CellFormId = 0x000ABCDE,
            RawSubrecords =
            [
                new NavMeshSubrecord("EDID", System.Text.Encoding.ASCII.GetBytes("Test\0")),
                new NavMeshSubrecord("DATA", new byte[20]),
            ],
        };
        var options = new PluginBuildOptions { CompressRecords = false };

        var plannerBytes = PlannedNavmEncoder.EncodeRecord(
            navm,
            newCellFormId: 0x01000800,
            newNavmFormId: 0x01000801,
            nvexRewrites: new Dictionary<uint, uint>(),
            options);

        var legacySubs = NavMeshByteRewriter.Rewrite(
            navm.RawSubrecords, 0x01000800, new Dictionary<uint, uint>());
        var legacyBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
            "NAVM", 0x01000801, 0u, legacySubs);

        Assert.Equal(legacyBytes, plannerBytes);
    }
}

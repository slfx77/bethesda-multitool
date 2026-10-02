using BethesdaMultitool.Tests.Core.Semantic.LoadOrder;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.CLI;

public sealed class LoadOrderExeSmokeTests
{
    [Fact]
    public async Task Show_maps_the_second_dlc_and_prints_winning_provenance_in_the_shipped_exe()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        LoadOrderRecordIndexTests.WritePlugin(directory.Path, "FalloutNV.esm", []);
        LoadOrderRecordIndexTests.WritePlugin(directory.Path, "Earlier.esm", ["FalloutNV.esm"],
            EsmTestFileBuilder.BuildRecord("NPC_", 0x01000800, 0, ("EDID", LoadOrderRecordIndexTests.Z("FirstActor"))));
        var last = LoadOrderRecordIndexTests.WritePlugin(directory.Path, "Later.esm", ["FalloutNV.esm"],
            EsmTestFileBuilder.BuildRecord("NPC_", 0x01000800, 0, ("EDID", LoadOrderRecordIndexTests.Z("SecondActor"))));
        var result = await CliExeRunner.RunAsync(["--plain", "show", last, "0x02000800", "--load-order",
            "FalloutNV.esm;Earlier.esm;Later.esm"], TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, result.Describe());
        Assert.Contains("SecondActor", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("FirstActor", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Later.esm:0x01000800 -> 0x02000800", result.StandardOutput, StringComparison.Ordinal);
    }
}

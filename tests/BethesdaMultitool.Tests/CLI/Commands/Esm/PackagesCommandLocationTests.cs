using BethesdaMultitool.CLI.Commands.Esm;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Commands.Esm;

public sealed class PackagesCommandLocationTests
{
    private static readonly FormIdResolver Resolver = new(new Dictionary<uint, string> { [7] = "Player" }, []);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(7)]
    public void UnusedLocationArmIsRawAndNeverResolvedToPlayer(byte type)
    {
        var text = PackagesCommand.FormatLocation(new PackageLocation { Type = type, Union = 7 }, Resolver);
        Assert.Contains("raw 0x00000007", text);
        Assert.DoesNotContain("Player", text);
    }

    [Fact]
    public void FormIdArmStillResolvesTheNamedRecord()
    {
        Assert.Contains("Player", PackagesCommand.FormatLocation(new PackageLocation { Type = 0, Union = 7 }, Resolver));
    }
}

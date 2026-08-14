using BethesdaMultitool.CLI.Commands.Dmp;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Commands.Dmp;

public sealed class DmpFormTypeCensusCommandTests
{
    [Theory]
    [InlineData("COBJ")]
    [InlineData("INGR")]
    public void ClassifyEmissionStatus_DirectButUnplannedTypeIsNotEmitted(string signature)
    {
        var status = DmpFormTypeCensusCommand.ClassifyEmissionStatus(
            signature,
            directEncoders: new HashSet<string>([signature], StringComparer.Ordinal),
            plannedEncoders: new HashSet<string>(StringComparer.Ordinal),
            reachableTopLevelTypes: new HashSet<string>([signature], StringComparer.Ordinal),
            nonTopLevelEmission: new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Equal("yielded but NOT PLANNED", status);
    }

    [Fact]
    public void ClassifyEmissionStatus_PlannedReachableTypeIsEmittedWithoutLegacyCatalogs()
    {
        const string signature = "STAT";
        var status = DmpFormTypeCensusCommand.ClassifyEmissionStatus(
            signature,
            directEncoders: new HashSet<string>(StringComparer.Ordinal),
            plannedEncoders: new HashSet<string>([signature], StringComparer.Ordinal),
            reachableTopLevelTypes: new HashSet<string>([signature], StringComparer.Ordinal),
            nonTopLevelEmission: new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Equal("emitted", status);
    }
}

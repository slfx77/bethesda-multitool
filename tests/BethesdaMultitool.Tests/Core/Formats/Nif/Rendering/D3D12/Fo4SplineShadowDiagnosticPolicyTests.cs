using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

public sealed class Fo4SplineShadowDiagnosticPolicyTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("01", false)]
    [InlineData(" 1", false)]
    [InlineData("1", true)]
    public void OnlyExplicitOneEnablesDiagnostic(string? value, bool expected)
        => Assert.Equal(expected, Fo4SplineShadowDiagnosticPolicy.IsEnabled(value));

    [Fact]
    public void SourceWindMarkerIsQualifiedByExactGame()
    {
        foreach (var game in Enum.GetValues<BethesdaGame>())
        {
            Assert.Equal(game == BethesdaGame.Fallout4,
                Fo4SplineShadowDiagnosticPolicy.IsCaster(game, isBendableSplineWind: true));
            Assert.False(Fo4SplineShadowDiagnosticPolicy.IsCaster(game, isBendableSplineWind: false));
        }

        Assert.False(Fo4SplineShadowDiagnosticPolicy.IsCaster((BethesdaGame)int.MaxValue, true));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void OnlyEnabledQualifiedCastersAreOmitted(bool enabled, bool isCaster, bool expected)
        => Assert.Equal(expected, Fo4SplineShadowDiagnosticPolicy.ShouldOmit(enabled, isCaster));
}

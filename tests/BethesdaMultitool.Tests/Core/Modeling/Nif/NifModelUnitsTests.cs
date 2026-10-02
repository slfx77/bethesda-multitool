using System.Numerics;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Units and basis with provenance: FNV's 1/69.99125 as Assumed with the plan's evidence when no game is
///     established, the game profile's own value and provenance when <c>bmt.game</c> names one, and the Z-up, +Y
///     forward, right-handed basis the GLB writer's rotation depends on.
/// </summary>
public class NifModelUnitsTests
{
    private static byte[] OneNode()
    {
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, -1, []);
        return builder.Build();
    }

    [Fact]
    public void Units_WithoutAGame_AreFnvAssumed_WithThePlanEvidence()
    {
        var units = Assert.IsType<SceneUnits>(Read(OneNode()).Document.Units);

        Assert.Equal(1.0 / 69.99125, units.MetersPerUnit);
        Assert.Equal(SceneValueProvenance.Assumed, units.Provenance);
        Assert.Equal(
            "BS 14\u201334 ship in FNV and FO3; game not established; FO3's 1/69.9904 differs by 0.0012%",
            units.Evidence);
    }

    /// <summary>
    ///     The option selects the profile row, with its reverse-engineered provenance and executable evidence. The FO3
    ///     rows are the control: their factor differs from the FNV default, so a reader that ignored the option fails.
    /// </summary>
    [Theory]
    [InlineData("fo3", 69.9904, "Fallout3.exe")]
    [InlineData("Fallout3", 69.9904, "Fallout3.exe")]
    [InlineData("fnv", 69.99125, "FalloutNV.exe")]
    [InlineData("FALLOUTNEWVEGAS", 69.99125, "FalloutNV.exe")]
    public void Units_FromTheGameOption_UseTheProfileValueAndProvenance(string game, double unitsPerMeter,
        string evidence)
    {
        var options = new Dictionary<string, string> { [BethesdaModelRegistration.GameOption] = game };

        var units = Read(OneNode(), options).Document.Units!;

        Assert.Equal(1.0 / unitsPerMeter, units.MetersPerUnit);
        Assert.Equal(SceneValueProvenance.ReverseEngineered, units.Provenance);
        Assert.Contains(evidence, units.Evidence);
        Assert.Contains("bmt.game=" + game, units.Evidence);
    }

    [Fact]
    public void Units_GameEvidenceOption_IsQuoted()
    {
        var options = new Dictionary<string, string>
        {
            [BethesdaModelRegistration.GameOption] = "fo3",
            [BethesdaModelRegistration.GameEvidenceOption] = "Fallout3.esm under the data root"
        };

        var units = Read(OneNode(), options).Document.Units!;

        Assert.Contains("Fallout3.esm under the data root", units.Evidence);
    }

    [Theory]
    [InlineData("Arena")]
    [InlineData("banana")]
    [InlineData("4")]
    public void Units_GameOptionNamingNoNifGame_Throws(string game)
    {
        var options = new Dictionary<string, string> { [BethesdaModelRegistration.GameOption] = game };

        Assert.Throws<ArgumentException>(() => Read(OneNode(), options));
    }

    [Fact]
    public void Units_AutoGame_IsTreatedAsNotEstablished()
    {
        var options = new Dictionary<string, string> { [BethesdaModelRegistration.GameOption] = "auto" };

        var units = Read(OneNode(), options).Document.Units!;

        Assert.Equal(SceneValueProvenance.Assumed, units.Provenance);
        Assert.Equal(NifModelUnits.AssumedGameEvidence, units.Evidence);
    }

    [Fact]
    public void Basis_IsZUp_YForward_RightHanded_NotNormalized_Assumed()
    {
        var basis = Assert.IsType<SceneSourceBasis>(Read(OneNode()).Document.SourceBasis);

        Assert.Equal(Vector3.UnitZ, basis.Up);
        Assert.Equal(Vector3.UnitY, basis.Forward);
        Assert.Null(basis.Right);
        Assert.Equal(SceneHandedness.RightHanded, basis.Handedness);
        Assert.False(basis.NormalizedByReader);
        Assert.Equal(SceneValueProvenance.Assumed, basis.Provenance);
        Assert.Equal("Gamebryo Z-up right-handed; BMT GLB exporter maps (x,y,z)\u2192(x,z,\u2212y)", basis.Evidence);
    }
}

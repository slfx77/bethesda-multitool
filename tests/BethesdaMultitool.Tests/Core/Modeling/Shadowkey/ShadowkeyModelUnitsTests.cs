using System.Numerics;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Core.Modeling.Shadowkey;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Shadowkey.ShadowkeyModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Shadowkey;

/// <summary>
///     The unit row, the two bases, the placement matrix and the game option of the Shadowkey documents (cut-2 plan
///     section 5.3 and decision D7, slice 3), pinned as literals, each with the control that must fail.
/// </summary>
public class ShadowkeyModelUnitsTests
{
    private static ShadowkeyEntity Placement(int angle2, ushort scale = 256, int x = 1000, int y = 2000, int z = -300)
    {
        return new ShadowkeyEntity(x, y, z, 0, 0, angle2, scale, 100, "p", "s");
    }

    [Fact]
    public void TheUnitRow_IsOneFiveHundredTwelfthMeter_Assumed_WithItsEvidence()
    {
        Assert.Equal(0.001953125, ShadowkeyModelUnits.MetersPerUnit);
        Assert.Equal(0.001953125, ShadowkeyModelUnits.Units.MetersPerUnit);
        Assert.Equal(SceneValueProvenance.Assumed, ShadowkeyModelUnits.Units.Provenance);
        Assert.Contains("RE-6", ShadowkeyModelUnits.Units.Evidence, StringComparison.Ordinal);
        Assert.Contains("2,025 of 8,255", ShadowkeyModelUnits.Units.Evidence, StringComparison.Ordinal);
        Assert.Equal(ShadowkeyModelUnits.MetersPerUnit, ShadowkeyModelUnits.PlacementUnits.MetersPerUnit);
        Assert.True(ShadowkeyModelUnits.PlacementUnits.Evidence!.Length < 80);
    }

    [Fact]
    public void TheBases_AreYUpForMeshesAndZUpMinusYForwardForZones()
    {
        Assert.Equal(Vector3.UnitY, ShadowkeyModelUnits.MeshBasis.Up);
        Assert.Equal(Vector3.UnitZ, ShadowkeyModelUnits.MeshBasis.Forward);
        Assert.Equal(SceneHandedness.RightHanded, ShadowkeyModelUnits.MeshBasis.Handedness);
        Assert.Equal(Vector3.UnitZ, ShadowkeyModelUnits.ZoneBasis.Up);
        Assert.Equal(-Vector3.UnitY, ShadowkeyModelUnits.ZoneBasis.Forward);
        Assert.Equal(SceneHandedness.RightHanded, ShadowkeyModelUnits.ZoneBasis.Handedness);
        Assert.Equal(SceneValueProvenance.Assumed, ShadowkeyModelUnits.ZoneBasis.Provenance);
    }

    [Fact]
    public void MeshToZone_IsTheProperMap_AndTheLegacySwapIsAReflection()
    {
        var mapped = Vector3.Transform(new Vector3(1, 2, 3), ShadowkeyModelUnits.MeshToZone);

        Assert.Equal(new Vector3(1, -3, 2), mapped);
        Assert.Equal(1f, ShadowkeyModelUnits.MeshToZone.GetDeterminant());
        // Control: the legacy viewer's (x, y, z) to (x, z, y) is a reflection, determinant -1.
        var legacy = new Matrix4x4(1, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        Assert.Equal(-1f, legacy.GetDeterminant());
    }

    [Theory]
    [InlineData(0, 256, 1f, 0f)]
    [InlineData(16384, 256, 0f, -1f)]
    [InlineData(-16384, 256, 0f, 1f)]
    [InlineData(32768, 512, -2f, 0f)]
    [InlineData(-32768, 128, -0.5f, 0f)]
    [InlineData(65536, 256, 1f, 0f)]
    public void QuarterTurns_AreExact(int angle2, int scale, float m00, float m01)
    {
        var matrix = ShadowkeyModelUnits.PlacementMatrix(Placement(angle2, (ushort)scale));

        var s = scale / 256f;
        var expected = new Matrix4x4(
            m00, m01, 0, 0,
            0, 0, s, 0,
            m01, -m00, 0, 0,
            1000, 2000, -300, 1);
        Assert.Equal(expected, matrix);
    }

    [Fact]
    public void TheYawSign_IsNegative_TheLegacyPositiveYawIsTheControl()
    {
        // A quarter turn (16384) takes mesh +X (1, 0, 0) to zone (0, -1, 0): a clockwise turn seen from +Z.
        var matrix = ShadowkeyModelUnits.PlacementMatrix(Placement(16384, x: 0, y: 0, z: 0));
        Assert.Equal(new Vector3(0, -1, 0), Vector3.Transform(Vector3.UnitX, matrix));
        // Mesh +Y (up) goes to zone +Z at the placement's scale, and mesh +Z (forward) to zone -Y before the yaw.
        var unturned = ShadowkeyModelUnits.PlacementMatrix(Placement(0, 512, 0, 0, 0));
        Assert.Equal(new Vector3(0, 0, 2), Vector3.Transform(Vector3.UnitY, unturned));
        Assert.Equal(new Vector3(0, -2, 0), Vector3.Transform(Vector3.UnitZ, unturned));
        // Control: +yaw (the legacy sign) would take +X to (0, +1, 0).
        var legacy = Matrix4x4.CreateRotationZ(16384 * MathF.Tau / 65536f);
        Assert.NotEqual(new Vector3(0, -1, 0), Vector3.Transform(Vector3.UnitX, legacy), new RoundedVectorComparer());
    }

    [Theory]
    [InlineData(8192, 256)]
    [InlineData(-5000, 300)]
    [InlineData(12345, 7424)]
    public void OtherAngles_RoundEachEntryOnce_AndAgreeWithTheComposedTransform(int angle2, int scale)
    {
        var matrix = ShadowkeyModelUnits.PlacementMatrix(Placement(angle2, (ushort)scale));

        var theta = -Math.Tau * angle2 / 65536;
        Assert.Equal((float)(scale / 256.0 * Math.Cos(theta)), matrix.M11);
        Assert.Equal((float)(scale / 256.0 * Math.Sin(theta)), matrix.M12);
        Assert.Equal(matrix.M12, matrix.M31);
        Assert.Equal(-matrix.M11, matrix.M32);
        var composed = Matrix4x4.CreateScale(scale / 256f) * ShadowkeyModelUnits.MeshToZone *
                       Matrix4x4.CreateRotationZ((float)theta) * Matrix4x4.CreateTranslation(1000, 2000, -300);
        var point = new Vector3(37, -12, 250);
        Assert.True(Vector3.Distance(Vector3.Transform(point, composed), Vector3.Transform(point, matrix)) < 0.05f);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("auto")]
    [InlineData("shadowkey")]
    [InlineData("Shadowkey")]
    public void TheGameOption_AcceptsShadowkeyOrNothing(string? game)
    {
        ShadowkeyModelUnits.RequireShadowkey(game is null ? new Dictionary<string, string>() : Game(game));
    }

    [Theory]
    [InlineData("fnv")]
    [InlineData("redguard")]
    [InlineData("Starfield")]
    public void TheGameOption_RefusesAnotherGame(string game)
    {
        Assert.Throws<ArgumentException>(() => ShadowkeyModelUnits.RequireShadowkey(Game(game)));
    }

    [Fact]
    public void TheFormatMetadata_NamesBothReaders_WithTheUnitRow()
    {
        Assert.Equal("bmt.shadowkey.mesh", new ShadowkeyMeshModelReader().FormatId);
        Assert.Equal("bmt.shadowkey.zone", new ShadowkeyZoneModelReader().FormatId);
        Assert.Equal(ShadowkeyModelFormatMetadata.MeshVariants, new ShadowkeyMeshModelReader().FormatMetadata.SupportedVariants);
        Assert.Equal(ShadowkeyModelFormatMetadata.ZoneVariants, new ShadowkeyZoneModelReader().FormatMetadata.SupportedVariants);
        Assert.Same(ShadowkeyModelUnits.Units, ShadowkeyModelFormatMetadata.Mesh.DefaultUnits);
        Assert.Contains(ShadowkeyModelFormatMetadata.Zone.AdmissionRules, static rule => rule.Id == "placements");
        Assert.Contains("0.001953125", ShadowkeyModelUnits.DescribeRow(), StringComparison.Ordinal);
    }

    /// <summary>Compares vectors to six decimals (the legacy rotation's cosine of a quarter turn is not exactly zero).</summary>
    private sealed class RoundedVectorComparer : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 x, Vector3 y)
        {
            return Vector3.Distance(x, y) < 1e-6f;
        }

        public int GetHashCode(Vector3 obj)
        {
            return 0;
        }
    }
}

using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     v3 Phase 3 — verifies <see cref="RenderableReference.TryBuild" /> filters skinned actors,
///     unresolved model paths, and NaN coordinates; and that the composed world matrix moves a
///     local origin to the REFR's world position and applies scale + rotation in the documented
///     Bethesda order. Pure CPU, no GPU.
/// </summary>
public sealed class RenderableReferenceTests
{
    [Fact]
    public void TryBuild_RefrWithIdentityTransform_ProducesIdentityWorldAtOrigin()
    {
        var placement = new PlacedReference
        {
            FormId = 0x1,
            BaseFormId = 0x2,
            ModelPath = "meshes/test.nif",
            RecordType = "REFR",
            X = 0f, Y = 0f, Z = 0f,
            RotX = 0f, RotY = 0f, RotZ = 0f,
            Scale = 1f
        };

        var built = RenderableReference.TryBuild(placement);
        Assert.NotNull(built);

        var local = new Vector3(1f, 0f, 0f);
        var transformed = Vector3.Transform(local, built.Value.WorldMatrix);
        Assert.Equal(1f, transformed.X, 5);
        Assert.Equal(0f, transformed.Y, 5);
        Assert.Equal(0f, transformed.Z, 5);
    }

    [Fact]
    public void TryBuild_RefrWithTranslation_OffsetsLocalOriginToWorldPosition()
    {
        var placement = new PlacedReference
        {
            FormId = 0x1,
            BaseFormId = 0x2,
            ModelPath = "meshes/test.nif",
            RecordType = "REFR",
            X = 1000f, Y = -500f, Z = 64f,
            Scale = 1f
        };

        var built = RenderableReference.TryBuild(placement)!.Value;
        var origin = Vector3.Transform(Vector3.Zero, built.WorldMatrix);

        Assert.Equal(1000f, origin.X, 3);
        Assert.Equal(-500f, origin.Y, 3);
        Assert.Equal(64f, origin.Z, 3);
    }

    [Fact]
    public void TryBuild_RefrWithRotZ90AndScale2_RotatesLocalUnitXToWorldNegativeY()
    {
        // Rotation: +π/2 around Z (yaw). On-screen the heading is negated (W applies Rz(−RotZ)),
        // so the yaw is inverted: local +X → world −Y. Scale: ×2 amplifies the translated component.
        var placement = new PlacedReference
        {
            FormId = 0x1,
            BaseFormId = 0x2,
            ModelPath = "meshes/test.nif",
            RecordType = "REFR",
            X = 100f, Y = 0f, Z = 0f,
            RotZ = MathF.PI / 2f,
            Scale = 2f
        };

        var built = RenderableReference.TryBuild(placement)!.Value;
        var local = new Vector3(1f, 0f, 0f);
        var world = Vector3.Transform(local, built.WorldMatrix);

        // local (1, 0, 0) → scale → (2, 0, 0) → Rz(−π/2) → (0, −2, 0) → translate → (100, −2, 0)
        Assert.Equal(100f, world.X, 3);
        Assert.Equal(-2f, world.Y, 3);
        Assert.Equal(0f, world.Z, 3);
    }

    [Fact]
    public void TryBuild_MultiAxisRotation_NegatesAllAnglesOfGamebryoEulerMatrix()
    {
        // The engine builds M = Rx·Ry·Rz (decompiled NiMatrix3::FromEulerAnglesXYZ) from the raw
        // DATA Euler angles and renders M·v. On-screen this renderer applies M(−θ) — all three angles
        // negated, same build order — because its world frame is a chirality flip of the engine's. So
        // the applied rotation is W = Rx(−RotX)·Ry(−RotY)·Rz(−RotZ). (Proven against the quarry
        // conveyor placement geometry.) Validate against W·local with all three axes non-trivial so
        // order + the all-angle negation are both pinned.
        const float rx = 0.30f, ry = 0.60f, rz = 1.10f;

        var placement = new PlacedReference
        {
            FormId = 0x1,
            BaseFormId = 0x2,
            ModelPath = "meshes/test.nif",
            RecordType = "REFR",
            X = 0f, Y = 0f, Z = 0f, // pure rotation: no translation to subtract out
            RotX = rx, RotY = ry, RotZ = rz,
            Scale = 1f
        };

        var world = RenderableReference.TryBuild(placement)!.Value.WorldMatrix;

        // Compare every local basis axis against W·local = Rx(−rx)·Ry(−ry)·Rz(−rz)·local.
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            var actual = Vector3.Transform(axis, world);
            var expected = MakeXRotation(-rx, MakeYRotation(-ry, MakeZRotation(-rz, axis)));
            Assert.Equal(expected.X, actual.X, 4);
            Assert.Equal(expected.Y, actual.Y, 4);
            Assert.Equal(expected.Z, actual.Z, 4);
        }
    }

    // Engine column-vector per-axis builders, transcribed verbatim from the decompile
    // (tools/GhidraProject/refr_rotation_decompiled.txt): each Make*Rotation writes a row-major 3x3
    // applied to a column vector (v' = R·v). Composing them with all three angles negated yields
    // the engine's M(−θ) — the renderer's on-screen orientation (chirality-flipped frame).
    private static Vector3 MakeXRotation(float a, Vector3 v)
    {
        float c = MathF.Cos(a), s = MathF.Sin(a);
        return new Vector3(v.X, c * v.Y - s * v.Z, s * v.Y + c * v.Z);
    }

    private static Vector3 MakeYRotation(float b, Vector3 v)
    {
        float c = MathF.Cos(b), s = MathF.Sin(b);
        return new Vector3(c * v.X + s * v.Z, v.Y, -s * v.X + c * v.Z);
    }

    private static Vector3 MakeZRotation(float cc, Vector3 v)
    {
        float c = MathF.Cos(cc), s = MathF.Sin(cc);
        return new Vector3(c * v.X - s * v.Y, s * v.X + c * v.Y, v.Z);
    }

    [Fact]
    public void TryBuild_ZeroScale_ClampsToUnitScaleToAvoidDegenerateMatrix()
    {
        // Some DMP captures surface REFRs with Scale=0 (parser fallback). A zero-scale matrix
        // collapses geometry to a point; clamp to 1.0 so the REFR at least renders at native
        // size. Negative scale is also clamped.
        var placement = new PlacedReference
        {
            FormId = 0x1,
            BaseFormId = 0x2,
            ModelPath = "meshes/test.nif",
            RecordType = "REFR",
            X = 0f, Y = 0f, Z = 0f,
            Scale = 0f
        };

        var built = RenderableReference.TryBuild(placement)!.Value;
        var transformed = Vector3.Transform(new Vector3(5f, 0f, 0f), built.WorldMatrix);
        Assert.Equal(5f, transformed.X, 3);
    }

    [Theory]
    [InlineData("ACHR")]
    [InlineData("ACRE")]
    public void TryBuild_ActorRecordTypes_ReturnNull(string recordType)
    {
        var placement = new PlacedReference
        {
            FormId = 0x1,
            BaseFormId = 0x2,
            ModelPath = "meshes/skinned.nif",
            RecordType = recordType,
            X = 0f, Y = 0f, Z = 0f
        };

        Assert.Null(RenderableReference.TryBuild(placement));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryBuild_NullOrEmptyModelPath_ReturnsNull(string? modelPath)
    {
        var placement = new PlacedReference
        {
            FormId = 0x1,
            BaseFormId = 0x2,
            ModelPath = modelPath,
            RecordType = "REFR",
            X = 0f, Y = 0f, Z = 0f
        };

        Assert.Null(RenderableReference.TryBuild(placement));
    }

    [Theory]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(0f, float.PositiveInfinity, 0f)]
    [InlineData(0f, 0f, float.NegativeInfinity)]
    public void TryBuild_NonFinitePosition_ReturnsNull(float x, float y, float z)
    {
        var placement = new PlacedReference
        {
            FormId = 0x1,
            BaseFormId = 0x2,
            ModelPath = "meshes/test.nif",
            RecordType = "REFR",
            X = x, Y = y, Z = z
        };

        Assert.Null(RenderableReference.TryBuild(placement));
    }

    [Fact]
    public void TryBuild_NoObjectBounds_FallsBackToDefaultRadius()
    {
        var placement = new PlacedReference
        {
            FormId = 0x1,
            BaseFormId = 0x2,
            ModelPath = "meshes/test.nif",
            RecordType = "REFR",
            X = 100f, Y = 200f, Z = 50f,
            Bounds = null,
            Scale = 1f
        };

        var built = RenderableReference.TryBuild(placement)!.Value;
        Assert.Equal(new Vector3(100f, 200f, 50f), built.BoundsCenter);
        // Generous transient fallback (raised from 256) so large OBND-less props (walls/buildings) are not
        // edge-culled for the frame or two before their true mesh radius resolves. See ComposeWorldBounds.
        Assert.Equal(1024f, built.BoundsRadius, 1);
    }

    [Fact]
    public void TryBuild_WithObjectBounds_ProducesScaledWorldSphere()
    {
        // OBND (-50, -50, -10) → (50, 50, 10) has local extents (50, 50, 10) and center at the
        // origin. With scale=2 and rotation only on Z, the world center sits at the REFR's
        // translation and the radius = |extents| * scale.
        var placement = new PlacedReference
        {
            FormId = 0x1,
            BaseFormId = 0x2,
            ModelPath = "meshes/test.nif",
            RecordType = "REFR",
            X = 1000f, Y = 0f, Z = 0f,
            Scale = 2f,
            Bounds = new ObjectBounds { X1 = -50, Y1 = -50, Z1 = -10, X2 = 50, Y2 = 50, Z2 = 10 }
        };

        var built = RenderableReference.TryBuild(placement)!.Value;
        // Local center is (0, 0, 0); transformed by world matrix it stays at (1000, 0, 0).
        Assert.Equal(1000f, built.BoundsCenter.X, 3);
        Assert.Equal(0f, built.BoundsCenter.Y, 3);
        Assert.Equal(0f, built.BoundsCenter.Z, 3);

        var expectedRadius = new Vector3(50f, 50f, 10f).Length() * 2f;
        Assert.Equal(expectedRadius, built.BoundsRadius, 2);
    }

    [Theory]
    // Standalone marker statics — filename starts with "marker" (the gap the EditorMarker
    // shape-name skip misses; their shapes are named e.g. "MarkerX:0", not "EditorMarker").
    [InlineData("meshes\\MarkerX.nif")]
    [InlineData("meshes\\MarkerXHeading.nif")]
    [InlineData("meshes\\Marker_Map.NIF")] // case-insensitive extension
    [InlineData("MarkerX.nif")] // bare filename, no directory
    [InlineData("meshes/marker_travel.nif")] // forward slashes
    // Data-defined encounter/idle markers — "markers" folder segment.
    [InlineData("meshes\\markers\\marker_encant.nif")]
    [InlineData("meshes\\MARKERS\\idle.nif")] // case-insensitive segment
    public void IsMarkerModelPath_MarkerObjects_ReturnTrue(string path)
        => Assert.True(RenderableReference.IsMarkerModelPath(path));

    [Theory]
    [InlineData("meshes\\architecture\\market.nif")] // "market" != "marker" prefix
    [InlineData("meshes\\supermarket\\shelf.nif")] // folder contains "market", not a marker
    [InlineData("meshes\\landscape\\rock01.nif")]
    [InlineData("meshes\\markers2\\thing.nif")] // segment "markers2" != "markers"
    [InlineData(null)]
    [InlineData("")]
    public void IsMarkerModelPath_NonMarkers_ReturnFalse(string? path)
        => Assert.False(RenderableReference.IsMarkerModelPath(path));

    [Fact]
    public void TryBuild_MarkerModelPath_SetsIsMarker()
    {
        var marker = new PlacedReference
        {
            FormId = 0x1, BaseFormId = 0x2, RecordType = "REFR",
            ModelPath = "meshes\\MarkerXHeading.nif", X = 0f, Y = 0f, Z = 0f, Scale = 1f
        };
        var normal = marker with { ModelPath = "meshes\\architecture\\market.nif" };

        Assert.True(RenderableReference.TryBuild(marker)!.Value.IsMarker);
        Assert.False(RenderableReference.TryBuild(normal)!.Value.IsMarker);
    }

    [Theory]
    [InlineData("meshes\\architecture\\strip\\Imposter\\OverpassSectionLo01_Imposter.NIF")] // segment + suffix
    [InlineData("meshes\\foo\\bar_imposter.nif")] // suffix only
    [InlineData("meshes\\architecture\\IMPOSTER\\thing.nif")] // segment, case-insensitive
    public void IsImposterModelPath_Imposters_ReturnTrue(string path)
        => Assert.True(RenderableReference.IsImposterModelPath(path));

    [Theory]
    [InlineData("meshes\\clutter\\composter.nif")] // ends "composter.nif", not "_imposter.nif"
    [InlineData("meshes\\imposters\\x.nif")] // plural segment "imposters" != "imposter"
    [InlineData("meshes\\architecture\\overpasssectionlo01.nif")]
    [InlineData(null)]
    [InlineData("")]
    public void IsImposterModelPath_NonImposters_ReturnFalse(string? path)
        => Assert.False(RenderableReference.IsImposterModelPath(path));

    [Fact]
    public void TryBuild_ImposterModelPath_SetsIsImposter()
    {
        var imposter = new PlacedReference
        {
            FormId = 0x1, BaseFormId = 0x2, RecordType = "REFR",
            ModelPath = "meshes\\architecture\\strip\\imposter\\x_imposter.nif",
            X = 0f, Y = 0f, Z = 0f, Scale = 1f
        };
        var full = imposter with { ModelPath = "meshes\\architecture\\overpasssectionlo01.nif" };

        Assert.True(RenderableReference.TryBuild(imposter)!.Value.IsImposter);
        Assert.False(RenderableReference.TryBuild(full)!.Value.IsImposter);
    }
}
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
    public void TryBuild_RefrWithRotZ90AndScale2_RotatesLocalUnitXToWorldY()
    {
        // Rotation: +π/2 around Z (yaw) takes local +X → world +Y (right-handed, Z-up).
        // Scale: ×2 amplifies the translated component.
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

        // local (1, 0, 0) → scale → (2, 0, 0) → Rz(π/2) → (0, 2, 0) → translate → (100, 2, 0)
        Assert.Equal(100f, world.X, 3);
        Assert.Equal(2f, world.Y, 3);
        Assert.Equal(0f, world.Z, 3);
    }

    [Fact]
    public void TryBuild_MultiAxisRotation_MatchesGamebryoNifSkopeEulerConvention()
    {
        // Regression guard for the rotation-composition order. The authoritative Gamebryo /
        // NIF Euler convention (NifSkope Matrix::fromEuler) builds, for a column vector,
        // world = Rx * Ry * Rz * local. For single-axis placements the order is irrelevant,
        // so a reversed product (Rz * Ry * Rx) still looks correct for yaw-only props — which
        // is exactly why the bug only showed on multi-axis objects like the HELIOS One
        // collectors. We therefore validate against a rotation with ALL THREE axes non-trivial.
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

        // Compare every local basis axis against the NifSkope column-vector reference. If all
        // three rotated axes match, the full 3x3 rotation matches.
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            var actual = Vector3.Transform(axis, world);
            var expected = NifSkopeFromEulerColumn(rx, ry, rz, axis);
            Assert.Equal(expected.X, actual.X, 4);
            Assert.Equal(expected.Y, actual.Y, 4);
            Assert.Equal(expected.Z, actual.Z, 4);
        }
    }

    /// <summary>
    ///     Reference implementation of NifSkope's <c>Matrix::fromEuler</c> (niftypes.cpp),
    ///     i.e. column-vector <c>world = Rx * Ry * Rz * local</c>. Returns the world-space
    ///     direction of a local basis vector under that convention.
    /// </summary>
    private static Vector3 NifSkopeFromEulerColumn(float x, float y, float z, Vector3 local)
    {
        float sinX = MathF.Sin(x), cosX = MathF.Cos(x);
        float sinY = MathF.Sin(y), cosY = MathF.Cos(y);
        float sinZ = MathF.Sin(z), cosZ = MathF.Cos(z);

        var m00 = cosY * cosZ;
        var m01 = -cosY * sinZ;
        var m02 = sinY;
        var m10 = sinX * sinY * cosZ + sinZ * cosX;
        var m11 = cosX * cosZ - sinX * sinY * sinZ;
        var m12 = -sinX * cosY;
        var m20 = sinX * sinZ - cosX * sinY * cosZ;
        var m21 = cosX * sinY * sinZ + sinX * cosZ;
        var m22 = cosX * cosY;

        return new Vector3(
            m00 * local.X + m01 * local.Y + m02 * local.Z,
            m10 * local.X + m11 * local.Y + m12 * local.Z,
            m20 * local.X + m21 * local.Y + m22 * local.Z);
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
        Assert.Equal(256f, built.BoundsRadius, 1);
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
}

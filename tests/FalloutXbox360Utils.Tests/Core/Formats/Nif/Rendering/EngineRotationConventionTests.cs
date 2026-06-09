using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Ground-truth guard for <see cref="RenderableReference.ComposeWorldMatrix" />, derived from
///     the ENGINE itself (not NifSkope). The Xbox 360 MemDebug XEX was decompiled (see
///     tools/GhidraProject/DecompileRefrRotation.java + refr_rotation_decompiled.txt) to read the
///     engine's per-axis rotation builders:
///     <list type="bullet">
///       <item><c>NiMatrix3::MakeXRotation</c> VA 0x8235B988</item>
///       <item><c>NiMatrix3::MakeYRotation</c> VA 0x82284BF0</item>
///       <item><c>NiMatrix3::MakeZRotation</c> VA 0x822EED88</item>
///     </list>
///     Each builds the STANDARD right-handed column-vector rotation matrix (v' = R·v):
///     <code>
///       Rx = [[1,0,0],[0,c,-s],[0,s,c]]
///       Ry = [[c,0,s],[0,1,0],[-s,0,c]]
///       Rz = [[c,-s,0],[s,c,0],[0,0,1]]
///     </code>
///     The engine BUILDS <c>M = Rx·Ry·Rz</c> from these (with the raw DATA Euler angles, no
///     per-angle negation — confirmed in <c>GetOrientation</c>). On-screen this renderer must
///     negate ONLY the yaw angle to match the game: the heading is the opposite hand from the
///     renderer's world Z, while pitch/roll are correct as built. So the applied rotation is
///     <c>W = Rx(RotX)·Ry(RotY)·Rz(−RotZ)</c>. This was pinned by two empirical states: plain
///     <c>M</c> rendered yaw wrong (pitch/roll right); the full transpose <c>Mᵀ</c> rendered yaw
///     right but pitch/roll wrong (the inverted pipes in Lucky38World). Negating RotZ alone is the
///     only transform consistent with both. These tests assert
///     <c>Vector3.Transform(v, WorldMatrix) == W·v</c>; if a future edit drops the yaw negation or
///     re-introduces the transpose in <see cref="RenderableReference.ComposeWorldMatrix" />, they fail.
/// </summary>
public sealed class EngineRotationConventionTests
{
    private const float Theta = 0.7f; // arbitrary non-trivial angle

    private static Matrix4x4 WorldFor(float rx, float ry, float rz)
    {
        var placement = new PlacedReference
        {
            FormId = 0x1,
            BaseFormId = 0x2,
            ModelPath = "meshes/test.nif",
            RecordType = "REFR",
            X = 0f, Y = 0f, Z = 0f, // isolate rotation
            RotX = rx, RotY = ry, RotZ = rz,
            Scale = 1f
        };
        return RenderableReference.TryBuild(placement)!.Value.WorldMatrix;
    }

    private static void AssertAxis(Matrix4x4 world, Vector3 axis, Vector3 expectedEngine)
    {
        var actual = Vector3.Transform(axis, world);
        Assert.Equal(expectedEngine.X, actual.X, 4);
        Assert.Equal(expectedEngine.Y, actual.Y, 4);
        Assert.Equal(expectedEngine.Z, actual.Z, 4);
    }

    [Fact]
    public void PureRotX_MatchesEngineMakeXRotation()
    {
        // Pitch is NOT negated: on-screen rotation is the forward builder Rx(+Theta).
        float c = MathF.Cos(Theta), s = MathF.Sin(Theta);
        var world = WorldFor(Theta, 0f, 0f);
        AssertAxis(world, Vector3.UnitX, new Vector3(1f, 0f, 0f));
        AssertAxis(world, Vector3.UnitY, new Vector3(0f, c, s));
        AssertAxis(world, Vector3.UnitZ, new Vector3(0f, -s, c));
    }

    [Fact]
    public void PureRotY_MatchesEngineMakeYRotation()
    {
        // Roll is NOT negated: on-screen rotation is the forward builder Ry(+Theta).
        float c = MathF.Cos(Theta), s = MathF.Sin(Theta);
        var world = WorldFor(0f, Theta, 0f);
        AssertAxis(world, Vector3.UnitX, new Vector3(c, 0f, -s));
        AssertAxis(world, Vector3.UnitY, new Vector3(0f, 1f, 0f));
        AssertAxis(world, Vector3.UnitZ, new Vector3(s, 0f, c));
    }

    // Engine column-vector per-axis matrices, transcribed verbatim from the decompile
    // (refr_rotation_decompiled.txt): each Make*Rotation writes a row-major 3x3.
    private static Vector3 EngineRx(float a, Vector3 v)
    {
        float c = MathF.Cos(a), s = MathF.Sin(a);
        return new Vector3(v.X, c * v.Y - s * v.Z, s * v.Y + c * v.Z);
    }

    private static Vector3 EngineRy(float b, Vector3 v)
    {
        float c = MathF.Cos(b), s = MathF.Sin(b);
        return new Vector3(c * v.X + s * v.Z, v.Y, -s * v.X + c * v.Z);
    }

    private static Vector3 EngineRz(float cc, Vector3 v)
    {
        float c = MathF.Cos(cc), s = MathF.Sin(cc);
        return new Vector3(c * v.X - s * v.Y, s * v.X + c * v.Y, v.Z);
    }

    [Fact]
    public void MultiAxis_AppliesEngineEulerWithYawNegated()
    {
        // FromEulerAnglesXYZ (VA 0x82E20B38) builds M = Rx · (Ry · Rz) and applies it column-vector:
        // v' = Rx(Ry(Rz(v))). On-screen this renderer negates ONLY the yaw angle, so the applied
        // rotation is W = Rx(rx)·Ry(ry)·Rz(-rz). Assert the viewer's row-vector WorldMatrix
        // reproduces W·v for a genuinely multi-axis placement (the case single-axis tests can't cover).
        float rx = 0.21f, ry = 0.34f, rz = 0.78f;
        var world = WorldFor(rx, ry, rz);
        foreach (var v in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new Vector3(1f, 2f, 3f) })
        {
            // On-screen = W·v = Rx(rx)·Ry(ry)·Rz(-rz)·v — engine builders, yaw angle negated.
            var expected = EngineRx(rx, EngineRy(ry, EngineRz(-rz, v)));
            var actual = Vector3.Transform(v, world);
            Assert.Equal(expected.X, actual.X, 4);
            Assert.Equal(expected.Y, actual.Y, 4);
            Assert.Equal(expected.Z, actual.Z, 4);
        }
    }

    [Fact]
    public void PureRotZ_Yaw_AppliesInverseOfEngineMakeZRotation()
    {
        // The load-bearing case. The renderer negates the yaw angle, so on-screen +RotZ applies
        // Rz(−RotZ) — sending +X → −Y (the inverse of the engine's MakeZRotation). Plain M (sending
        // +X → +Y) is what made diagonal roads/walls render ~90° off while cardinal ones looked fine.
        float c = MathF.Cos(Theta), s = MathF.Sin(Theta);
        var world = WorldFor(0f, 0f, Theta);
        AssertAxis(world, Vector3.UnitX, new Vector3(c, -s, 0f));
        AssertAxis(world, Vector3.UnitY, new Vector3(s, c, 0f));
        AssertAxis(world, Vector3.UnitZ, new Vector3(0f, 0f, 1f));
    }
}

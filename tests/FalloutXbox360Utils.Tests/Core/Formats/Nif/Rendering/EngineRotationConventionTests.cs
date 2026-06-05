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
///     This test asserts that, for a single-axis placement, our row-vector
///     <c>Vector3.Transform(v, WorldMatrix)</c> reproduces the engine's column-vector R·v exactly.
///     It passing confirms the renderer's rotation convention matches the engine for pure yaw /
///     pitch / roll (and pure yaw being engine-exact rules the placement matrix OUT as a source of
///     systematic yaw error). If a future edit "fixes" yaw by negating/transposing RotZ, this
///     test fails — that change would diverge from the decompiled engine truth.
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
        float c = MathF.Cos(Theta), s = MathF.Sin(Theta);
        var world = WorldFor(Theta, 0f, 0f);
        AssertAxis(world, Vector3.UnitX, new Vector3(1f, 0f, 0f));
        AssertAxis(world, Vector3.UnitY, new Vector3(0f, c, s));
        AssertAxis(world, Vector3.UnitZ, new Vector3(0f, -s, c));
    }

    [Fact]
    public void PureRotY_MatchesEngineMakeYRotation()
    {
        float c = MathF.Cos(Theta), s = MathF.Sin(Theta);
        var world = WorldFor(0f, Theta, 0f);
        AssertAxis(world, Vector3.UnitX, new Vector3(c, 0f, -s));
        AssertAxis(world, Vector3.UnitY, new Vector3(0f, 1f, 0f));
        AssertAxis(world, Vector3.UnitZ, new Vector3(s, 0f, c));
    }

    [Fact]
    public void PureRotZ_Yaw_MatchesEngineMakeZRotation()
    {
        // The load-bearing case for the reported "yaw is wrong" symptom: +RotZ sends +X → +Y
        // (CCW about +Z), exactly as the engine's NiMatrix3::MakeZRotation. So the placement
        // matrix is engine-correct for yaw — a systematic yaw error cannot originate here.
        float c = MathF.Cos(Theta), s = MathF.Sin(Theta);
        var world = WorldFor(0f, 0f, Theta);
        AssertAxis(world, Vector3.UnitX, new Vector3(c, s, 0f));
        AssertAxis(world, Vector3.UnitY, new Vector3(-s, c, 0f));
        AssertAxis(world, Vector3.UnitZ, new Vector3(0f, 0f, 1f));
    }
}

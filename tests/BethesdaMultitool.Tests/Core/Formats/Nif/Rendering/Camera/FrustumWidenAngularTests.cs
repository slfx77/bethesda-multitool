using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Covers the angular widening the tolerant reference cull cache relies on. The cache reuses a
///     survivor set across small camera rotations, which is only sound if the establishment frustum
///     was widened enough to contain every accepted orientation — so the superset property below is
///     the correctness argument for the whole optimisation, not a nicety.
/// </summary>
public sealed class FrustumWidenAngularTests
{
    private const float FovY = MathF.PI / 3f;   // 60°, the CameraState default
    private const float Aspect = 16f / 9f;
    private const float Near = 16f;
    private const float Far = 400_000f;

    /// <summary>
    ///     The LIVE camera convention: a standard projection post-multiplied by the reversed-Z remap
    ///     (CameraState.GetProjectionMatrix). This matters — under reversed-Z the plane extracted into
    ///     <c>Frustum.Near</c> is the world FAR plane and its normal points backward along the view, so
    ///     any widening that derives the frustum axis from it rotates the side planes the wrong way and
    ///     NARROWS the frustum. Testing only the non-reversed convention hid exactly that bug.
    /// </summary>
    private static readonly Matrix4x4 ReverseZ = new(
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, -1f, 0f,
        0f, 0f, 1f, 1f);

    private static Matrix4x4 PerspectiveViewProj(Vector3 eye, Vector3 forward) =>
        Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitZ) *
        Matrix4x4.CreatePerspectiveFieldOfView(FovY, Aspect, Near, Far) * ReverseZ;

    private static Matrix4x4 StandardDepthViewProj(Vector3 eye, Vector3 forward) =>
        Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitZ) *
        Matrix4x4.CreatePerspectiveFieldOfView(FovY, Aspect, Near, Far);

    private static Vector3 ForwardFromYawPitch(float yaw, float pitch)
    {
        var cp = MathF.Cos(pitch);
        return new Vector3(MathF.Sin(yaw) * cp, MathF.Cos(yaw) * cp, MathF.Sin(pitch));
    }

    [Fact]
    public void WidenAngular_ContainsEveryRotationWithinTheSlack()
    {
        const float slack = 4f * MathF.PI / 180f;
        const float reach = 99_202f;   // the cull's max broadphase reach at 16 cells
        var eye = new Vector3(12_345f, -6_789f, 2_048f);

        var baseForward = ForwardFromYawPitch(0.7f, -0.15f);
        var widened = Frustum
            .FromViewProjection(PerspectiveViewProj(eye, baseForward))
            .WidenAngular(slack, reach, out var applied);
        Assert.True(applied);

        // Sample orientations inside the slack cone, and points inside each rotated exact frustum.
        var random = new Random(20260801);
        var checkedPoints = 0;
        for (var rotation = 0; rotation < 120; rotation++)
        {
            // A rotation of at most `slack` about an arbitrary axis.
            var axis = Vector3.Normalize(new Vector3(
                (float)random.NextDouble() - 0.5f,
                (float)random.NextDouble() - 0.5f,
                (float)random.NextDouble() - 0.5f));
            var angle = slack * (float)random.NextDouble();
            var rotated = Vector3.Normalize(
                Vector3.Transform(baseForward, Matrix4x4.CreateFromAxisAngle(axis, angle)));

            var exact = Frustum.FromViewProjection(PerspectiveViewProj(eye, rotated));

            for (var i = 0; i < 60; i++)
            {
                var point = eye + new Vector3(
                    ((float)random.NextDouble() - 0.5f) * 2f * reach,
                    ((float)random.NextDouble() - 0.5f) * 2f * reach,
                    ((float)random.NextDouble() - 0.5f) * 2f * reach);

                if (!exact.IntersectsSphere(point, 0f))
                {
                    continue;
                }

                checkedPoints++;
                Assert.True(
                    widened.IntersectsSphere(point, 0f),
                    $"point {point} is inside a frustum rotated {angle * 180f / MathF.PI:0.00}° " +
                    "from the establishment pose but outside the widened frustum");
            }
        }

        Assert.True(checkedPoints > 500, $"sampling was too sparse to be meaningful ({checkedPoints})");
    }

    [Theory]
    [InlineData(true)]   // reversed-Z, the live camera convention
    [InlineData(false)]  // standard depth
    public void WidenAngular_OpensEachSideHalfAngleByTheSlack_UnderEitherDepthConvention(bool reverseZ)
    {
        const float slack = 6f * MathF.PI / 180f;
        var forward = Vector3.UnitY;
        var viewProj = reverseZ
            ? PerspectiveViewProj(Vector3.Zero, forward)
            : StandardDepthViewProj(Vector3.Zero, forward);
        var frustum = Frustum.FromViewProjection(viewProj);
        var widened = frustum.WidenAngular(slack, 1_000f, out var applied);
        Assert.True(applied);

        // Half-angle between the view direction and a side plane is 90° minus the angle to its normal.
        static float HalfAngle(Vector3 forward, Plane plane) =>
            (MathF.PI / 2f) - MathF.Acos(Math.Clamp(Vector3.Dot(forward, plane.Normal), -1f, 1f));

        // Each half-angle must GROW by exactly the slack. A sign error in the axis derivation shows up
        // here as a shrink of the same magnitude, which is what culled geometry at the viewport edges.
        Assert.Equal(HalfAngle(forward, frustum.Left) + slack, HalfAngle(forward, widened.Left), 4);
        Assert.Equal(HalfAngle(forward, frustum.Right) + slack, HalfAngle(forward, widened.Right), 4);
        Assert.Equal(HalfAngle(forward, frustum.Top) + slack, HalfAngle(forward, widened.Top), 4);
        Assert.Equal(HalfAngle(forward, frustum.Bottom) + slack, HalfAngle(forward, widened.Bottom), 4);
    }

    [Fact]
    public void WidenAngular_NeverShrinksTheFrustum_ForAPointOnTheViewportEdge()
    {
        // Direct regression guard for the reported symptom: a point just inside the exact frustum's
        // lateral edge must survive the widened frustum. Under the sign bug it did not.
        const float slack = 4f * MathF.PI / 180f;
        var eye = new Vector3(1_000f, 2_000f, 300f);
        var forward = Vector3.UnitY;
        var frustum = Frustum.FromViewProjection(PerspectiveViewProj(eye, forward));
        var widened = frustum.WidenAngular(slack, 100_000f, out var applied);
        Assert.True(applied);

        // Horizontal half-angle for a 60 deg vertical FOV at 16:9.
        var halfX = MathF.Atan(MathF.Tan(FovY / 2f) * Aspect);
        const float distance = 30_000f;

        foreach (var sign in new[] { -1f, 1f })
        {
            // 99.5% of the way to the lateral edge — unambiguously visible.
            var edge = eye + new Vector3(sign * distance * MathF.Tan(halfX * 0.995f), distance, 0f);
            Assert.True(frustum.IntersectsSphere(edge, 0f), "test point should be inside the exact frustum");
            Assert.True(widened.IntersectsSphere(edge, 0f), "widening must never cull a visible point");
        }
    }

    [Fact]
    public void WidenAngular_DeclinesOrthographicProjections()
    {
        // The headless capture and top-down paths cull with an ORTHOGRAPHIC viewProj while still
        // supplying a camera pose. An ortho frustum has parallel side planes and no apex to rotate
        // about, so widening must decline and leave the caller on an exact-orientation compare —
        // otherwise the apex solve is singular and the returned planes are meaningless.
        var ortho = Matrix4x4.CreateLookAt(new Vector3(0f, -10_000f, 5_000f), Vector3.Zero, Vector3.UnitZ) *
                    Matrix4x4.CreateOrthographic(20_000f, 12_000f, 1f, 100_000f);

        var frustum = Frustum.FromViewProjection(ortho);
        var result = frustum.WidenAngular(4f * MathF.PI / 180f, 50_000f, out var applied);

        Assert.False(applied);
        Assert.Equal(frustum, result);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void WidenAngular_NonPositiveSlackIsANoOp(float slack)
    {
        var frustum = Frustum.FromViewProjection(PerspectiveViewProj(Vector3.Zero, Vector3.UnitY));
        var result = frustum.WidenAngular(slack, 1_000f, out var applied);

        Assert.False(applied);
        Assert.Equal(frustum, result);
    }

    [Fact]
    public void WidenAngular_KeepsTheExactFrustumAsASubset()
    {
        // Widening must never REMOVE anything: the cull would then drop references the exact frustum
        // accepts, which the draw-time refilter can only prune further, never restore.
        const float slack = 8f * MathF.PI / 180f;
        var eye = new Vector3(-4_000f, 9_000f, 700f);
        var forward = ForwardFromYawPitch(-2.1f, 0.3f);
        var exact = Frustum.FromViewProjection(PerspectiveViewProj(eye, forward));
        var widened = exact.WidenAngular(slack, 80_000f, out var applied);
        Assert.True(applied);

        var random = new Random(4242);
        for (var i = 0; i < 4000; i++)
        {
            var point = eye + new Vector3(
                ((float)random.NextDouble() - 0.5f) * 120_000f,
                ((float)random.NextDouble() - 0.5f) * 120_000f,
                ((float)random.NextDouble() - 0.5f) * 120_000f);

            if (exact.IntersectsSphere(point, 0f))
            {
                Assert.True(widened.IntersectsSphere(point, 0f));
            }
        }
    }
}

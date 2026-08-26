using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Camera;

public sealed class TrimetricViewProjBuilderTests
{
    /// <summary>
    ///     Content boxes spanning the capture corpus: a small room, a real mid-sized interior
    ///     (GunRunnerHQInterior's box), a long corridor, and a worldspace-scale extent.
    /// </summary>
    public static TheoryData<float, float, float, float, float, float> ContentBoxes => new()
    {
        { -400f, 400f, -400f, 400f, 0f, 300f },
        { 5269f, 6845f, -202f, 1226f, -100f, 500f },
        { -6000f, 6000f, -1200f, 1200f, 0f, 400f },
        { -20000f, 20000f, -20000f, 20000f, -2000f, 2000f }
    };

    private static readonly float[] CompassYaws =
    [
        TrimetricViewProjBuilder.YawDegrees,
        TrimetricViewProjBuilder.YawDegrees + 90f,
        TrimetricViewProjBuilder.YawDegrees + 180f,
        TrimetricViewProjBuilder.YawDegrees + 270f
    ];

    /// <summary>
    ///     The framed centre must project to the NDC origin at every yaw. The original
    ///     implementation failed this by a subject-size-dependent margin (up to several NDC units
    ///     for small rooms): <c>CreateLookAt(eye, eye + forward, up)</c> at the 10⁶ stand-off
    ///     rounds the look direction's cos30°·sin30° component to exactly 7/16 in float32, a fixed
    ///     0.0045 rad error that displaced every frame ~4,040 world units in the image plane and
    ///     pushed small subjects entirely off-screen.
    /// </summary>
    [Theory]
    [MemberData(nameof(ContentBoxes))]
    public void Build_ProjectsContentCentreToNdcOrigin_AtEveryCompassYaw(
        float minX, float maxX, float minY, float maxY, float minZ, float maxZ)
    {
        var centre = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
        foreach (var yaw in CompassYaws)
        {
            var tri = TrimetricViewProjBuilder.Build(
                minX, maxX, minY, maxY, minZ, maxZ, clipWorldZMax: null, yawDegrees: yaw);
            var clip = Vector4.Transform(new Vector4(centre, 1f), tri.ViewProj);

            Assert.True(clip.W > 0f, $"yaw {yaw}: w={clip.W}");
            var ndcX = clip.X / clip.W;
            var ndcY = clip.Y / clip.W;
            Assert.True(MathF.Abs(ndcX) < 0.01f, $"yaw {yaw}: centre NDC X = {ndcX}");
            Assert.True(MathF.Abs(ndcY) < 0.01f, $"yaw {yaw}: centre NDC Y = {ndcY}");
        }
    }

    /// <summary>
    ///     Every corner of the (unpadded) content box must land inside clip space at every yaw —
    ///     the padding guarantees a margin, so a corner at |NDC| ≥ 1 means the frame is displaced
    ///     or mis-sized and content is being cut off.
    /// </summary>
    [Theory]
    [MemberData(nameof(ContentBoxes))]
    public void Build_KeepsEveryContentCorner_InsideClipSpace(
        float minX, float maxX, float minY, float maxY, float minZ, float maxZ)
    {
        foreach (var yaw in CompassYaws)
        {
            var tri = TrimetricViewProjBuilder.Build(
                minX, maxX, minY, maxY, minZ, maxZ, clipWorldZMax: null, yawDegrees: yaw);
            for (var i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    (i & 1) == 0 ? minX : maxX,
                    (i & 2) == 0 ? minY : maxY,
                    (i & 4) == 0 ? minZ : maxZ);
                var clip = Vector4.Transform(new Vector4(corner, 1f), tri.ViewProj);
                var ndcX = clip.X / clip.W;
                var ndcY = clip.Y / clip.W;
                var ndcZ = clip.Z / clip.W;

                Assert.True(MathF.Abs(ndcX) < 1f, $"yaw {yaw} corner {i}: NDC X = {ndcX}");
                Assert.True(MathF.Abs(ndcY) < 1f, $"yaw {yaw} corner {i}: NDC Y = {ndcY}");
                // Reversed-Z: depth lands in (0, 1], nearer content at higher values.
                Assert.True(ndcZ is > 0f and <= 1f, $"yaw {yaw} corner {i}: NDC Z = {ndcZ}");
            }
        }
    }
}

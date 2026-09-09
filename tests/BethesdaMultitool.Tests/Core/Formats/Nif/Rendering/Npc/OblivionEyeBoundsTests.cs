using System.Numerics;
using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class OblivionEyeBoundsTests
{
    [Fact]
    public void AttachmentTransformsStoredSphereIndependentlyOfDeformedPositions()
    {
        var eye = Eye();
        eye.Positions[0] = 100f; // FaceGen has already displaced geometry independently of NiBound.
        NpcRenderHelpers.TransformSubmesh(eye,
            Matrix4x4.CreateScale(2f) * Matrix4x4.CreateRotationZ(MathF.PI / 2f) *
            Matrix4x4.CreateTranslation(10f, 20f, 30f));
        var bound = Assert.IsType<NifLocalBounds>(eye.OblivionEyeBounds);
        AssertClose(new Vector3(6f, 22f, 36f), bound.Center);
        Assert.Equal(8f, bound.Radius, 5);
        AssertClose(new Vector3(10f, 220f, 30f), new Vector3(eye.Positions[0], eye.Positions[1], eye.Positions[2]));
        Assert.Equal(new NifLocalBounds(new Vector3(1, 2, 3), 4f), eye.LocalBounds);
    }

    [Fact]
    public void RigidSceneUsesAuthoredReflectionSphereAndPosedGeometryForFraming()
    {
        var decoded = Scene(Eye(), Matrix4x4.CreateScale(3f) * Matrix4x4.CreateTranslation(10f, 20f, 30f));
        var bound = Assert.IsType<NifLocalBounds>(
            BethesdaViewerScenePoseMaterializer12.ResolveReviewedEyeBounds(decoded, 0));
        AssertClose(new Vector3(13f, 26f, 39f), bound.Center);
        Assert.Equal(12f, bound.Radius);
        var posed = BethesdaViewerScenePoseMaterializer12.Materialize(decoded);
        Assert.Empty(posed.UnsupportedMeshParts);
        Assert.NotNull(posed.Bounds);
        AssertClose(new Vector3(10f, 20f, 30f), posed.Bounds.Value.Minimum);
        AssertClose(new Vector3(16f, 26f, 30f), posed.Bounds.Value.Maximum);
        var geometry = Assert.Single(posed.Mesh.Submeshes);
        AssertClose(new Vector3(13f, 23f, 30f), geometry.LocalBoundsCenter);
        Assert.Equal(MathF.Sqrt(18f), geometry.LocalBoundsRadius, 5);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void MissingProvenanceSphereOrRigidRouteCannotContribute(bool reviewed, bool hasBound, bool skinned)
    {
        var eye = Eye();
        eye.HasReviewedOblivionEyeSource = reviewed;
        if (!hasBound) eye.OblivionEyeBounds = null;
        var decoded = Scene(eye, Matrix4x4.Identity);
        if (skinned)
        {
            var part = decoded.MeshParts[0] with
            {
                Skin = new DecodedBethesdaViewerSkinBinding12([0], [Matrix4x4.Identity], [])
            };
            decoded = decoded with { MeshParts = [part] };
        }

        Assert.Null(BethesdaViewerScenePoseMaterializer12.ResolveReviewedEyeBounds(decoded, 0));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0f, -1f)]
    [InlineData(0f, float.NaN)]
    [InlineData(0f, float.PositiveInfinity)]
    [InlineData(float.NaN, 1f)]
    public void InvalidStoredSpheresAreRejected(float centerX, float radius)
    {
        var eye = Eye();
        eye.OblivionEyeBounds = new NifLocalBounds(new Vector3(centerX, 0, 0), radius);
        Assert.Null(BethesdaViewerScenePoseMaterializer12.ResolveReviewedEyeBounds(Scene(eye, Matrix4x4.Identity), 0));
    }

    [Fact]
    public void ControllerMorphRouteCannotReuseTheReviewedStaticSphere()
    {
        var decoded = Scene(Eye(), Matrix4x4.Identity);
        var morph = new NifGeometryMorphData(0, 1f, 0f, 0f, 1f, false, []);
        decoded = decoded with
        {
            AnimationClips =
            [
                new BethesdaViewerAnimationClip("morph", 0f, 1f, false, [], [], [],
                    GeometryMorphTracks: [new BethesdaViewerGeometryMorphTrack(0, morph)])
            ]
        };
        Assert.Null(BethesdaViewerScenePoseMaterializer12.ResolveReviewedEyeBounds(decoded, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacedGeometryOwnsTheEyeSphereAndRequiresItsOwnProvenance(bool reviewedGeometry)
    {
        var geometry = Eye();
        geometry.HasReviewedOblivionEyeSource = reviewedGeometry;
        var renderState = Eye();
        renderState.OblivionEyeBounds = new NifLocalBounds(new Vector3(50f), 100f);
        var clone = RenderableSubmeshCloner.CloneGeometryWithRenderState(geometry, renderState);
        Assert.Equal(geometry.OblivionEyeBounds, clone.OblivionEyeBounds);
        Assert.Equal(reviewedGeometry, clone.HasReviewedOblivionEyeSource);
    }

    private static RenderableSubmesh Eye()
    {
        return new RenderableSubmesh
        {
            Positions = [0, 0, 0, 2, 0, 0, 0, 2, 0], Triangles = [0, 1, 2],
            HasReviewedOblivionEyeSource = true,
            LocalBounds = new NifLocalBounds(new Vector3(1, 2, 3), 4f),
            OblivionEyeBounds = new NifLocalBounds(new Vector3(1, 2, 3), 4f)
        };
    }

    private static DecodedBethesdaViewerScene12 Scene(RenderableSubmesh eye, Matrix4x4 world)
    {
        var scene = new BethesdaViewerScene("eye bounds", BethesdaViewerScenePurpose.NpcAppearance,
            game: BethesdaGame.Oblivion);
        var node = scene.AddNode("eye", BethesdaViewerScene.RootNodeIndex, world, world,
            BethesdaViewerNodeRole.Attachment);
        scene.MeshParts.Add(new BethesdaViewerMeshPart { Name = "eye", NodeIndex = node, Submesh = eye });
        return BethesdaViewerSceneDecoder12.Decode(scene);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(Vector3.Distance(expected, actual), 0f, 1e-4f);
    }
}
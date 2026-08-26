using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Sample-gated proof for the exact retail assets that motivated the renderer hook. Synthetic
///     fixtures pin byte offsets; this gate pins the cross-block coordinate frame and source-shape
///     routing that cannot be inferred from an isolated constraint payload.
/// </summary>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class FnvPhysicsLiteRetailTests
{
    private const string MeshesBsaRelative =
        @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Meshes.bsa";

    private const string HangingLightPath =
        @"meshes\dungeons\office\lights\offrmlighthanging01.nif";

    private const string GoodspringsSignPath =
        @"meshes\architecture\goodsprings\nv_gs-saloon-sign.nif";

    public FnvPhysicsLiteRetailTests()
    {
        BucketBTestGuard.SkipUnlessEnabled();
    }

    [Fact]
    public void OfficeHangingLight_MapsRootLocalHingeOnlyToShadeSubmeshes()
    {
        using var archives = OpenRetailMeshes();
        var (data, nif) = Extract(archives, HangingLightPath);
        var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            data, nif,
            skipSkinning: true,
            collectBillboards: true,
            dropBoneAttachedShapes: true,
            treatRootsAsIdentity: true));
        var set = FnvHavokConstraintParser.Parse(data, nif);

        Assert.True(set.IsSupportedLayout);
        Assert.False(set.HasOrdinaryTransformAnimation);
        Assert.Equal(2, set.Constraints.Count);
        var limitedHinge = Assert.Single(
            set.Constraints, constraint => constraint.Kind == FnvHavokAngularConstraintKind.LimitedHinge);
        Assert.Equal(19, limitedHinge.BlockIndex);
        Assert.Equal(18, limitedHinge.DrivenBodyBlockIndex);

        // Entity A is bhkRigidBodyT relative to target node 15; entity B is a plain bhkRigidBody
        // rooted at node 6. Converting either authored frame lands on the same physical hanger.
        var plan = PhysicsLiteSway.CreatePlan(set, limitedHinge, 0);
        var descriptor = Assert.IsType<PhysicsLiteSwayDescriptor>(plan.Descriptor);
        VectorAssert.Equal(new Vector3(-55.7041f, 0.05523f, -19.1407f), descriptor.Pivot, 0.02f);
        VectorAssert.Equal(-Vector3.UnitY, descriptor.Axis, 0.0001f);
        Assert.Equal(-MathF.PI / 2f, descriptor.MinimumAngle, 4);
        Assert.Equal(MathF.PI / 2f, descriptor.MaximumAngle, 4);

        var routes = PhysicsLiteSway.BuildSourceBlockRoutes(set);
        var visualSources = model.Submeshes.Select(submesh => submesh.SourceBlockIndex).ToArray();
        Assert.Equal([21, 26, 31], visualSources);
        Assert.Equal(19, routes[21].ConstraintBlockIndex);
        Assert.Equal(19, routes[26].ConstraintBlockIndex);
        Assert.DoesNotContain(31, routes.Keys);

        // Constraint 10 drives the small collision-link subtree [6,12], not a rendered submesh;
        // it must never make the whole fixture or its fixed ceiling bracket sway.
        var ragdoll = Assert.Single(
            set.Constraints, constraint => constraint.Kind == FnvHavokAngularConstraintKind.Ragdoll);
        Assert.Equal([6, 12], ragdoll.DrivenEntity!.TargetSubtree);
        Assert.DoesNotContain(visualSources, source => routes.TryGetValue(source, out var route) &&
                                                       route.ConstraintBlockIndex == ragdoll.BlockIndex);
    }

    [Fact]
    public void GoodspringsHangingSign_RemainsOnItsAuthoredTransformAnimationPath()
    {
        using var archives = OpenRetailMeshes();
        var (data, nif) = Extract(archives, GoodspringsSignPath);

        var set = FnvHavokConstraintParser.Parse(data, nif);

        Assert.True(set.IsSupportedLayout);
        Assert.True(set.HasOrdinaryTransformAnimation);
        Assert.Empty(set.Constraints);
        Assert.Empty(PhysicsLiteSway.BuildSourceBlockRoutes(set));
    }

    private static MeshArchiveSet OpenRetailMeshes()
    {
        var bsaPath = SampleFileFixture.FindSamplePath(MeshesBsaRelative);
        Assert.SkipWhen(bsaPath is null, "FNV PC final meshes BSA not available");
        return MeshArchiveSet.Open(bsaPath!, null, false);
    }

    private static (byte[] Data, NifInfo Nif) Extract(MeshArchiveSet archives, string path)
    {
        Assert.True(archives.TryExtractFile(path, out var data, out _), $"Retail NIF missing: {path}");
        return (data, Assert.IsType<NifInfo>(NifParser.Parse(data)));
    }
}
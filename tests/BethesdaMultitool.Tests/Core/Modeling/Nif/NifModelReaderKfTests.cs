using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelKfFixtures;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 10, the <c>.kf</c> path of <see cref="NifModelReader" /> (<see cref="NifModelAnimationStreamReader" />;
///     plan section 1.7, owner rulings D4, D12 and D14): synthetic streams and skeletons written field by field, read
///     through the real reader contract with a companion resolver. Every test carries a control that fails.
/// </summary>
public sealed class NifModelReaderKfTests
{
    private const string KfPath = "meshes/creatures/foo/idleanims/special.kf";
    private const string NearSkeleton = "meshes/creatures/foo/skeleton.nif";
    private const string FarSkeleton = "meshes/creatures/skeleton.nif";
    private const string ExplicitSkeleton = "skel/custom.nif";

    /// <summary>
    ///     With <c>bmt.skeleton</c> the document is the skeleton's nodes (no mesh) plus the clip; Pelvis and Spine bind to
    ///     skeleton nodes 1 and 2, the '##' attachment name stays native with its reason, and the skeleton provenance row
    ///     records the explicit rule, the path, the digest of the skeleton bytes and the bound and unbound names. Control:
    ///     a skeleton missing Spine keeps the Spine track native with 'target not in the resolved skeleton' while Pelvis
    ///     still binds.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitSkeleton_YieldsSkeletonNodesAndClips(bool bigEndian)
    {
        var kf = Kf(bigEndian, 34, PelvisName, SpineName, WeaponName);
        var skeleton = Skeleton(bigEndian);
        var options = SkeletonOptions(ExplicitSkeleton);

        var result = ReadWith(kf, OneFile(ExplicitSkeleton, skeleton), path: KfPath, options: options);

        var document = result.Document;
        Assert.Equal(new[] { RootName, PelvisName, SpineName }, document.Nodes.Select(static node => node.Name));
        Assert.Empty(document.Meshes);
        var clip = Assert.Single(document.Animations);
        Assert.Equal("Idle", clip.Name);
        Assert.Equal(new[] { 1, 2 }, clip.TransformTracks.Select(static track => track.NodeIndex).Distinct());
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:0").Kind);
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:1").Kind);
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("block:3").Kind);
        var weapon = result.Coverage.GetClassification("block:5");
        Assert.Equal(ModelSourceCoverageKind.NativeOnly, weapon.Kind);
        Assert.Equal(NifModelTargetNames.TargetNotInSkeletonReason, weapon.Reason);
        Assert.Equal(7, result.Coverage.TotalCount);

        var row = Assert.Single(Rows(document, NifModelSkeletonProvenance.Kind));
        Assert.Equal(SceneElementKind.Document, row.Target.Kind);
        var payload = JsonNode.Parse(row.PayloadJson)!.AsObject();
        Assert.Equal(NifModelSkeletonResolver.ExplicitRuleName, (string)payload["rule"]!);
        Assert.Equal(ExplicitSkeleton, (string)payload["path"]!);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(skeleton)), (string)payload["sha256"]!);
        Assert.Equal(new[] { PelvisName, SpineName },
            payload["matched"]!.AsArray().Select(static name => (string)name!["text"]!));
        var unmatched = Assert.Single(payload["unmatched"]!.AsArray())!;
        Assert.Equal(WeaponName, (string)unmatched["name"]!["text"]!);
        Assert.Equal("targetNotInSkeleton", (string)unmatched["code"]!);
        var clipRow = Assert.Single(Rows(document, NifModelAnimationNativeState.Kind));
        Assert.Equal(new SceneElementRef(SceneElementKind.Animation, 0), clipRow.Target);
        Assert.Contains(document.Diagnostics, static d => d.Code == NifModelAnimationDiagnostics.SkeletonDiagnostic);
        SceneValidation.ValidateStructure(document);

        var control = ReadWith(kf, OneFile(ExplicitSkeleton, Skeleton(bigEndian, withSpine: false)), path: KfPath,
            options: options);
        Assert.Equal(new[] { RootName, PelvisName }, control.Document.Nodes.Select(static node => node.Name));
        var spine = control.Coverage.GetClassification("block:3");
        Assert.Equal(ModelSourceCoverageKind.NativeOnly, spine.Kind);
        Assert.Equal(NifModelTargetNames.TargetNotInSkeletonReason, spine.Reason);
        Assert.Equal(ModelSourceCoverageKind.Typed, control.Coverage.GetClassification("block:1").Kind);
        Assert.All(Assert.Single(control.Document.Animations).TransformTracks,
            static track => Assert.Equal(1, track.NodeIndex));
    }

    /// <summary>
    ///     Without <c>bmt.skeleton</c> the walk-up through the data-root resolver chooses the NEAREST ancestor
    ///     <c>skeleton.nif</c>: the <c>.kf</c>'s own directory holds none, its parent does. Control: the farther skeleton
    ///     (a different root name and digest) is not chosen although it exists.
    /// </summary>
    [Fact]
    public async Task WalkUp_ChoosesTheNearestAncestorSkeleton()
    {
        var near = Skeleton(false);
        var far = Skeleton(false, rootName: "Far Root");
        await using var companions = Companions((NearSkeleton, near), (FarSkeleton, far));

        var result = ReadWith(Kf(false, 34, PelvisName), companions.ResolveAsync, path: KfPath);

        var payload = JsonNode.Parse(Assert.Single(Rows(result.Document, NifModelSkeletonProvenance.Kind)).PayloadJson)!
            .AsObject();
        Assert.Equal(NifModelSkeletonResolver.NearestAncestorRuleName, (string)payload["rule"]!);
        Assert.Equal(NearSkeleton, (string)payload["path"]!);
        Assert.Equal(new[] { "meshes/creatures/foo/idleanims/skeleton.nif", NearSkeleton },
            payload["candidates"]!.AsArray().Select(static candidate => (string)candidate!["path"]!));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(near)), (string)payload["sha256"]!);
        Assert.Equal(RootName, result.Document.Nodes[0].Name);
        Assert.Single(result.Document.Animations);

        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(far)), (string)payload["sha256"]!);
        Assert.DoesNotContain(result.Document.Nodes, static node => node.Name == "Far Root");
    }

    /// <summary>
    ///     D4: with no skeleton anywhere up the path (and no <c>bmt.skeleton</c>) the read is not supported with
    ///     'no skeleton found; pass --skeleton', naming the candidates it examined. Control: the same stream with the
    ///     skeleton in place reads.
    /// </summary>
    [Fact]
    public async Task NoSkeleton_IsNotSupported_D4()
    {
        var kf = Kf(false, 34, PelvisName);
        await using var empty = Companions(("textures/unrelated.dds", new byte[] { 0 }));
        await using var present = Companions((NearSkeleton, Skeleton(false)));

        var error = Assert.Throws<NotSupportedException>(() => ReadWith(kf, empty.ResolveAsync, path: KfPath));

        Assert.StartsWith(NifModelSkeletonResolver.NoSkeletonReason, error.Message);
        Assert.Contains("meshes/creatures/foo/idleanims/skeleton.nif", error.Message, StringComparison.Ordinal);
        Assert.Single(ReadWith(kf, present.ResolveAsync, path: KfPath).Document.Animations);
    }

    /// <summary>
    ///     An explicit skeleton that names nothing is not supported, and the walk-up is NOT tried in its place: the parent
    ///     directory's skeleton exists here. Control: without the option that skeleton is found.
    /// </summary>
    [Fact]
    public async Task ExplicitSkeletonMissing_DoesNotFallBackToTheWalkUp()
    {
        var kf = Kf(false, 34, PelvisName);
        await using var companions = Companions((NearSkeleton, Skeleton(false)));

        var error = Assert.Throws<NotSupportedException>(() => ReadWith(kf, companions.ResolveAsync, path: KfPath,
            options: SkeletonOptions("meshes/nowhere/skeleton.nif")));

        Assert.StartsWith(NifModelSkeletonResolver.ExplicitSkeletonMissingReason, error.Message);
        Assert.Single(ReadWith(kf, companions.ResolveAsync, path: KfPath).Document.Animations);
    }

    /// <summary>
    ///     D14: a stream whose every track stays native (its one target is an attachment name the skeleton lacks) drives
    ///     nothing, so it is not supported with 'no clip expressible: ' and the first blocking reason, never exported as a
    ///     skeleton-only document. Control: the same stream with a bindable target beside it reads.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoExpressibleClip_IsNotSupported_D14(bool bigEndian)
    {
        var resolver = OneFile(ExplicitSkeleton, Skeleton(bigEndian));
        var options = SkeletonOptions(ExplicitSkeleton);

        var error = Assert.Throws<NotSupportedException>(() =>
            ReadWith(Kf(bigEndian, 34, WeaponName), resolver, path: KfPath, options: options));

        Assert.StartsWith(NifModelAnimationStreamReader.NoClipExpressibleReason + ": ", error.Message);
        Assert.Contains(NifModelTargetNames.TargetNotInSkeletonReason, error.Message, StringComparison.Ordinal);
        var control = ReadWith(Kf(bigEndian, 34, PelvisName, WeaponName), resolver, path: KfPath, options: options);
        Assert.Single(control.Document.Animations);
    }

    /// <summary>
    ///     A BS 33 stream (a key only animation streams use) reads as a <c>.kf</c> against a BS 34 skeleton. Control: a
    ///     scene graph at BS 33 is not supported as an animation-stream key.
    /// </summary>
    [Fact]
    public void AnimationOnlyBsVersion_ReadsAsAnAnimationStream()
    {
        var result = ReadWith(Kf(false, 33, PelvisName), OneFile(ExplicitSkeleton, Skeleton(false)), path: KfPath,
            options: SkeletonOptions(ExplicitSkeleton));

        Assert.Single(result.Document.Animations);
        var graph = new NifTestFileBuilder(false, 33);
        AddNode(graph, -1, []);
        var error = Assert.Throws<NotSupportedException>(() => Read(graph.Build()));
        Assert.StartsWith(NifModelProbe.AnimationStreamKeyCategory, error.Message);
    }

    /// <summary>
    ///     Roots that mix a sequence with a scene-graph node are not supported as either. Control: the same blocks with the
    ///     sequence as the only root are a <c>.kf</c> stream (refused here only for want of a skeleton).
    /// </summary>
    [Fact]
    public void MixedRoots_AreNotSupported()
    {
        var mixed = MixedRootsFile(true);
        var sequenceOnly = MixedRootsFile(false);

        var error = Assert.Throws<NotSupportedException>(() => Read(mixed));
        var control = Assert.Throws<NotSupportedException>(() => Read(sequenceOnly));

        Assert.StartsWith(NifModelProbe.AnimationStreamKeyCategory, error.Message);
        Assert.Contains("NiControllerSequence", error.Message, StringComparison.Ordinal);
        Assert.StartsWith(NifModelSkeletonResolver.NoSkeletonReason, control.Message);
    }

    /// <summary>The app options naming an explicit skeleton.</summary>
    private static Dictionary<string, string> SkeletonOptions(string path)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BethesdaModelRegistration.SkeletonOption] = path
        };
    }

    /// <summary>0 NiNode, 1 NiControllerSequence with no controlled block; roots [0, 1] or [1].</summary>
    private static byte[] MixedRootsFile(bool withNodeRoot)
    {
        var builder = new NifTestFileBuilder(false, 34);
        var idle = builder.AddString("Idle");
        AddNode(builder, -1, []);
        builder.AddBlock("NiControllerSequence", NifModelAnimationReaderTestSupport.Sequence(idle, []));
        if (withNodeRoot)
        {
            builder.WithRoot(0);
        }

        return builder.WithRoot(1).Build();
    }
}

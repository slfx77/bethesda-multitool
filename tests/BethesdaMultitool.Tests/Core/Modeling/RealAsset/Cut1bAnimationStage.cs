using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Core.Modeling.Nif;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Runs the cut-1b animation stage on one cover-manifest file for the slice-9 document hops: the stage order
///     <see cref="NifModelReader" /> uses before its sub-readers (parse, decode each block, reachability, palette names,
///     node walk), then for a <c>.nif</c> the cut-1a skin, texture, material and geometry stages and the property targets
///     slice 14 binds to (the stage order of <c>NifModelAnimationPropertyTestSupport.ReadFixture</c>), and
///     <see cref="NifModelAnimationReader.ReadNif(NifModelReadState, NifModelNodeGraph, NifModelPropertyTargets, NifPackedPlatformSelection, CancellationToken)" />;
///     for a <c>.kf</c> the pinned skeleton's graph and
///     <see cref="NifModelAnimationReader.ReadKf(NifModelReadState, NifModelNodeGraph, NifPackedPlatformSelection, CancellationToken)" />.
/// </summary>
/// <remarks>
///     <para>
///         Skip rules (never early returns): a decline control, or a file the probe declined, is hop A0's (it asserts the
///         decline); a <c>.kf</c> whose manifest row pins no skeleton is unreadable until SA8 and D4 (plan section 1.7);
///         a missing container or expectation record skips through <see cref="Cut1bFixtureResolver" /> and
///         <see cref="Cut1bProbeExpectations" />.
///     </para>
///     <para>
///         Platform: a big-endian file carries no byte that names its console, so it is read under the platform the probe
///         recorded for it (<c>platform</c>, <c>x360</c> or <c>ps3</c>), falling back to the console its manifest source
///         names. A little-endian file is PC whatever the option says (<see cref="NifModelSquadPolicy.Resolve" />).
///     </para>
/// </remarks>
internal static class Cut1bAnimationStage
{
    /// <summary>The virtual path prefix every manifest entry is rooted at.</summary>
    private const string MeshesPrefix = "meshes/";

    /// <summary>
    ///     Manifest files the cut-1a stages refuse before the animation stage runs, by SHA-256, with the start of the exact
    ///     refusal. A row for one of them skips with that refusal; a refusal of any other file, or with another message,
    ///     fails; and a listed file that reads fails too, so the list cannot go stale. X360
    ///     <c>meshes/armor/headgear/slavehats/nvslave_02_go.nif</c> (the plan's 170-degree <c>.nif</c> twin) stores NaN
    ///     (0x7FC00000) in NiNode 109's Translation.x, and the node reader refuses non-finite transforms (measured
    ///     2026-09-26; whether such a file is admitted is an owner question recorded in the slice 9 landing notes).
    /// </summary>
    private static readonly Dictionary<string, string> KnownRefusals = new(StringComparer.Ordinal)
    {
        ["1a3622e0194abb50c45ee77c99f135c39fe513b963870223fbf8f3d133e8a513"] =
            "NIF block 109 (NiNode), field 'Translation.x' at offset 0x5DCF: the transform value NaN (0x7FC00000) is not finite."
    };

    /// <summary>Reads the manifest file with the given entry and SHA-256 (a theory row) through the stage.</summary>
    /// <param name="entry">The manifest entry, checked against the row.</param>
    /// <param name="sha256">The manifest SHA-256.</param>
    /// <returns>The read.</returns>
    public static Cut1bAnimationRead Load(string entry, string sha256)
    {
        var file = Cut1bCoverManifest.Require(sha256);
        Assert.Equal(entry, file.Entry);
        return Read(file);
    }

    /// <summary>Reads the manifest file pinned for one of the plan's named controls.</summary>
    /// <param name="control">The control's name as the plan's control table spells it.</param>
    /// <returns>The read.</returns>
    public static Cut1bAnimationRead LoadControl(string control)
    {
        return Read(Cut1bCoverManifest.RequireControl(control));
    }

    /// <summary>Reads one manifest file through the stage (see the remarks for the skip rules).</summary>
    /// <param name="file">The manifest row.</param>
    /// <returns>The read.</returns>
    public static Cut1bAnimationRead Read(Cut1bCoverFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Assert.SkipWhen(file.IsDeclinedControl,
            $"{file}: a decline control ({file.Declined}); hop A0 asserts that the views refuse it.");
        var expectation = Cut1bProbeExpectations.Require(file);
        var declined = expectation["declined"];
        Assert.SkipWhen(declined is not null,
            $"{file}: the probe declines it ({declined}); hop A0 asserts the decline.");
        var bytes = Cut1bFixtureResolver.Require(file);
        var platform = Platform(file, expectation);
        var cancellationToken = TestContext.Current.CancellationToken;
        if (file.IsAnimationStream)
        {
            Assert.SkipWhen(file.Skeleton is null,
                $"{file}: no skeleton is pinned ({file.SkeletonMissing}); a skeleton-less .kf waits on SA8 and D4.");
            var skeletonFile = Cut1bCoverManifest.Require(file.Skeleton!.Sha256);
            var skeletonBytes = Cut1bFixtureResolver.Require(skeletonFile);
            var (_, skeleton) = ReadGraph(skeletonBytes, skeletonFile.Entry);
            var state = ReadState(bytes, file.Entry);
            var result = NifModelAnimationReader.ReadKf(state, skeleton, platform, cancellationToken);
            return new Cut1bAnimationRead(file, expectation, bytes, state, skeleton, platform, result,
                skeletonFile.Sha256);
        }

        NifModelReadState nifState;
        NifModelNodeGraph graph;
        try
        {
            (nifState, graph) = ReadGraph(bytes, file.Entry);
        }
        catch (InvalidDataException refusal) when (KnownRefusals.TryGetValue(file.Sha256, out var expected))
        {
            Assert.Equal(expected, refusal.Message);
            Assert.Skip($"{file}: the cut-1a stages refuse it before the animation stage: {refusal.Message}");
            throw;
        }

        Assert.False(KnownRefusals.ContainsKey(file.Sha256),
            $"{file}: listed as a known refusal but its graph now reads; remove it from the list.");
        var (placed, targets) = ReadMaterials(nifState, graph, platform, bytes, file.Entry);
        var nifResult = NifModelAnimationReader.ReadNif(nifState, placed, targets, platform, cancellationToken);
        return new Cut1bAnimationRead(file, expectation, bytes, nifState, graph, platform, nifResult, null);
    }

    /// <summary>
    ///     The document a hop evaluates: the read's graph nodes (one scene over its roots) and the given clips, with no
    ///     meshes (Shared's evaluator refuses unlowered cut-1a source geometry, and transform tracks drive nodes only).
    /// </summary>
    /// <param name="read">The read.</param>
    /// <param name="clips">The clips, usually the reader's.</param>
    /// <returns>The document.</returns>
    public static ModelDocument Assemble(Cut1bAnimationRead read, IEnumerable<SceneAnimation> clips)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clips);
        // Shared's evaluator refuses source roles and source billboards until they are explicitly lowered, and they do not
        // change a node's local matrix, which is all the samplers read, so the nodes are carried without them.
        var nodes = read.Graph.Nodes.Select(static node => node.LocalTrs is { } trs
            ? new SceneNode(node.Name, trs, node.Children, node.MeshIndex, node.ExtrasJson, node.SkinIndex, node.Presentation,
                node.MorphWeights)
            : new SceneNode(node.Name, node.LocalTransform, node.Children, node.MeshIndex, node.ExtrasJson, node.SkinIndex,
                node.Presentation, node.MorphWeights)).ToArray();
        return new ModelDocument("nif", read.File.Entry, [new SceneDefinition("scene", read.Graph.RootNodeIndices)],
            nodes, [], animations: clips);
    }

    /// <summary>
    ///     The platform a manifest file is read under: for a big-endian file the console the probe recorded (or its
    ///     manifest source names), otherwise the option-less default (which a little-endian read ignores).
    /// </summary>
    /// <param name="file">The manifest row.</param>
    /// <param name="expectation">The probe record.</param>
    /// <returns>The platform selection.</returns>
    public static NifPackedPlatformSelection Platform(Cut1bCoverFile file, JsonObject expectation)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (file.IsBigEndian)
        {
            var recorded = expectation["platform"]?.GetValue<string>();
            var console = recorded is NifPackedPlatformOption.X360Value or NifPackedPlatformOption.Ps3Value
                ? recorded
                : file.Source.Contains("PS3", StringComparison.OrdinalIgnoreCase)
                    ? NifPackedPlatformOption.Ps3Value
                    : file.Source.Contains("X360", StringComparison.OrdinalIgnoreCase)
                        ? NifPackedPlatformOption.X360Value
                        : null;
            if (console is not null)
            {
                options[BethesdaModelRegistration.PlatformOption] = console;
            }
        }

        return NifPackedPlatformOption.Resolve(options);
    }

    /// <summary>
    ///     Parses and decodes a file into a read state the way NifModelReader does before its sub-readers run: NiNode and
    ///     geometry blocks strictly, every other block tolerantly. A <c>.kf</c> stops here (its roots are sequences).
    /// </summary>
    /// <param name="bytes">The file bytes.</param>
    /// <param name="entry">The manifest entry, used as the item's virtual path.</param>
    /// <returns>The read state.</returns>
    public static NifModelReadState ReadState(byte[] bytes, string entry)
    {
        var info = NifParser.Parse(bytes);
        Assert.True(info is not null, $"{entry}: NifParser could not parse the file.");
        var schema = NifSchema.LoadEmbedded();
        var decoder = new NifBlockDecoder(schema, info, bytes);
        var footer = decoder.ValidateLayout();
        var blocks = new NifDecodedBlock[info.Blocks.Count];
        for (var i = 0; i < blocks.Length; i++)
        {
            var type = info.Blocks[i].TypeName;
            var mode = schema.Inherits(type, "NiNode") || NifModelGeometryReader.IsStrictType(type)
                ? NifDecodeMode.Strict
                : NifDecodeMode.Tolerant;
            blocks[i] = decoder.Decode(i, mode);
        }

        var (item, input) = NifModelTestSupport.Open(bytes, VirtualPath(entry));
        input.Dispose();
        return new NifModelReadState(item, ModelNativeDetail.Metadata, bytes,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), info, schema, decoder, footer, blocks);
    }

    /// <summary>Reads a file's state and walks its node graph (reachability, palette names, node walk).</summary>
    /// <param name="bytes">The file bytes.</param>
    /// <param name="entry">The manifest entry.</param>
    /// <returns>The state and the unplaced graph.</returns>
    public static (NifModelReadState State, NifModelNodeGraph Graph) ReadGraph(byte[] bytes, string entry)
    {
        var state = ReadState(bytes, entry);
        var cancellationToken = TestContext.Current.CancellationToken;
        var reachable = NifModelCoverage.ReferenceReachability(state.Blocks, state.Footer.Roots, cancellationToken);
        var palette = NifModelPaletteNames.Read(state, reachable, cancellationToken);
        return (state, NifModelNodeReader.Read(state, palette, cancellationToken));
    }

    /// <summary>
    ///     The cut-1a stages a property track binds through (skins, textures, materials, geometry), in NifModelReader's
    ///     order, and the property targets of slice 14; the graph comes back with the stages' placements applied (same
    ///     node indices), as the reader is given it.
    /// </summary>
    private static (NifModelNodeGraph Placed, NifModelPropertyTargets Targets) ReadMaterials(NifModelReadState state,
        NifModelNodeGraph graph, NifPackedPlatformSelection platform, byte[] bytes, string entry)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (item, input) = NifModelTestSupport.Open(bytes, VirtualPath(entry));
        using (input)
        {
            var cache = new NifModelReadCache();
            var context = new ModelReadContext(item, input, cache);
            var skins = new NifModelSkinReader(state, graph, cancellationToken);
            var textures = new NifModelTextureSource(state, context, cache, NifTextureCodec.Instance, cancellationToken);
            var materials = new NifModelMaterialReader(state, graph, textures, cancellationToken);
            var geometry = NifModelGeometryReader.Read(state, graph, materials.Resolve, skins, platform,
                cancellationToken);
            var materialResult = materials.Complete();
            _ = textures.Complete();
            var skinResult = skins.Complete();
            var placed = NifModelNodeReader.WithPlacements(graph, new NifModelNodePlacements(geometry.MeshByNode,
                skinResult.SkinByNode, skinResult.JointNodes, new Dictionary<int, SceneBillboard>()));
            return (placed, NifModelPropertyTargets.FromResults(materialResult, geometry));
        }
    }

    /// <summary>The manifest entry as a Data-relative virtual path (<c>meshes/...</c>).</summary>
    private static string VirtualPath(string entry)
    {
        return entry.StartsWith(MeshesPrefix, StringComparison.OrdinalIgnoreCase) ? entry : MeshesPrefix + entry;
    }
}

using System.Globalization;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 10: the <c>.kf</c> path of <see cref="NifModelReader" /> (plan section 1.7, owner rulings D4, D12 and
///     D14). A <c>.kf</c> animation stream has no scene graph of its own: its clips bind to a skeleton, and its document is
///     that skeleton's nodes plus the clips.
/// </summary>
/// <remarks>
///     <list type="number">
///         <item>
///             The skeleton: the <see cref="BethesdaModelRegistration.SkeletonOption" /> path when given, else the nearest
///             ancestor <c>skeleton.nif</c> of the <c>.kf</c>'s virtual path (its <see cref="ModelSourceItem" /> reference
///             path), both chosen by <see cref="NifModelSkeletonResolver" /> (exact names, nearest wins, no compatibility
///             gate) through the read context's companion resolver. Two guards make that resolver a lookup: a resolver
///             that refuses a path with <see cref="ArgumentException" /> (Shared's default resolver takes basenames within
///             the input's own source) names nothing for it; and a walk-up candidate counts only the occurrences whose
///             path is the candidate's (separator- and case-insensitively), so a basename search never passes off a
///             skeleton elsewhere in the source as an ancestor. The explicit path's occurrences are taken as the
///             resolver returns them.
///         </item>
///         <item>
///             No skeleton: Unsupported with the resolver's reason (D4: 'no skeleton found; pass --skeleton'). Shared's
///             <c>SceneRestPoseProvenance</c> (SA8) can now declare an invented rest pose, but no writer classes such a
///             pose Degraded yet, so the D4 refusal stands until the owner re-evaluates it.
///         </item>
///         <item>
///             The skeleton is read through the cache's companion budget into its own read state, parsed, checked against
///             the scene-graph key, decoded as the reader decodes a <c>.nif</c> and walked by the cut-1a node reader; its
///             geometry, collision, materials and skins are not read (D12). Its node and palette diagnostics describe the
///             document's nodes and join the document.
///         </item>
///         <item>
///             The clips: <see cref="NifModelAnimationReader.ReadKf(NifModelReadState, NifModelNodeGraph, NifPackedPlatformSelection, CancellationToken)" />
///             under the read's platform. A <c>.kf</c> whose clips drive nothing (every transform track NotDriven or native,
///             and no Euler, morph, property or matrix track) is Unsupported: 'no clip expressible: ' and the first
///             blocking reasons (D14), so no build exports a skeleton-only document.
///         </item>
///         <item>
///             The document: the skeleton's nodes (one scene over its roots), no mesh, material or skin, the clips, the
///             <c>.kf</c>'s own header and block rows, one <c>bmt.nif.animation.clip</c> row per clip and one
///             <c>bmt.nif.animation.skeleton</c> Document row (<see cref="NifModelSkeletonProvenance" />: path, SHA-256,
///             rule, candidates, and every controlled block's target name, bound or with its reason). Coverage is the
///             <c>.kf</c>'s own header census: every block classified by the animation stage's decisions or its plan 2.1
///             table row.
///         </item>
///     </list>
/// </remarks>
internal static class NifModelAnimationStreamReader
{
    /// <summary>The D14 refusal prefix.</summary>
    public const string NoClipExpressibleReason = "no clip expressible";

    /// <summary>The D14 detail when no decision names a blocking reason (no controlled block at all).</summary>
    public const string NoControlledBlocksReason = "no sequence drives a skeleton node";

    /// <summary>The most blocking reasons the D14 refusal names.</summary>
    public const int MaximumListedReasons = 3;

    /// <summary>The most candidates a skeleton refusal names.</summary>
    public const int MaximumListedCandidates = 8;

    /// <summary>The decision codes that never block a clip (D11 fields kept native beside a clip, anim notes).</summary>
    private static readonly HashSet<string> NonBlockingCodes = new(StringComparer.Ordinal)
    {
        NifModelAnimationReasons.SequenceNativeFieldsCode, NifModelAnimationCoverage.AnimNotesCode
    };

    /// <summary>Reads one <c>.kf</c> whose every footer root is an NiControllerSequence.</summary>
    /// <param name="formatId">The reader's format id.</param>
    /// <param name="state">The <c>.kf</c>'s read state (every block decoded).</param>
    /// <param name="context">The read context (app options, companion resolver).</param>
    /// <param name="cache">The per-item cache, which reads and hashes the skeleton within the companion budget.</param>
    /// <param name="census">The <c>.kf</c>'s header census.</param>
    /// <param name="units">The document units.</param>
    /// <param name="platform">The platform the read resolved.</param>
    /// <param name="cancellationToken">Observed per stage.</param>
    /// <returns>The document and its coverage.</returns>
    /// <exception cref="NotSupportedException">No skeleton, an unreadable or out-of-scope skeleton, no expressible clip, or too many rows.</exception>
    /// <exception cref="InvalidDataException">The skeleton is corrupt, or a transform static value is malformed.</exception>
    public static ModelReadResult Read(string formatId, NifModelReadState state, ModelReadContext context,
        NifModelReadCache cache, IReadOnlyList<ModelSourceElement> census, SceneUnits units,
        NifPackedPlatformSelection platform, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formatId);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(census);
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(platform);
        var item = state.Item;
        var explicitPath = NifModelSkeletonOption.Resolve(context.AppOptions);
        var resolution = NifModelSkeletonResolver.Resolve(item.Reference.Path, explicitPath,
            Lookup(context, item, explicitPath, cancellationToken));
        if (!resolution.IsResolved)
        {
            throw new NotSupportedException(SkeletonRefusal(resolution));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var skeletonPath = resolution.SkeletonPath!;
        var companion = cache.ReadCompanion(resolution.Skeleton!, cancellationToken);
        if (companion.Bytes is not { } skeletonBytes)
        {
            throw new NotSupportedException($"The skeleton '{skeletonPath}' could not be read: {companion.FailureReason}");
        }

        var skeletonDiagnostics = new List<SceneDiagnostic>();
        var skeleton = ReadSkeleton(skeletonPath, resolution.Skeleton!, skeletonBytes, companion.Sha256!,
            skeletonDiagnostics, cancellationToken);

        var result = NifModelAnimationReader.ReadKf(state, skeleton, platform, cancellationToken);
        if (!result.Clips.Any(Expresses))
        {
            throw new NotSupportedException(NoClipRefusal(result));
        }

        NifModelReader.CheckRowBudget(state.Blocks.Count, 0, 0, 0, 0, result.Clips.Count, 1);
        var provenance = NifModelSkeletonProvenance.FromDigest(resolution, companion.Sha256!,
            TargetMatches(state, skeleton, cancellationToken));

        NifModelReader.AddDecodeDiagnostics(state);
        var diagnostics = new List<SceneDiagnostic>(skeletonDiagnostics);
        diagnostics.AddRange(state.Diagnostics);
        diagnostics.AddRange(NifModelAnimationDiagnostics.ForSkeleton(provenance));
        diagnostics.AddRange(NifModelAnimationDiagnostics.ForClassifications(result.Clips.Count,
            NifModelAnimationCoverage.ClassifyFile(state, result, cancellationToken)));

        var classifications = Classify(state, result, cancellationToken);
        var prebuiltRows = NifModelAnimationNativeState.Build(state, result, 0, cancellationToken)
            .Append(provenance.ToNativeState()).ToArray();
        var nativeStates = NifModelNativeState.Build(state, EmptyGraph(state.Blocks.Count), EmptyGeometry(),
            prebuiltRows, new Dictionary<int, SceneElementRef>(), new Dictionary<int, JsonObject>(),
            cancellationToken);
        var name = Path.GetFileNameWithoutExtension(item.Reference.Path);
        var document = new ModelDocument(formatId, name, [new SceneDefinition(name, skeleton.RootNodeIndices)],
            skeleton.Nodes, [], animations: result.Clips, sourceIdentity: item.Reference.ToString(),
            diagnostics: diagnostics, units: units, sourceBasis: NifModelUnits.Basis, nativeStates: nativeStates)
        {
            SourceProvenance = new SceneSourceProvenance(item.Reference.Path, state.Sha256)
        };
        var coverage = NifModelCoverage.Build(item.Reference, state.Header, census, classifications,
            cancellationToken);
        return new ModelReadResult(document, coverage);
    }

    /// <summary>
    ///     True when a clip drives something: a transform track that is not NotDriven, or any Euler, morph, per-target
    ///     morph, property or matrix track (D14).
    /// </summary>
    /// <param name="clip">The clip.</param>
    /// <returns>True when the clip is expressible.</returns>
    public static bool Expresses(SceneAnimation clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.TransformTracks.Any(static track => track.State != SceneAnimationChannelState.NotDriven) ||
               clip.EulerRotationTracks.Count > 0 || clip.MorphTracks.Count > 0 || clip.MorphTargetTracks.Count > 0 ||
               clip.PropertyTracks.Count > 0 || clip.MatrixTracks.Count > 0;
    }

    /// <summary>
    ///     The skeleton lookup over the context's companion resolver, with the two guards the type remarks describe.
    /// </summary>
    private static NifModelSkeletonLookup Lookup(ModelReadContext context, ModelSourceItem owner, string? explicitPath,
        CancellationToken cancellationToken)
    {
        return path =>
        {
            IReadOnlyList<ModelSourceItem> found;
            try
            {
                found = context.CompanionResolver(owner, path, cancellationToken).AsTask().GetAwaiter().GetResult();
            }
            catch (ArgumentException)
            {
                // Shared's default resolver resolves basenames within the input's own source and refuses a path.
                return [];
            }

            if (explicitPath is not null && string.Equals(path, explicitPath, StringComparison.Ordinal))
            {
                return found;
            }

            return found.Where(candidate => SamePath(candidate.Reference.Path, path)).ToArray();
        };
    }

    /// <summary>True when two virtual paths name the same file, ignoring separator spelling, a leading slash and case.</summary>
    private static bool SamePath(string resolved, string candidate)
    {
        return string.Equals(Canonical(resolved), Canonical(candidate), StringComparison.OrdinalIgnoreCase);

        static string Canonical(string path)
        {
            return path.Replace('\\', '/').TrimStart('/');
        }
    }

    /// <summary>The refusal text of an unresolved skeleton: the resolver's reason and the candidates it examined.</summary>
    private static string SkeletonRefusal(NifModelSkeletonResolution resolution)
    {
        var reason = resolution.FailureReason!;
        if (resolution.Candidates.Count == 0)
        {
            return reason;
        }

        var listed = string.Join(", ", resolution.Candidates.Take(MaximumListedCandidates)
            .Select(static candidate => candidate.Path));
        var more = resolution.Candidates.Count > MaximumListedCandidates
            ? string.Create(CultureInfo.InvariantCulture,
                $", {resolution.Candidates.Count - MaximumListedCandidates} more")
            : "";
        return $"{reason} (examined: {listed}{more})";
    }

    /// <summary>The D14 refusal: the prefix and the first distinct blocking reasons in decision order.</summary>
    private static string NoClipRefusal(NifModelAnimationResult result)
    {
        var reasons = result.Decisions
            .Where(static decision => !decision.IsTyped && !NonBlockingCodes.Contains(decision.Code))
            .Select(static decision => decision.Reason!)
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumListedReasons)
            .ToList();
        return $"{NoClipExpressibleReason}: {(reasons.Count == 0 ? NoControlledBlocksReason : string.Join("; ", reasons))}";
    }

    /// <summary>
    ///     Reads the resolved skeleton's node graph: parsed, checked against the scene-graph key, every block decoded as
    ///     the reader decodes a <c>.nif</c>, walked by the cut-1a node reader (reachability, palette names, nodes).
    /// </summary>
    private static NifModelNodeGraph ReadSkeleton(string path, ModelSourceItem item, byte[] bytes, string sha256,
        List<SceneDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var info = NifParser.Parse(bytes)
                   ?? throw new InvalidDataException(
                       $"The skeleton '{path}' header could not be parsed (truncated or malformed).");
        var rejection =
            NifModelProbe.ScopeRejection(info.BinaryVersion, info.UserVersion, info.BsVersion, info.IsBigEndian) ??
            NifModelProbe.GraphScopeRejection(info.BinaryVersion, info.UserVersion, info.BsVersion, info.IsBigEndian);
        if (rejection is not null)
        {
            throw new NotSupportedException($"The skeleton '{path}' is not a scene graph the reader reads: {rejection}");
        }

        if (info.BlockCount > NifModelReader.MaximumBlocks)
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The skeleton '{path}' declares {info.BlockCount} blocks, more than the {NifModelReader.MaximumBlocks} " +
                $"the model budgets allow."));
        }

        try
        {
            var schema = NifSchema.LoadEmbedded();
            var decoder = new NifBlockDecoder(schema, info, bytes);
            var footer = decoder.ValidateLayout();
            if (NifModelReader.IsAnimationStream(schema, info, footer))
            {
                throw new NotSupportedException($"The skeleton '{path}' is a .kf animation stream, not a scene graph.");
            }

            var blocks = NifModelReader.DecodeBlocks(decoder, schema, info, cancellationToken);
            var skeletonState = new NifModelReadState(item, ModelNativeDetail.Metadata, bytes, sha256, info, schema,
                decoder, footer, blocks);
            var reachable = NifModelCoverage.ReferenceReachability(blocks, footer.Roots, cancellationToken);
            var palette = NifModelPaletteNames.Read(skeletonState, reachable, cancellationToken);
            var graph = NifModelNodeReader.Read(skeletonState, palette, cancellationToken);
            diagnostics.AddRange(graph.Diagnostics);
            diagnostics.AddRange(palette.Diagnostics);
            return graph;
        }
        catch (InvalidDataException failure)
        {
            throw new InvalidDataException($"The skeleton '{path}': {failure.Message}", failure);
        }
    }

    /// <summary>
    ///     Every controlled block's target name matched against the skeleton, footer sequence by footer sequence, for the
    ///     provenance row (duplicates are fine: the provenance lists each name once).
    /// </summary>
    private static List<NifModelTargetMatch> TargetMatches(NifModelReadState state, NifModelNodeGraph skeleton,
        CancellationToken cancellationToken)
    {
        var targets = NifModelTargetNames.ForSkeleton(skeleton);
        var source = new NifModelAnimationSource(state);
        var matches = new List<NifModelTargetMatch>();
        foreach (var root in state.Footer.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!source.TryReadSequence(root, out var view))
            {
                continue;
            }

            foreach (var block in view.ControlledBlocks)
            {
                matches.Add(targets.Match(block.NodeNameIndex, source.Strings));
            }
        }

        return matches;
    }

    /// <summary>
    ///     The <c>.kf</c>'s coverage: the animation stage's decision for a decided block (Typed wins), else the reachability
    ///     rules, else the plan 2.1 table row (particle reach included).
    /// </summary>
    private static ModelSourceClassification[] Classify(NifModelReadState state, NifModelAnimationResult result,
        CancellationToken cancellationToken)
    {
        var reachable = NifModelCoverage.ReferenceReachability(state.Blocks, state.Footer.Roots, cancellationToken);
        var particleOnly = NifModelAnimationCoverage.ParticleReach(state, cancellationToken);
        var classifications = new ModelSourceClassification[state.Blocks.Count];
        for (var block = 0; block < classifications.Length; block++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            classifications[block] = NifModelCoverage.Classify(state.Schema, block, state.Blocks[block].Type, false,
                reachable[block], result.Dispositions.TryGetValue(block, out var disposition) ? disposition : null,
                particleOnly.Contains(block));
        }

        return classifications;
    }

    /// <summary>A graph with no node, one empty occurrence list per <c>.kf</c> block (the block rows target the document).</summary>
    private static NifModelNodeGraph EmptyGraph(int blockCount)
    {
        var occurrences = new IReadOnlyList<int>[blockCount];
        Array.Fill<IReadOnlyList<int>>(occurrences, Array.Empty<int>());
        return new NifModelNodeGraph([], [], occurrences, new Dictionary<int, NifModelNodeFacts>(), []);
    }

    /// <summary>A geometry result with nothing in it (a <c>.kf</c> has no geometry).</summary>
    private static NifModelGeometryResult EmptyGeometry()
    {
        return new NifModelGeometryResult([], new Dictionary<int, int>(), new Dictionary<int, int>(),
            new Dictionary<int, int>(), new Dictionary<int, NifModelBlockDisposition>(), [], []);
    }
}

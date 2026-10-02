using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Types the skins of placed, drawable, PC-layout geometry (plan section 3, "Skin"; section 6, slice 7):
///     NiSkinInstance and BSDismemberSkinInstance become <see cref="SceneSkin" /> palettes (joints, inverse binds,
///     skeleton root, BindMode InverseBind), the skinned geometry's occurrences get their
///     <see cref="SceneNode.SkinIndex" />, bone occurrences get the Joint role, the primitive gets its
///     <see cref="SceneSkinInfluences" />, and a dismember instance's body parts become Face-domain streams.
/// </summary>
/// <remarks>
///     <para>
///         Joints: bone k of the skin instance is joint k. A bone block placed once resolves to that occurrence; a bone
///         block placed several times resolves to its one occurrence under the resolved skeleton root, and throws when
///         that is not exactly one. The skeleton root resolves to the block's only occurrence, or to the one occurrence
///         that contains the skinned shape; otherwise it is left unresolved (null) with a diagnostic. A bone that is not
///         placed in the scene graph, or a bone repeated in the palette, is corrupt input and throws
///         <see cref="InvalidDataException" />. A resolved skeleton root that does not contain every joint is dropped
///         from the typed skin (Shared requires it to) and reported. One <see cref="SceneSkin" /> exists per skin
///         instance and resolved palette, so instanced geometry that sees the same bones shares it.
///     </para>
///     <para>
///         Inverse binds: joint k's inverse bind is bone k's NiSkinData Skin Transform composed S R^T T
///         (<see cref="NifModelTransform.ComposeMatrix" />), which is exactly how BMT's renderer
///         (<c>NifSkinBlockParser.ParseNiTransform</c>, used by <c>NifShapeSkinningDataBuilder</c>) and its GLB export
///         (<c>NifExportExtractor</c>: <c>InverseBindMatrices = bone.InverseBindPose</c>) build them. Nothing is
///         orthonormalized; the writers project Float32 binds themselves (plan decisions, 2026-09-24). The bind analysis
///         (orthonormality error, determinant) is recorded per bone.
///     </para>
///     <para>
///         The NiSkinData overall Skin Transform is kept in native state and reported by a diagnostic when it is not the
///         identity; it is not folded into the inverse binds. Evidence from BMT's working code: the renderer skins with
///         <c>InverseBindPose x BoneWorld</c> alone (<c>NifShapeSkinningDataBuilder.BuildBoneSkinMatrices</c>) and the GLB
///         export writes the per-bone transforms alone, and both place FNV armor, whose skins carry a non-identity overall
///         transform about 62% of the time (for example a translation of (0, -0.80, -0.31)), on the body. That is the
///         InverseBind contract Shared declares: position times inverse bind times joint world. The overall transform
///         relates skin space to the skeleton root, which the per-bone transforms already encode.
///     </para>
///     <para>
///         Influences come from <see cref="NifModelSkinInfluences" />: NiSkinData's weights when Has Vertex Weights is not
///         0, else the partitions' weights; with neither, the skin is not typed (Shared requires a bound skin to carry
///         influences, and an unbound palette would reach writers only as a dropped row), the geometry stays in bind
///         space unskinned, the skin blocks are NativeOnly <see cref="NifModelCoverage.SkinNoInfluencesReason" /> and a
///         diagnostic says so. Joints that span several scene roots, or a non-finite bind or weight, are handled the
///         same way with their own reasons.
///     </para>
///     <para>
///         Partitions (PC): read and checked (<see cref="NifSkinPartitionView" />) whenever they decoded exactly, and
///         compared with the typed triangles and NiSkinData weights; the comparison is native state. A partition is Typed
///         when it feeds typed state (the influences, or the dismember faces of <see cref="NifModelDismemberFaces" />),
///         otherwise NativeOnly <see cref="NifModelCoverage.PartitionNativeReason" />.
///     </para>
///     <para>
///         Console packed geometry (slice 10): its partitions were read and checked by <see cref="NifPackedGeometryReader" />
///         (they give the primitive's vertex order) and the influences come from the packed weight and bone-index
///         channels (<see cref="NifModelSkinInfluences.FromPacked" />): stride 4 in packed order, slot k = the owning
///         partition's Bones[decoded index]; on the Xbox 360 the fourth weight is the engine's derived lane
///         1f - ((w0 + w1) + w2) on the stored slot-3 bone and the stored fourth half is never read
///         (<see cref="NifPackedEngineLanes" />), on the PS3 the stored halves are typed with the slot-3 sentinel read as
///         0; no renormalization. The console NiSkinData
///         stores no vertex weights (Has Vertex Weights 0 on every sampled skin), and the NiSkinData path is not used
///         even when it does; its state is recorded. The partition block is Typed (it fed the vertex order and the
///         influences), and a dismember instance's body parts become face streams through
///         <see cref="NifPackedDismemberFaces" />, one entry per partition in partition order. Packed geometry that
///         yields no primitive hands its skin blocks the geometry's NativeOnly reason through <see cref="MarkUntyped" />.
///     </para>
///     <para>
///         Skin blocks are decoded tolerantly (whether they feed typed state is known only here) and must have decoded
///         exactly once they do; a skin instance whose Data or Skin Partition link names the wrong block type, or whose
///         bone count differs from its NiSkinData's, is corrupt input.
///     </para>
/// </remarks>
internal sealed class NifModelSkinReader
{
    /// <summary>The native-state kind of the per-skin rows.</summary>
    public const string SkinKind = "bmt.nif.skin";

    /// <summary>The payload version of <see cref="SkinKind" /> rows.</summary>
    public const int SkinPayloadVersion = 1;

    /// <summary>Diagnostic code for an NiSkinData overall Skin Transform that is not the identity.</summary>
    public const string OverallTransformDiagnostic = "bmt.nif.skin-overall-transform";

    /// <summary>Diagnostic code for a skin kept as native state only (the geometry is placed unskinned).</summary>
    public const string NotTypedDiagnostic = "bmt.nif.skin-not-typed";

    /// <summary>Diagnostic code for a Skeleton Root that is null, unplaced, ambiguous or does not contain every joint.</summary>
    public const string SkeletonRootDiagnostic = "bmt.nif.skin-skeleton-root";

    /// <summary>Diagnostic code for dismember body parts that could not be typed as Face-domain streams.</summary>
    public const string DismemberDiagnostic = "bmt.nif.dismember-faces";

    /// <summary>The base skin instance type.</summary>
    public const string SkinInstanceType = "NiSkinInstance";

    /// <summary>The Bethesda dismember skin instance type.</summary>
    public const string DismemberType = "BSDismemberSkinInstance";

    /// <summary>The rule recorded for the inverse binds.</summary>
    public const string InverseBindRule =
        "joint k = bone k; inverse bind k = S R^T T of bone k's NiSkinData Skin Transform, each element rounded to " +
        "Float32 once (exact copies when s = 1); no orthonormalization; the overall Skin Transform is not folded";

    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<int, (Matrix4x4[]? Binds, string? Detail)> _bindsByData = [];
    private readonly Dictionary<int, NifSkinDataView> _dataViews = [];
    private readonly NifModelDiagnosticSink _diagnostics = new();
    private readonly Dictionary<int, NifModelBlockDisposition> _dispositions = [];
    private readonly NifModelNodeGraph _graph;
    private readonly HashSet<int> _joints = [];
    private readonly List<int> _locationBlocks = [];
    private readonly int[] _parentByNode;
    private readonly List<JsonObject> _payloads = [];
    private readonly HashSet<int> _reportedOverall = [];
    private readonly Dictionary<int, int> _skinByFedBlock = [];
    private readonly Dictionary<string, int> _skinByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int> _skinByNode = [];
    private readonly List<SceneSkin> _skins = [];
    private readonly NifModelReadState _state;

    /// <summary>Creates the reader for one read, indexing each node occurrence's parent.</summary>
    public NifModelSkinReader(NifModelReadState state, NifModelNodeGraph graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(graph);
        _state = state;
        _graph = graph;
        _cancellationToken = cancellationToken;
        _parentByNode = new int[graph.Nodes.Count];
        Array.Fill(_parentByNode, -1);
        for (var node = 0; node < graph.Nodes.Count; node++)
        {
            foreach (var child in graph.Nodes[node].Children)
            {
                _parentByNode[child] = node;
            }
        }
    }

    /// <summary>
    ///     Types the skin of one placed, drawable geometry block and records the skin of each of its occurrences. Called
    ///     by the geometry reader once per geometry block, in block order, so skins are numbered deterministically.
    /// </summary>
    /// <param name="shape">The geometry block.</param>
    /// <param name="data">Its drawable data.</param>
    /// <param name="occurrences">The geometry block's node occurrences, all of which receive a mesh.</param>
    /// <returns>What the geometry's primitives carry; <see cref="NifModelShapeSkin.None" /> without a skin instance.</returns>
    /// <exception cref="InvalidDataException">The skin is corrupt (see the type remarks).</exception>
    public NifModelShapeSkin Read(NifDecodedBlock shape, NifModelGeometryData data, IReadOnlyList<int> occurrences)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(occurrences);
        _cancellationToken.ThrowIfCancellationRequested();
        if (SkinInstanceLink(shape) is not { } instanceIndex)
        {
            return NifModelShapeSkin.None;
        }

        var instanceBlock = _state.Blocks[instanceIndex];
        RequireComplete(instanceBlock);
        var instance = NifSkinInstanceView.Read(instanceBlock,
            _state.Schema.Inherits(instanceBlock.Type, DismemberType));
        var dataBlock = RequireLinked(instanceBlock, instance.DataLink, "Data", "NiSkinData");
        RequireComplete(dataBlock);
        var skinData = DataView(dataBlock);
        if (instance.Bones.Count != skinData.Bones.Count)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"NIF block {instanceBlock.Index} ({instanceBlock.Type}) lists {instance.Bones.Count} bones, but its " +
                $"NiSkinData block {dataBlock.Index} stores {skinData.Bones.Count}."));
        }

        var partitionBlock = instance.PartitionLink == -1
            ? null
            : RequireLinked(instanceBlock, instance.PartitionLink, "Skin Partition", "NiSkinPartition");
        var facts = new JsonObject
        {
            ["skinInstance"] = instanceBlock.Index,
            ["skinInstanceType"] = instanceBlock.Type,
            ["skinData"] = dataBlock.Index,
            ["skinPartition"] = instance.PartitionLink,
            ["hasVertexWeights"] = skinData.HasVertexWeights,
            ["occurrences"] = NifModelNativeValues.Integers(occurrences)
        };
        ReportOverall(dataBlock, skinData);

        IReadOnlyList<NifSkinPartitionView>? partitions = null;
        if (data.Packed is not null)
        {
            // The packed reader read and checked the partitions of this same skin instance; they gave the packed
            // vertex order and are reused here rather than re-read.
            partitions = data.PackedPartitions;
            facts["packedBlock"] = data.Packed.BlockIndex;
            facts["packedLayout"] = data.Packed.Layout.Id;
        }
        else if (partitionBlock is not null)
        {
            if (partitionBlock.IsComplete)
            {
                partitions = NifSkinPartitionView.ReadAll(partitionBlock, skinData.Bones.Count, data.VertexCount);
            }
            else
            {
                facts["partitionDecode"] = "incomplete: the partition is kept as native state and not compared";
            }
        }

        // Joints depend on an occurrence only through its resolved skeleton root, so each distinct root is resolved
        // once however many times the shape is instanced.
        var resolutions = new JointResolution[occurrences.Count];
        var byRoot = new Dictionary<(int Root, string Resolution), JointResolution>();
        for (var i = 0; i < resolutions.Length; i++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var (root, rootResolution) = ResolveSkeletonRoot(instance, occurrences[i]);
            if (!byRoot.TryGetValue((root ?? -1, rootResolution), out var resolution))
            {
                resolution = ResolveJoints(instanceBlock, instance, root, rootResolution, out var unplaced);
                if (resolution is null)
                {
                    return NotTyped(shape, instance, dataBlock, partitionBlock, facts,
                        NifModelCoverage.SkinUnplacedBoneReason, unplaced!);
                }

                byRoot.Add((root ?? -1, rootResolution), resolution);
            }

            resolutions[i] = resolution;
        }

        foreach (var resolution in byRoot.Values)
        {
            if (!SharesOneRoot(resolution.Joints))
            {
                return NotTyped(shape, instance, dataBlock, partitionBlock, facts, NifModelCoverage.SkinRootsReason,
                    "its joints lie under different scene roots, and Shared requires one common ancestor.");
            }
        }

        var (binds, bindDetail) = InverseBinds(dataBlock, skinData);
        if (binds is null)
        {
            return NotTyped(shape, instance, dataBlock, partitionBlock, facts, NifModelCoverage.SkinNonFiniteReason,
                bindDetail!);
        }

        (SceneSkinInfluences? Influences, string? Reason, string? Detail, JsonObject Facts) built;
        if (data.Packed is { } packed && partitions is not null && data.PackedPartitionStarts is { } starts)
        {
            built = NifModelSkinInfluences.FromPacked(packed, partitions, starts);
            built.Facts["skinDataStoresWeights"] = skinData.StoresWeights;
            built.Facts["skinDataWeightsUsed"] = false;
        }
        else if (skinData.StoresWeights)
        {
            built = NifModelSkinInfluences.FromSkinData(dataBlock, skinData, data.VertexCount);
        }
        else if (partitions is not null)
        {
            built = NifModelSkinInfluences.FromPartitions(partitions, data.VertexCount);
        }
        else
        {
            built = (null, NifModelCoverage.SkinNoInfluencesReason, partitionBlock is null
                    ? "NiSkinData stores no vertex weights and the skin instance names no NiSkinPartition."
                    : "NiSkinData stores no vertex weights and the NiSkinPartition did not decode exactly.",
                new JsonObject { ["source"] = "none" });
        }

        var (influences, reason, detail, influenceFacts) = built;
        facts["influences"] = influenceFacts;
        if (reason is not null)
        {
            return NotTyped(shape, instance, dataBlock, partitionBlock, facts, reason, detail!);
        }

        var partitionFed = data.Packed is not null || !skinData.StoresWeights;
        if (partitions is not null)
        {
            facts["partitions"] = data.Packed is null
                ? PartitionFacts(partitions, data, skinData)
                : PackedPartitionFacts(partitions, data);
        }

        SceneFaceList? faces = null;
        IReadOnlyList<SceneAttributeStream> faceAttributes = Array.Empty<SceneAttributeStream>();
        if (instance.BodyParts is { } bodyParts)
        {
            if (partitions is null)
            {
                var missing = partitionBlock is null
                    ? "no NiSkinPartition: the body parts have no triangles to address"
                    : "the NiSkinPartition did not decode exactly";
                facts["dismember"] = new JsonObject { ["typed"] = false, ["reason"] = missing };
                ReportDismember(shape, instanceBlock, missing);
            }
            else
            {
                var (builtFaces, streams, failure, dismemberFacts) = data.Packed is null
                    ? NifModelDismemberFaces.Build(data.Triangulation!.Indices, partitions, bodyParts,
                        _cancellationToken)
                    : NifPackedDismemberFaces.Build(data.Triangulation!.Indices, partitions, bodyParts,
                        _cancellationToken);
                dismemberFacts["typed"] = failure is null;
                facts["dismember"] = dismemberFacts;
                if (failure is not null)
                {
                    dismemberFacts["reason"] = failure;
                    ReportDismember(shape, instanceBlock, failure);
                }
                else
                {
                    faces = builtFaces;
                    faceAttributes = streams;
                    partitionFed = true;
                }
            }
        }

        var shapeName = _graph.FactsByBlock.TryGetValue(shape.Index, out var shapeFacts) ? shapeFacts.Name : "";
        var skinIndices = new int[occurrences.Count];
        var skinByResolution = new Dictionary<JointResolution, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < occurrences.Count; i++)
        {
            if (!skinByResolution.TryGetValue(resolutions[i], out var skin))
            {
                skin = SkinFor(shape, shapeName, instance, dataBlock, skinData, binds, resolutions[i]);
                skinByResolution.Add(resolutions[i], skin);
                _joints.UnionWith(resolutions[i].Joints);
            }

            skinIndices[i] = skin;
            _skinByNode[occurrences[i]] = skin;
        }

        Mark(instanceBlock.Index, NifModelBlockDisposition.Typed);
        Mark(dataBlock.Index, NifModelBlockDisposition.Typed);
        _skinByFedBlock.TryAdd(instanceBlock.Index, skinIndices[0]);
        _skinByFedBlock.TryAdd(dataBlock.Index, skinIndices[0]);
        if (partitionBlock is not null)
        {
            Mark(partitionBlock.Index, partitionFed
                ? NifModelBlockDisposition.Typed
                : NifModelBlockDisposition.NativeOnly(NifModelCoverage.PartitionNativeReason));
            _skinByFedBlock.TryAdd(partitionBlock.Index, skinIndices[0]);
        }

        facts["typed"] = true;
        facts["skins"] = NifModelNativeValues.Integers(skinIndices);
        facts["partitionFeedsTypedState"] = partitionBlock is null ? null : JsonValue.Create(partitionFed);
        return new NifModelShapeSkin(influences, faces, faceAttributes, facts);
    }

    /// <summary>
    ///     Gives the skin blocks of a placed geometry that yields no primitive the geometry's own NativeOnly reason (for
    ///     console packed geometry, <see cref="NifModelCoverage.PackedLayoutUnknownReason" /> or
    ///     <see cref="NifModelCoverage.PackedPayloadReason" />), unless another geometry typed them. The blocks' decodes
    ///     are not required to be exact here.
    /// </summary>
    public void MarkUntyped(NifDecodedBlock shape, string reason)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (SkinInstanceLink(shape) is not { } instanceIndex)
        {
            return;
        }

        var disposition = NifModelBlockDisposition.NativeOnly(reason);
        Mark(instanceIndex, disposition);
        var (dataLink, partitionLink) = NifSkinInstanceView.TryReadLinks(_state.Blocks[instanceIndex],
            _state.Blocks.Count);
        if (dataLink is { } dataIndex && _state.Schema.Inherits(_state.Blocks[dataIndex].Type, "NiSkinData"))
        {
            Mark(dataIndex, disposition);
        }

        if (partitionLink is { } partitionIndex &&
            _state.Schema.Inherits(_state.Blocks[partitionIndex].Type, "NiSkinPartition"))
        {
            Mark(partitionIndex, disposition);
        }
    }

    /// <summary>Finishes the read: skins, node bindings, joints, native rows, dispositions and diagnostics.</summary>
    public NifModelSkinResult Complete()
    {
        var owner = _state.Item.Reference;
        var rows = new List<SceneNativeState>(_skins.Count);
        for (var i = 0; i < _skins.Count; i++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var block = _state.Blocks[_locationBlocks[i]];
            rows.Add(new SceneNativeState(new SceneElementRef(SceneElementKind.Skin, i), SkinKind,
                SkinPayloadVersion, _payloads[i].ToJsonString(),
                new SceneSourceLocation(owner.SourceId, NifModelCoverage.Identity(block.Index), block.Offset,
                    block.Size, owner)));
        }

        return new NifModelSkinResult(_skins.AsReadOnly(), _skinByNode, _joints, rows.AsReadOnly(), _dispositions,
            _skinByFedBlock, _diagnostics.ToList());
    }

    /// <summary>The shape's skin instance, or null for none.</summary>
    /// <exception cref="InvalidDataException">The link names a block that is not an NiSkinInstance.</exception>
    private int? SkinInstanceLink(NifDecodedBlock shape)
    {
        if (!shape.Root.TryGet("Skin Instance", out var value) || value is not NifRefValue link || link.IsNone)
        {
            return null;
        }

        if ((uint)link.Index >= (uint)_state.Blocks.Count ||
            !_state.Schema.Inherits(_state.Blocks[link.Index].Type, SkinInstanceType))
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"NIF block {shape.Index} ({shape.Type}) links block {link.Index} as its Skin Instance, which is not " +
                $"an NiSkinInstance."));
        }

        return link.Index;
    }

    private NifDecodedBlock RequireLinked(NifDecodedBlock owner, int link, string field, string expectedType)
    {
        if (link == -1)
        {
            throw new InvalidDataException(
                $"NIF block {owner.Index} ({owner.Type}) has no {field} link; a skin instance of placed geometry must " +
                $"name its {expectedType}.");
        }

        if ((uint)link >= (uint)_state.Blocks.Count || !_state.Schema.Inherits(_state.Blocks[link].Type, expectedType))
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"NIF block {owner.Index} ({owner.Type}) links block {link} as its {field}; it must be an " +
                $"{expectedType}."));
        }

        return _state.Blocks[link];
    }

    /// <summary>A skin block about to feed typed state must have decoded exactly (plan section 1, self-check 1).</summary>
    private static void RequireComplete(NifDecodedBlock block)
    {
        if (block.IsComplete)
        {
            return;
        }

        var detail = block.Failure?.Message ??
                     (block.Problems.Count > 0
                         ? block.Problems[0].Message
                         : $"consumed {block.ConsumedBytes} of {block.Size} bytes");
        throw new InvalidDataException(
            $"NIF block {block.Index} ({block.Type}) feeds a typed skin but did not decode exactly: {detail}");
    }

    private NifSkinDataView DataView(NifDecodedBlock block)
    {
        if (!_dataViews.TryGetValue(block.Index, out var view))
        {
            view = NifSkinDataView.Read(block);
            _dataViews.Add(block.Index, view);
        }

        return view;
    }

    /// <summary>The inverse binds of one NiSkinData, computed once; null with a detail when one is not finite.</summary>
    private (Matrix4x4[]? Binds, string? Detail) InverseBinds(NifDecodedBlock block, NifSkinDataView data)
    {
        if (_bindsByData.TryGetValue(block.Index, out var cached))
        {
            return cached;
        }

        var binds = new Matrix4x4[data.Bones.Count];
        (Matrix4x4[]? Binds, string? Detail) result = (binds, null);
        for (var b = 0; b < binds.Length; b++)
        {
            var transform = data.Bones[b].Transform;
            binds[b] = transform.ToMatrix();
            if (!transform.IsFinite || !SceneAffineTransform.IsFiniteAffine(binds[b]))
            {
                result = (null, string.Create(CultureInfo.InvariantCulture,
                    $"NiSkinData block {block.Index} Bone List[{b}] Skin Transform is not finite, or its scale times " +
                    $"its rotation overflows Float32 ({transform.Describe()})."));
                break;
            }
        }

        _bindsByData.Add(block.Index, result);
        return result;
    }

    /// <summary>Reports a non-identity overall Skin Transform once per NiSkinData block.</summary>
    private void ReportOverall(NifDecodedBlock block, NifSkinDataView data)
    {
        if (data.Overall.IsIdentity || !_reportedOverall.Add(block.Index))
        {
            return;
        }

        _diagnostics.Add(OverallTransformDiagnostic, string.Create(CultureInfo.InvariantCulture,
            $"Block {block.Index} (NiSkinData): the overall Skin Transform is not the identity " +
            $"({data.Overall.Describe()}). It is kept in native state and not folded into the inverse binds: the " +
            $"per-bone Skin Transforms already map skin-space vertices to their bones, which is how BMT's renderer and " +
            $"GLB export skin today."));
    }

    /// <summary>
    ///     Resolves every bone to one placed occurrence under the given skeleton root.
    /// </summary>
    /// <returns>
    ///     The resolution, or null with <paramref name="unplaced" /> set when a bone link is null or names a block that is
    ///     not placed (the skin is then not typed, as BMT's renderer tolerates such bones rather than failing).
    /// </returns>
    /// <exception cref="InvalidDataException">A bone link names no block, a bone is ambiguous, or a joint repeats.</exception>
    private JointResolution? ResolveJoints(NifDecodedBlock instanceBlock, NifSkinInstanceView instance, int? root,
        string rootResolution, out string? unplaced)
    {
        unplaced = null;
        var joints = new int[instance.Bones.Count];
        var ordinalByJoint = new Dictionary<int, int>();
        for (var k = 0; k < joints.Length; k++)
        {
            var bone = instance.Bones[k];
            if (bone == -1)
            {
                unplaced = string.Create(CultureInfo.InvariantCulture,
                    $"Bones[{k}] of block {instanceBlock.Index} ({instanceBlock.Type}) is a null link.");
                return null;
            }

            if ((uint)bone >= (uint)_state.Blocks.Count)
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"NIF block {instanceBlock.Index} ({instanceBlock.Type}) Bones[{k}] = {bone} names no bone block."));
            }

            var placed = _graph.OccurrencesByBlock[bone];
            int joint;
            if (placed.Count == 0)
            {
                unplaced = string.Create(CultureInfo.InvariantCulture,
                    $"Bones[{k}] of block {instanceBlock.Index} ({instanceBlock.Type}) names block {bone} " +
                    $"({_state.Blocks[bone].Type}), which is not placed in the scene graph (not reachable from the " +
                    $"footer roots through Children links).");
                return null;
            }

            if (placed.Count == 1)
            {
                joint = placed[0];
            }
            else
            {
                var under = root is { } skeleton
                    ? placed.Where(o => IsAncestorOrSelf(skeleton, o)).ToList()
                    : new List<int>();
                if (under.Count != 1)
                {
                    throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                        $"NIF block {instanceBlock.Index} ({instanceBlock.Type}) Bones[{k}] names block {bone}, which " +
                        $"is placed {placed.Count} times, and {under.Count} of those occurrences lie under the resolved " +
                        $"skeleton root ({rootResolution}): the joint is ambiguous."));
                }

                joint = under[0];
            }

            if (!ordinalByJoint.TryAdd(joint, k))
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"NIF block {instanceBlock.Index} ({instanceBlock.Type}) Bones[{k}] repeats the bone of " +
                    $"Bones[{ordinalByJoint[joint]}] (block {bone}); a skin cannot repeat a joint."));
            }

            joints[k] = joint;
        }

        var contains = root is not { } declared || joints.All(joint => IsAncestorOrSelf(declared, joint));
        return new JointResolution(instance.SkeletonRoot, root, rootResolution, joints, contains);
    }

    private (int? Occurrence, string Resolution) ResolveSkeletonRoot(NifSkinInstanceView instance, int shapeNode)
    {
        var block = instance.SkeletonRoot;
        if (block == -1)
        {
            return (null, "none: the Skeleton Root link is null");
        }

        if ((uint)block >= (uint)_state.Blocks.Count || _graph.OccurrencesByBlock[block].Count == 0)
        {
            return (null, "unplaced: the Skeleton Root block is not in the scene graph");
        }

        var placed = _graph.OccurrencesByBlock[block];
        if (placed.Count == 1)
        {
            return (placed[0], "the block's only occurrence");
        }

        var containing = placed.Where(o => IsAncestorOrSelf(o, shapeNode)).ToList();
        return containing.Count == 1
            ? (containing[0], "the one occurrence that contains the skinned geometry")
            : (null, string.Create(CultureInfo.InvariantCulture,
                $"ambiguous: the block is placed {placed.Count} times and {containing.Count} of those occurrences " +
                $"contain the skinned geometry"));
    }

    private int SkinFor(NifDecodedBlock shape, string shapeName, NifSkinInstanceView instance,
        NifDecodedBlock dataBlock, NifSkinDataView data, Matrix4x4[] binds, JointResolution resolution)
    {
        var skeletonRoot = resolution.RootContainsJoints ? resolution.SkeletonRoot : null;
        var key = string.Create(CultureInfo.InvariantCulture,
            $"{instance.BlockIndex}|{skeletonRoot ?? -1}|{string.Join(',', resolution.Joints)}");
        if (_skinByKey.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (resolution.SkeletonRoot is null || !resolution.RootContainsJoints)
        {
            var why = resolution.SkeletonRoot is null
                ? resolution.RootResolution
                : "the resolved skeleton root does not contain every joint, so the typed skin declares none";
            _diagnostics.Add(SkeletonRootDiagnostic, string.Create(CultureInfo.InvariantCulture,
                $"Block {instance.BlockIndex} ({instance.Type}) for block {shape.Index} ({shape.Type}): skeleton " +
                $"root block {instance.SkeletonRoot} is not declared on the typed skin ({why}); the authored link is " +
                $"kept in native state."));
        }

        var index = _skins.Count;
        _skins.Add(new SceneSkin(shapeName, resolution.Joints, binds, skeletonRoot));
        _skinByKey.Add(key, index);
        _locationBlocks.Add(instance.BlockIndex);
        _payloads.Add(SkinPayload(shape, instance, dataBlock, data, resolution, skeletonRoot));
        return index;
    }

    private JsonObject SkinPayload(NifDecodedBlock shape, NifSkinInstanceView instance, NifDecodedBlock dataBlock,
        NifSkinDataView data, JointResolution resolution, int? declaredRoot)
    {
        var jointBlocks = instance.Bones;
        var payload = new JsonObject
        {
            ["skinInstance"] = instance.BlockIndex,
            ["skinInstanceType"] = instance.Type,
            ["skinData"] = dataBlock.Index,
            ["skinPartition"] = instance.PartitionLink,
            ["firstShape"] = shape.Index,
            ["bindMode"] = nameof(SceneSkinBindMode.InverseBind),
            ["skeletonRoot"] = new JsonObject
            {
                ["block"] = instance.SkeletonRoot,
                ["occurrence"] = resolution.SkeletonRoot,
                ["resolution"] = resolution.RootResolution,
                ["containsEveryJoint"] = resolution.RootContainsJoints,
                ["declaredOnSkin"] = declaredRoot
            },
            ["jointBlocks"] = NifModelNativeValues.Integers(jointBlocks),
            ["jointOccurrences"] = NifModelNativeValues.Integers(resolution.Joints),
            ["jointBlockOccurrenceCounts"] = NifModelNativeValues.Integers(
                jointBlocks.Select(b => _graph.OccurrencesByBlock[b].Count).ToList()),
            ["inverseBindRule"] = InverseBindRule,
            ["bones"] = BonesJson(data),
            ["overallTransform"] = OverallJson(data)
        };
        return payload;
    }

    private static JsonNode BonesJson(NifSkinDataView data)
    {
        if (data.Bones.Count <= NifModelNativeValues.MaximumInlineElements)
        {
            return new JsonArray(data.Bones.Select(bone => (JsonNode?)bone.Transform.ToJson()).ToArray());
        }

        var maximumError = 0.0;
        int negative = 0, scaled = 0;
        Span<double> stored = stackalloc double[9];
        foreach (var bone in data.Bones)
        {
            var transform = bone.Transform;
            if (transform.Scale != 1f)
            {
                scaled++;
            }

            for (var i = 0; i < 9; i++)
            {
                stored[i] = transform.RowMajorRotation[i];
            }

            maximumError = Math.Max(maximumError, NifModelTransform.OrthonormalityError(stored));
            if (NifModelTransform.Determinant(stored) < 0)
            {
                negative++;
            }
        }

        return new JsonObject
        {
            ["count"] = data.Bones.Count,
            ["maximumOrthonormalityError"] = NifModelNativeValues.Double(maximumError),
            ["negativeDeterminants"] = negative,
            ["scaleNotOne"] = scaled,
            ["listed"] = "the exact transforms are in the NiSkinData block row"
        };
    }

    private static JsonObject OverallJson(NifSkinDataView data)
    {
        var overall = data.Overall.ToJson();
        overall["foldedIntoInverseBinds"] = false;
        overall["rule"] = "kept as authored; the per-bone Skin Transforms alone are the inverse binds, as BMT's " +
                          "renderer and GLB export skin";
        return overall;
    }

    /// <summary>Partition facts (PC): shapes, vertex coverage, triangle and weight agreement with the typed data.</summary>
    private JsonObject PartitionFacts(IReadOnlyList<NifSkinPartitionView> partitions, NifModelGeometryData data,
        NifSkinDataView skinData)
    {
        var facts = new JsonObject { ["count"] = partitions.Count };
        if (partitions.Count <= NifModelNativeValues.MaximumInlineElements)
        {
            facts["partitions"] = new JsonArray(partitions.Select(p => (JsonNode?)p.ToJson()).ToArray());
        }
        else
        {
            facts["partitions"] = "see the NiSkinPartition block row";
        }

        var mapped = new int[data.VertexCount];
        var partitionKeys = new HashSet<(int, int, int)>();
        foreach (var partition in partitions)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (partition.VertexMap is not { } map)
            {
                continue;
            }

            foreach (var vertex in map)
            {
                mapped[vertex]++;
            }

            if (partition.Triangles is { } triangles)
            {
                for (var t = 0; t + 2 < triangles.Indices.Length; t += 3)
                {
                    partitionKeys.Add(NifModelDismemberFaces.TriangleKey(map[triangles.Indices[t]],
                        map[triangles.Indices[t + 1]], map[triangles.Indices[t + 2]]));
                }
            }
        }

        facts["verticesMapped"] = mapped.Count(count => count > 0);
        facts["verticesMappedMoreThanOnce"] = mapped.Count(count => count > 1);
        facts["verticesUnmapped"] = mapped.Count(count => count == 0);

        var indices = data.Triangulation!.Indices;
        var primitiveKeys = new HashSet<(int, int, int)>();
        var inPartitions = 0;
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            var key = NifModelDismemberFaces.TriangleKey(indices[t], indices[t + 1], indices[t + 2]);
            primitiveKeys.Add(key);
            if (partitionKeys.Contains(key))
            {
                inPartitions++;
            }
        }

        facts["triangles"] = new JsonObject
        {
            ["primitive"] = indices.Length / 3,
            ["foundInPartitions"] = inPartitions,
            ["partitionOnly"] = partitionKeys.Count(key => !primitiveKeys.Contains(key)),
            ["rule"] = "shape vertex indices, winding kept (rotation starting at the smallest index)"
        };

        if (skinData.StoresWeights)
        {
            facts["weightsAgainstSkinData"] = WeightComparison(partitions, skinData, data.VertexCount);
        }

        return facts;
    }

    /// <summary>
    ///     Partition facts (packed): shapes and vertex coverage of the shape domain; the primitive's triangles are the
    ///     partitions' own by construction, so no triangle comparison is needed and none is made.
    /// </summary>
    private JsonObject PackedPartitionFacts(IReadOnlyList<NifSkinPartitionView> partitions,
        NifModelGeometryData data)
    {
        var facts = new JsonObject { ["count"] = partitions.Count };
        if (partitions.Count <= NifModelNativeValues.MaximumInlineElements)
        {
            facts["partitions"] = new JsonArray(partitions.Select(p => (JsonNode?)p.ToJson()).ToArray());
        }
        else
        {
            facts["partitions"] = "see the NiSkinPartition block row";
        }

        var mapped = new int[data.VertexCount];
        var packedVertices = 0;
        foreach (var partition in partitions)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            packedVertices += partition.VertexCount;
            if (partition.VertexMap is not { } map)
            {
                continue;
            }

            foreach (var vertex in map)
            {
                mapped[vertex]++;
            }
        }

        facts["packedVertices"] = packedVertices;
        facts["verticesMapped"] = mapped.Count(count => count > 0);
        facts["verticesMappedMoreThanOnce"] = mapped.Count(count => count > 1);
        facts["verticesUnmapped"] = mapped.Count(count => count == 0);
        facts["triangles"] = new JsonObject
        {
            ["primitive"] = data.Triangulation!.Indices.Length / 3,
            ["rule"] = "the primitive's triangles are the partitions' triangles in partition order, offset by each " +
                       "partition's first primitive vertex (no comparison: they are the same by construction)"
        };
        facts["storedWeights"] = "none in the partitions; the influences come from the packed weight channel";
        return facts;
    }

    /// <summary>
    ///     Compares each partition vertex's nonzero (joint, weight) pairs with NiSkinData's for the shape vertex it maps
    ///     (a joint's weights summed): identical, or different with the largest absolute difference.
    /// </summary>
    private static JsonObject WeightComparison(IReadOnlyList<NifSkinPartitionView> partitions,
        NifSkinDataView skinData, int vertexCount)
    {
        var expected = new Dictionary<int, double>?[vertexCount];
        for (var b = 0; b < skinData.Bones.Count; b++)
        {
            foreach (var (vertex, weight) in skinData.Bones[b].Weights)
            {
                if (weight == 0f)
                {
                    continue;
                }

                var row = expected[vertex] ??= new Dictionary<int, double>();
                row[b] = row.GetValueOrDefault(b) + weight;
            }
        }

        int compared = 0, identical = 0;
        var maximum = 0.0;
        var actual = new Dictionary<int, double>();
        foreach (var partition in partitions)
        {
            if (partition.VertexMap is not { } map || partition.Weights is not { } weights ||
                partition.BoneIndices is not { } bones)
            {
                continue;
            }

            for (var local = 0; local < partition.VertexCount; local++)
            {
                actual.Clear();
                for (var k = 0; k < weights[local].Length; k++)
                {
                    if (weights[local][k] != 0f)
                    {
                        var joint = (int)partition.Bones[bones[local][k]];
                        actual[joint] = actual.GetValueOrDefault(joint) + weights[local][k];
                    }
                }

                var row = expected[map[local]] ?? new Dictionary<int, double>();
                var difference = 0.0;
                foreach (var joint in row.Keys.Union(actual.Keys))
                {
                    difference = Math.Max(difference,
                        Math.Abs(row.GetValueOrDefault(joint) - actual.GetValueOrDefault(joint)));
                }

                compared++;
                if (difference == 0.0)
                {
                    identical++;
                }

                maximum = Math.Max(maximum, difference);
            }
        }

        return new JsonObject
        {
            ["comparedPartitionVertices"] = compared,
            ["identical"] = identical,
            ["different"] = compared - identical,
            ["maximumAbsoluteDifference"] = NifModelNativeValues.Double(maximum),
            ["rule"] = "nonzero (joint, weight) pairs per shape vertex, a joint's weights summed in double"
        };
    }

    private NifModelShapeSkin NotTyped(NifDecodedBlock shape, NifSkinInstanceView instance, NifDecodedBlock dataBlock,
        NifDecodedBlock? partitionBlock, JsonObject facts, string reason, string detail)
    {
        var disposition = NifModelBlockDisposition.NativeOnly(reason);
        Mark(instance.BlockIndex, disposition);
        Mark(dataBlock.Index, disposition);
        if (partitionBlock is not null)
        {
            Mark(partitionBlock.Index, disposition);
        }

        facts["typed"] = false;
        facts["reason"] = reason;
        facts["detail"] = detail;
        _diagnostics.Add(NotTypedDiagnostic, string.Create(CultureInfo.InvariantCulture,
            $"Block {shape.Index} ({shape.Type}): its skin (block {instance.BlockIndex}, {instance.Type}) is kept as " +
            $"native state ({reason}): {detail} The geometry is placed unskinned, as stored in bind space."));
        return new NifModelShapeSkin(null, null, Array.Empty<SceneAttributeStream>(), facts);
    }

    private void ReportDismember(NifDecodedBlock shape, NifDecodedBlock instanceBlock, string reason)
    {
        _diagnostics.Add(DismemberDiagnostic, string.Create(CultureInfo.InvariantCulture,
            $"Block {shape.Index} ({shape.Type}): the body parts of block {instanceBlock.Index} " +
            $"({instanceBlock.Type}) are not typed as face streams ({reason}); they stay in native state."));
    }

    /// <summary>Records a decision for a block: Typed wins over NativeOnly, and the first NativeOnly reason is kept.</summary>
    private void Mark(int block, NifModelBlockDisposition disposition)
    {
        if (!_dispositions.TryGetValue(block, out var existing) || (!existing.IsTyped && disposition.IsTyped))
        {
            _dispositions[block] = disposition;
        }
    }

    private bool IsAncestorOrSelf(int ancestor, int node)
    {
        for (var current = node; current >= 0; current = _parentByNode[current])
        {
            if (current == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    private bool SharesOneRoot(IReadOnlyList<int> joints)
    {
        var first = -1;
        foreach (var joint in joints)
        {
            var root = joint;
            while (_parentByNode[root] >= 0)
            {
                root = _parentByNode[root];
            }

            if (first < 0)
            {
                first = root;
            }
            else if (root != first)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One occurrence's resolved palette.</summary>
    /// <param name="SkeletonRootBlock">The authored Skeleton Root link.</param>
    /// <param name="SkeletonRoot">The resolved skeleton root occurrence, or null.</param>
    /// <param name="RootResolution">How the skeleton root was (or was not) resolved.</param>
    /// <param name="Joints">Joint k's node occurrence, for bone k.</param>
    /// <param name="RootContainsJoints">True when there is no resolved root or it contains every joint.</param>
    private sealed record JointResolution(
        int SkeletonRootBlock,
        int? SkeletonRoot,
        string RootResolution,
        int[] Joints,
        bool RootContainsJoints);
}

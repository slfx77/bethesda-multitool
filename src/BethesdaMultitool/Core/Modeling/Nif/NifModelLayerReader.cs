using System.Globalization;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Types the runtime visibility choices and camera-facing state of placed nodes (plan section 3, "Nodes"; section 6,
///     slice 8): NiSwitchNode and NiLODNode children become exclusive <see cref="SceneLayerSet" /> groups, the
///     NiAVObject hidden flag becomes a default-off set, and NiBillboardNode occurrences get their
///     <see cref="SceneBillboard" /> (<see cref="NifModelBillboards" />).
/// </summary>
/// <remarks>
///     <para>
///         NiSwitchNode: one set per stored child ordinal, null children included (an empty set keeps the ordinal), id
///         <c>switch:{block}:{ordinal}</c>, members = the child's occurrence under every occurrence of the switch, label =
///         the child's name, default-on when the ordinal equals the stored Index (compared against the stored Children
///         array, never the list without nulls), exclusive group <c>switch:{block}</c>, source kind
///         <see cref="SwitchSourceKind" />. An Index at or beyond the child count leaves every set off and is reported.
///         The switch flags are native state.
///     </para>
///     <para>
///         NiLODNode: the same sets under <c>lod:{block}</c> and <see cref="LodSourceKind" />, with the finest level
///         default-on: the level whose NiRangeLODData Near Extent is smallest, ties to the lowest ordinal. When that
///         cannot be established (no LOD data, NiScreenLODData, a range count that differs from the child count, a
///         non-finite extent or a partial decode), the stored active Index is the default and a diagnostic says why. The
///         LOD data block is NativeOnly <see cref="NifModelCoverage.LodDataReason" />: its center and ranges have no
///         typed vocabulary and are kept in native state.
///     </para>
///     <para>
///         Hidden flag (NiAVObject Flags bit 0): one set <c>hidden:{block}</c> holding every occurrence of the block,
///         default-off, source kind <see cref="HiddenSourceKind" />. A visible node belongs to no hidden set.
///     </para>
///     <para>
///         NiBillboardNode: every occurrence of the block gets the encoding mapped from the stored Billboard Mode under
///         the read's <see cref="NifModelBillboardSource" /> (<see cref="NifModelBillboards" />, RE-25), and its own
///         <see cref="SceneBillboard.SourceScale" />: the signed product of the stored Scale fields along its chain from the
///         document root (<see cref="NifModelBillboards.SourceScale" />), so two occurrences of one block under different
///         ancestors differ in that member only. Effective modes 6 and 7 give no billboard. The native payload keeps the
///         stored <c>mode</c> and adds the effective mode, the update-controllers bit, the source key and both
///         provenances. A facing that is Assumed for the source key is reported once per block
///         (<see cref="BillboardDiagnostic" />), and so is a block with an occurrence whose product double cannot state
///         (<see cref="SourceScaleDiagnostic" />; that occurrence's scale is left unstated). After the animation stage,
///         <see cref="NifModelBillboardScaleSign" /> adds a <c>scaleSign</c> entry to the payload of a block with an
///         occurrence whose world scale is not positive, at rest (the value its SourceScale declares) or at a scale value
///         a clip states.
///     </para>
/// </remarks>
internal static class NifModelLayerReader
{
    /// <summary>The source kind of NiSwitchNode layer sets.</summary>
    public const string SwitchSourceKind = "bmt.nif.NiSwitchNode";

    /// <summary>The source kind of NiLODNode layer sets.</summary>
    public const string LodSourceKind = "bmt.nif.NiLODNode";

    /// <summary>The source kind of hidden-flag layer sets.</summary>
    public const string HiddenSourceKind = "bmt.nif.hidden";

    /// <summary>Diagnostic code for a switch whose stored Index names no child slot.</summary>
    public const string SwitchIndexDiagnostic = "bmt.nif.switch-index";

    /// <summary>Diagnostic code for a LOD node whose finest level could not be established from its data.</summary>
    public const string LodDefaultDiagnostic = "bmt.nif.lod-default";

    /// <summary>Diagnostic code for a billboard block whose facing rule is Assumed for the read's source key.</summary>
    public const string BillboardDiagnostic = "bmt.nif.billboard-assumed";

    /// <summary>
    ///     Diagnostic code for a billboard block with an occurrence whose product of stored Scale fields overflows or
    ///     underflows double, so its <see cref="SceneBillboard.SourceScale" /> is left unstated.
    /// </summary>
    public const string SourceScaleDiagnostic = "bmt.nif.billboard-source-scale";

    /// <summary>The rule recorded for a LOD node's default level.</summary>
    public const string FinestLevelRule =
        "default-on = the level whose NiRangeLODData Near Extent is smallest (ties: lowest ordinal)";

    /// <summary>Reads the layer sets and billboards of every placed block.</summary>
    /// <param name="state">The read state.</param>
    /// <param name="graph">The placed node graph.</param>
    /// <param name="billboardSource">The read's source key, which the billboard provenance and evidence follow.</param>
    /// <param name="cancellationToken">Observed per block and per member.</param>
    /// <exception cref="InvalidDataException">A placed switch, LOD or billboard node lacks a field the reader needs.</exception>
    /// <exception cref="NotSupportedException">The file needs more layer sets than a document allows.</exception>
    public static NifModelLayerResult Read(NifModelReadState state, NifModelNodeGraph graph,
        NifModelBillboardSource billboardSource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(billboardSource);
        var context = new Context(state, graph, billboardSource, cancellationToken);
        foreach (var block in graph.FactsByBlock.Keys.Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var facts = graph.FactsByBlock[block];
            var extra = new JsonObject();
            if (state.Schema.Inherits(facts.TypeName, "NiSwitchNode"))
            {
                AddSwitch(context, facts, extra);
            }

            if (state.Schema.Inherits(facts.TypeName, "NiBillboardNode"))
            {
                AddBillboards(context, facts, extra);
            }

            if (facts.IsHidden)
            {
                var id = string.Create(CultureInfo.InvariantCulture, $"hidden:{block}");
                context.Add(new SceneLayerSet(id, facts.Name, graph.OccurrencesByBlock[block], false, HiddenSourceKind,
                    cancellationToken: cancellationToken));
                extra["hiddenLayer"] = new JsonObject
                {
                    ["flagBit"] = 0,
                    ["layerSet"] = id,
                    ["defaultOn"] = false,
                    ["members"] = NifModelNativeValues.Integers(graph.OccurrencesByBlock[block])
                };
            }

            if (extra.Count > 0)
            {
                context.NodeFacts[block] = extra;
            }
        }

        return new NifModelLayerResult(context.Sets.AsReadOnly(), context.Billboards, context.NodeFacts,
            context.Dispositions, context.NodeByFedBlock, context.Diagnostics.ToList());
    }

    private static void AddSwitch(Context context, NifModelNodeFacts facts, JsonObject extra)
    {
        var block = context.State.Blocks[facts.BlockIndex];
        var isLod = context.State.Schema.Inherits(facts.TypeName, "NiLODNode");
        var flags = NifModelPropertyFields.Integer(block, block.Root, "Switch Node Flags");
        var index = NifModelPropertyFields.Integer(block, block.Root, "Index");
        var slots = facts.ChildSlotCount;
        var prefix = isLod ? "lod" : "switch";
        var node = new JsonObject
        {
            ["flags"] = JsonValue.Create(flags.RawBits),
            ["index"] = index.Value,
            ["childSlots"] = slots
        };

        int? defaultOrdinal;
        if (isLod)
        {
            defaultOrdinal = LodDefault(context, facts, block, index.Value, node);
        }
        else
        {
            defaultOrdinal = index.Value < slots ? (int)index.Value : null;
            node["rule"] = "default-on = the child at the stored Index, counted over the stored Children (nulls included)";
            if (defaultOrdinal is null)
            {
                context.Diagnostics.Add(SwitchIndexDiagnostic, string.Create(CultureInfo.InvariantCulture,
                    $"Block {facts.BlockIndex} ({facts.TypeName}): the stored Index {index.Value} names no child slot " +
                    $"(it has {slots}); no child is default-on."));
            }
        }

        node["defaultOnOrdinal"] = defaultOrdinal;
        var group = string.Create(CultureInfo.InvariantCulture, $"{prefix}:{facts.BlockIndex}");
        var positions = PositionsByOrdinal(facts);
        var occurrences = context.Graph.OccurrencesByBlock[facts.BlockIndex];
        var ids = new List<string>(slots);
        for (var ordinal = 0; ordinal < slots; ordinal++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var members = new List<int>();
            if (positions[ordinal] >= 0)
            {
                foreach (var occurrence in occurrences)
                {
                    members.Add(context.Graph.Nodes[occurrence].Children[positions[ordinal]]);
                }
            }

            var label = members.Count > 0 ? context.Graph.Nodes[members[0]].Name : "";
            var id = string.Create(CultureInfo.InvariantCulture, $"{group}:{ordinal}");
            ids.Add(id);
            context.Add(new SceneLayerSet(id, label, members, ordinal == defaultOrdinal,
                isLod ? LodSourceKind : SwitchSourceKind, group, cancellationToken: context.CancellationToken));
        }

        node["exclusiveGroup"] = group;
        node["layerSets"] = NifModelNativeValues.Texts(ids);
        extra[prefix] = node;
    }

    /// <summary>The finest level of a LOD node, or the fallback to its stored Index with a diagnostic.</summary>
    private static int? LodDefault(Context context, NifModelNodeFacts facts, NifDecodedBlock block, long index,
        JsonObject node)
    {
        var slots = facts.ChildSlotCount;
        var link = NifModelPropertyFields.Ref(block, block.Root, "LOD Level Data");
        node["dataBlock"] = link;
        if (link == -1)
        {
            return Fallback(context, facts, index, node, "it links no LOD data");
        }

        var data = context.State.Blocks[link];
        node["dataType"] = data.Type;
        context.MarkLodData(link, context.Graph.OccurrencesByBlock[facts.BlockIndex][0]);
        if (!context.State.Schema.Inherits(data.Type, "NiRangeLODData"))
        {
            return Fallback(context, facts, index, node,
                $"its LOD data is {data.Type}; only NiRangeLODData ranges establish the finest level");
        }

        if (!data.IsComplete || !data.Root.TryGet("LOD Levels", out var levelsValue) ||
            levelsValue is not NifArrayValue { IsRows: false } levels)
        {
            return Fallback(context, facts, index, node, "its NiRangeLODData did not decode exactly");
        }

        if (data.Root.TryGet("LOD Center", out var centerValue))
        {
            node["center"] = NifModelNativeValues.ToJson(centerValue);
        }

        var nears = new float[levels.Count];
        var ranges = new List<JsonNode?>();
        for (var i = 0; i < levels.Count; i++)
        {
            if (levels.Items[i] is not NifStructValue range ||
                !range.TryGet("Near Extent", out var nearValue) || nearValue is not NifFloatValue near ||
                !range.TryGet("Far Extent", out var farValue) || farValue is not NifFloatValue far)
            {
                return Fallback(context, facts, index, node, "its NiRangeLODData did not decode exactly");
            }

            nears[i] = near.Value;
            if (ranges.Count < NifModelNativeValues.MaximumInlineElements)
            {
                ranges.Add(new JsonArray(NifModelNativeValues.Float(near.Value), NifModelNativeValues.Float(far.Value)));
            }
        }

        node["ranges"] = levels.Count <= NifModelNativeValues.MaximumInlineElements
            ? new JsonArray(ranges.ToArray())
            : NifModelNativeValues.ToJson(levels);
        if (levels.Count != slots)
        {
            return Fallback(context, facts, index, node, string.Create(CultureInfo.InvariantCulture,
                $"its NiRangeLODData stores {levels.Count} range(s) for {slots} child slot(s)"));
        }

        var finest = -1;
        for (var i = 0; i < nears.Length; i++)
        {
            if (!float.IsFinite(nears[i]))
            {
                return Fallback(context, facts, index, node, string.Create(CultureInfo.InvariantCulture,
                    $"range {i} has a non-finite Near Extent"));
            }

            if (finest < 0 || nears[i] < nears[finest])
            {
                finest = i;
            }
        }

        if (finest < 0)
        {
            return Fallback(context, facts, index, node, "it has no child slot");
        }

        node["rule"] = FinestLevelRule;
        return finest;
    }

    private static int? Fallback(Context context, NifModelNodeFacts facts, long index, JsonObject node,
        string reason)
    {
        var slots = facts.ChildSlotCount;
        int? ordinal = index >= 0 && index < slots ? (int)index : null;
        node["rule"] = "fallback: default-on = the child at the stored Index (" + reason + ")";
        var outcome = ordinal is null
            ? string.Create(CultureInfo.InvariantCulture,
                $"the stored Index {index} names no child slot either, so no level is default-on.")
            : string.Create(CultureInfo.InvariantCulture, $"the stored active Index {index} is the default-on level.");
        context.Diagnostics.Add(LodDefaultDiagnostic, string.Create(CultureInfo.InvariantCulture,
            $"Block {facts.BlockIndex} ({facts.TypeName}): the finest level could not be established ({reason}); " +
            $"{outcome}"));
        return ordinal;
    }

    /// <summary>
    ///     Types one NiBillboardNode block: for every occurrence the block's encoding with the occurrence's own source
    ///     scale (no billboard for effective modes 6 and 7), the native payload, one diagnostic when the facing is Assumed
    ///     for the source key and one when an occurrence's source scale is left unstated.
    /// </summary>
    private static void AddBillboards(Context context, NifModelNodeFacts facts, JsonObject extra)
    {
        var block = context.State.Blocks[facts.BlockIndex];
        var mode = NifModelPropertyFields.Integer(block, block.Root, "Billboard Mode").RawBits;
        var effective = NifModelBillboards.EffectiveMode(mode);
        var billboard = NifModelBillboards.Map(mode, context.BillboardSource);
        var occurrences = context.Graph.OccurrencesByBlock[facts.BlockIndex];
        if (billboard is not null)
        {
            var unstated = new List<int>();
            foreach (var occurrence in occurrences)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var sourceScale = NifModelBillboards.SourceScale(context.Chain(occurrence));
                if (sourceScale is null)
                {
                    unstated.Add(occurrence);
                }

                context.Billboards[occurrence] = NifModelBillboards.Map(mode, context.BillboardSource, sourceScale)!;
            }

            if (unstated.Count > 0)
            {
                context.Diagnostics.Add(SourceScaleDiagnostic, string.Format(CultureInfo.InvariantCulture,
                    "Block {0} ({1}): {2} of {3} occurrence(s), first node {4}, have a product of the stored Scale " +
                    "fields (root to node) that overflows or underflows double although no factor is zero; their " +
                    "SourceScale is left unstated (null, which is not a scale of one).",
                    facts.BlockIndex, facts.TypeName, unstated.Count, occurrences.Count, unstated[0]));
            }
        }

        var node = new JsonObject
        {
            ["mode"] = JsonValue.Create(mode),
            ["modeName"] = NifModelBillboards.ModeName(mode),
            ["effectiveMode"] = effective,
            ["effectiveModeName"] = NifModelBillboards.ModeName((ulong)effective),
            ["updateControllersBit"] = (mode & NifModelBillboards.UpdateControllersBit) != 0,
            ["occurrences"] = occurrences.Count,
            ["sourceKey"] = context.BillboardSource.Description,
            ["pivotAndAnchor"] = NifModelBillboards.PivotRule
        };
        if (billboard is null)
        {
            node["facing"] = NifModelBillboards.NoFacingRule;
            extra["billboard"] = node;
            return;
        }

        node["aim"] = billboard.Aim?.ToString();
        node["rigid"] = billboard.Rigid is { } rigid ? JsonValue.Create(rigid) : null;
        node["roll"] = billboard.Roll?.ToString();
        node["lockedAxisFrame"] = billboard.LockedAxisFrame?.ToString();
        node["facingProvenance"] = billboard.FacingProvenance.ToString();
        node["scheduleProvenance"] = billboard.ScheduleProvenance.ToString();
        if (context.BillboardSource.AssumedReason is { } reason)
        {
            context.Diagnostics.Add(BillboardDiagnostic, string.Create(CultureInfo.InvariantCulture,
                $"Block {facts.BlockIndex} ({facts.TypeName}): the facing of Billboard Mode {mode} (effective mode " +
                $"{effective}) is Assumed for this read because {reason}; the FNV/Fallout 3 encoding is kept."));
        }

        extra["billboard"] = node;
    }

    /// <summary>For each stored child ordinal, its position in the node's non-null child list, or -1 for a null child.</summary>
    private static int[] PositionsByOrdinal(NifModelNodeFacts facts)
    {
        var nulls = new HashSet<int>(facts.NullChildOrdinals);
        var positions = new int[facts.ChildSlotCount];
        var position = 0;
        for (var ordinal = 0; ordinal < positions.Length; ordinal++)
        {
            positions[ordinal] = nulls.Contains(ordinal) ? -1 : position++;
        }

        return positions;
    }

    /// <summary>The mutable state of one layer read.</summary>
    private sealed class Context
    {
        private int[]? _blockByNode;
        private long _members;
        private int[]? _parentByNode;

        public Context(NifModelReadState state, NifModelNodeGraph graph, NifModelBillboardSource billboardSource,
            CancellationToken cancellationToken)
        {
            State = state;
            Graph = graph;
            BillboardSource = billboardSource;
            CancellationToken = cancellationToken;
        }

        public NifModelReadState State { get; }

        public NifModelNodeGraph Graph { get; }

        public NifModelBillboardSource BillboardSource { get; }

        public CancellationToken CancellationToken { get; }

        public List<SceneLayerSet> Sets { get; } = [];

        public Dictionary<int, SceneBillboard> Billboards { get; } = [];

        public Dictionary<int, JsonObject> NodeFacts { get; } = [];

        public Dictionary<int, NifModelBlockDisposition> Dispositions { get; } = [];

        public Dictionary<int, int> NodeByFedBlock { get; } = [];

        public NifModelDiagnosticSink Diagnostics { get; } = new();

        /// <summary>Adds a set within the document's layer-set row and member budgets.</summary>
        public void Add(SceneLayerSet set)
        {
            _members += set.Members.Count;
            if (Sets.Count >= ModelDocument.MaximumLayerSets || _members > ModelDocument.MaximumLayerSetMembers)
            {
                throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                    $"The NIF's switch, LOD and hidden nodes need more than the {ModelDocument.MaximumLayerSets} layer " +
                    $"sets or {ModelDocument.MaximumLayerSetMembers} members a document allows."));
            }

            Sets.Add(set);
        }

        /// <summary>
        ///     The chain of one occurrence, root first: from the document root down to and including the occurrence, each
        ///     node's block index and that block's stored Scale.
        /// </summary>
        /// <exception cref="InvalidOperationException">A node on the chain is no block's occurrence (a graph invariant).</exception>
        public List<(int Block, float Scale)> Chain(int node)
        {
            var (parents, blocks) = Ancestry();
            var chain = new List<(int Block, float Scale)>();
            for (var current = node; current >= 0; current = parents[current])
            {
                CancellationToken.ThrowIfCancellationRequested();
                var block = blocks[current];
                if (block < 0)
                {
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                        $"Node {current} on the chain of node {node} is no block's occurrence."));
                }

                chain.Add((block, Graph.FactsByBlock[block].Scale));
            }

            chain.Reverse();
            return chain;
        }

        /// <summary>A LOD data block is always NativeOnly; the first LOD node occurrence it serves is its row target.</summary>
        public void MarkLodData(int block, int node)
        {
            Dispositions.TryAdd(block, NifModelBlockDisposition.NativeOnly(NifModelCoverage.LodDataReason));
            NodeByFedBlock.TryAdd(block, node);
        }

        /// <summary>Each node's parent (-1 for a root) and block (-1 for none), built once on the first chain.</summary>
        private (int[] Parents, int[] Blocks) Ancestry()
        {
            if (_parentByNode is { } knownParents && _blockByNode is { } knownBlocks)
            {
                return (knownParents, knownBlocks);
            }

            var parents = new int[Graph.Nodes.Count];
            Array.Fill(parents, -1);
            for (var node = 0; node < Graph.Nodes.Count; node++)
            {
                CancellationToken.ThrowIfCancellationRequested();
                foreach (var child in Graph.Nodes[node].Children)
                {
                    parents[child] = node;
                }
            }

            var blocks = new int[Graph.Nodes.Count];
            Array.Fill(blocks, -1);
            for (var block = 0; block < Graph.OccurrencesByBlock.Count; block++)
            {
                foreach (var node in Graph.OccurrencesByBlock[block])
                {
                    blocks[node] = block;
                }
            }

            _parentByNode = parents;
            _blockByNode = blocks;
            return (parents, blocks);
        }
    }
}

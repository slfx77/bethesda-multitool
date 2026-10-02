using System.Globalization;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using Slfx77.Multitool.Core.Documents;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Builds the document's node hierarchy from the NIF footer roots (plan section 3, "Nodes"). Every NiAVObject reached
///     through a root or a NiNode Children link becomes one <see cref="SceneNode" /> per occurrence: a block reached
///     through two parent edges becomes two nodes (instancing), because Shared allows one parent per node. Children keep
///     their stored order; null child entries are dropped from the typed list and their ordinals kept in native state.
///     A child that is already an ancestor on the current path is a cycle and throws.
/// </summary>
/// <remarks>
///     Names are the Latin-1 text of the header string-table entry (NifParser's ASCII decoding is lossy); the raw bytes
///     stay available through <see cref="NifModelNodeFacts.NameValue" /> for native state. When a node's own name is
///     null, unresolved or empty and an NiDefaultAVObjectPalette names the block (<see cref="NifModelPaletteNames" />),
///     that name is the display name and the name source says so; otherwise the node's own name stays authoritative.
///     Transforms follow <see cref="NifModelTransform" />. Roles: Transform for NiNode subclasses and geometry
///     placements, Helper for particles, cameras, lights and any other NiAVObject. After the sub-readers have run,
///     <see cref="WithPlacements" /> gives occurrences their mesh and skin indices, the Joint role for skin bones and
///     their billboards; switch, LOD and hidden layer sets are document-level (<see cref="NifModelLayerReader" />), and
///     the hidden flag also stays in native state. Only the hierarchy reachable from the footer roots is walked, so a
///     cycle among unreachable blocks is not reported (those blocks feed nothing and are NativeOnly).
/// </remarks>
internal static class NifModelNodeReader
{
    /// <summary>The most node occurrences instancing may expand one file into.</summary>
    public const int MaximumNodeOccurrences = 1 << 18;

    /// <summary>Diagnostic code for a footer root whose stored transform is not the identity.</summary>
    public const string RootTransformDiagnostic = "bmt.nif.root-transform";

    private const string AvObjectType = "NiAVObject";
    private const string NodeType = "NiNode";

    /// <summary>
    ///     Matrix33's field names in file order (nif.xml:5932-5980). Position k is row-major element k: "m21" is
    ///     documented as member (1,2), so the names read transposed but the storage is row-major. Shared with the
    ///     NiTransform reader (<see cref="NifSkinTransformView" />).
    /// </summary>
    internal static readonly IReadOnlyList<string> Matrix33Fields =
        Array.AsReadOnly(new[] { "m11", "m21", "m31", "m12", "m22", "m32", "m13", "m23", "m33" });

    /// <summary>Walks the hierarchy from the footer roots.</summary>
    /// <param name="state">The read state.</param>
    /// <param name="palette">The palette names that may supply display names for unnamed nodes.</param>
    /// <param name="cancellationToken">Observed per occurrence.</param>
    /// <exception cref="InvalidDataException">
    ///     A cycle, a placed block that is not an NiAVObject or did not decode through its transform, a non-finite
    ///     transform value, or more than <see cref="MaximumNodeOccurrences" /> occurrences.
    /// </exception>
    public static NifModelNodeGraph Read(NifModelReadState state, NifModelPaletteNames palette,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(palette);
        var blockCount = state.Blocks.Count;
        var walk = new Walk(state, palette, blockCount);
        var roots = new List<int>(state.Footer.Roots.Count);

        foreach (var root in state.Footer.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            roots.Add(walk.Enter(root));
            while (walk.Stack.TryPeek(out var frame))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (frame.Cursor < frame.Facts.ChildBlocks.Count)
                {
                    var child = frame.Facts.ChildBlocks[frame.Cursor];
                    frame.Cursor++;
                    if (walk.OnPath[child])
                    {
                        throw new InvalidDataException(
                            $"NIF block {frame.Facts.BlockIndex} ({frame.Facts.TypeName}) lists block {child} " +
                            $"({state.Blocks[child].Type}) as a child, but block {child} is already its ancestor: " +
                            "the scene graph has a cycle.");
                    }

                    frame.ChildNodes.Add(walk.Enter(child));
                    continue;
                }

                walk.Stack.Pop();
                walk.OnPath[frame.Facts.BlockIndex] = false;
                walk.Nodes[frame.NodeIndex] = CreateNode(frame.Facts, frame.ChildNodes);
            }
        }

        var nodes = new SceneNode[walk.Nodes.Count];
        for (var i = 0; i < nodes.Length; i++)
        {
            nodes[i] = walk.Nodes[i] ?? throw new InvalidOperationException($"Node occurrence {i} was never completed.");
        }

        var occurrences = new IReadOnlyList<int>[blockCount];
        for (var i = 0; i < blockCount; i++)
        {
            occurrences[i] = walk.Occurrences[i] is { } list ? list.AsReadOnly() : Array.Empty<int>();
        }

        return new NifModelNodeGraph(nodes, roots.AsReadOnly(), occurrences, walk.Facts,
            Diagnostics(state, walk.Facts));
    }

    /// <summary>
    ///     Applies what the sub-readers placed on occurrences (slices 3, 5, 7 and 8): mesh indices (a geometry block's
    ///     occurrences usually share one mesh, but placements with different effective materials place different
    ///     meshes), skin indices, the Joint role for skin bones, and billboards. Nodes are immutable, so each touched
    ///     occurrence is rebuilt by the same <see cref="CreateNode" /> the walk used, keeping its children, transform and
    ///     name.
    /// </summary>
    /// <param name="graph">The walked hierarchy.</param>
    /// <param name="placements">The per-occurrence placements.</param>
    /// <returns>The graph with the placements applied, or <paramref name="graph" /> itself when there are none.</returns>
    public static NifModelNodeGraph WithPlacements(NifModelNodeGraph graph, NifModelNodePlacements placements)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(placements);
        var touched = placements.TouchedNodes.ToList();
        if (touched.Count == 0)
        {
            return graph;
        }

        var blockByNode = new int[graph.Nodes.Count];
        for (var block = 0; block < graph.OccurrencesByBlock.Count; block++)
        {
            foreach (var nodeIndex in graph.OccurrencesByBlock[block])
            {
                blockByNode[nodeIndex] = block;
            }
        }

        var nodes = graph.Nodes.ToArray();
        foreach (var nodeIndex in touched)
        {
            var facts = graph.FactsByBlock[blockByNode[nodeIndex]];
            int? mesh = placements.MeshByNode.TryGetValue(nodeIndex, out var meshIndex) ? meshIndex : null;
            int? skin = placements.SkinByNode.TryGetValue(nodeIndex, out var skinIndex) ? skinIndex : null;
            SceneNodeRole? role = placements.JointNodes.Contains(nodeIndex) ? SceneNodeRole.Joint : null;
            var billboard = placements.BillboardByNode.GetValueOrDefault(nodeIndex);
            nodes[nodeIndex] = CreateNode(facts, nodes[nodeIndex].Children, mesh, skin, role, billboard);
        }

        return new NifModelNodeGraph(nodes, graph.RootNodeIndices, graph.OccurrencesByBlock, graph.FactsByBlock,
            graph.Diagnostics);
    }

    private static SceneNode CreateNode(NifModelNodeFacts facts, IEnumerable<int> children, int? meshIndex = null,
        int? skinIndex = null, SceneNodeRole? role = null, SceneBillboard? billboard = null)
    {
        var nodeRole = role ?? facts.Role;
        return facts.Transform.Trs is { } trs
            ? new SceneNode(facts.Name, trs, children, meshIndex, skinIndex: skinIndex)
            {
                Role = nodeRole,
                Billboard = billboard
            }
            : new SceneNode(facts.Name, facts.Transform.Matrix, children, meshIndex, skinIndex: skinIndex)
            {
                Role = nodeRole,
                Billboard = billboard
            };
    }

    private static List<SceneDiagnostic> Diagnostics(NifModelReadState state,
        Dictionary<int, NifModelNodeFacts> facts)
    {
        var diagnostics = new List<SceneDiagnostic>();
        var reported = new HashSet<int>();
        foreach (var root in state.Footer.Roots)
        {
            var rootFacts = facts[root];
            if (!reported.Add(root) || rootFacts.IsIdentityTransform)
            {
                continue;
            }

            var measured = string.Create(CultureInfo.InvariantCulture,
                $"Root block {root} ({rootFacts.TypeName}) has a non-identity transform (translation " +
                $"{Format(rootFacts.Translation)}, scale {rootFacts.Scale:R}, kind {rootFacts.Transform.Kind}); ");
            diagnostics.Add(new SceneDiagnostic(RootTransformDiagnostic, DocumentText.Raw(measured +
                "it is kept as authored. FO3-era and later placement replaces the root transform in game.")));
        }

        return diagnostics;
    }

    private static string Format(Vector3 value)
    {
        return string.Create(CultureInfo.InvariantCulture, $"({value.X:R}, {value.Y:R}, {value.Z:R})");
    }

    private static NifModelNodeFacts ReadFacts(NifModelReadState state, NifModelPaletteNames palette, int blockIndex)
    {
        var block = state.Blocks[blockIndex];
        var type = block.Type;
        if (!state.Schema.Inherits(type, AvObjectType))
        {
            throw new InvalidDataException(
                $"NIF block {blockIndex} ({type}) is placed in the scene graph but is not an NiAVObject.");
        }

        var root = block.Root;
        if (!root.TryGet("Name", out var nameValue) || !root.TryGet("Flags", out var flagsValue) ||
            !root.TryGet("Translation", out var translationValue) || !root.TryGet("Rotation", out var rotationValue) ||
            !root.TryGet("Scale", out var scaleValue))
        {
            var detail = block.Failure is { } failure ? ": " + failure.Message : ".";
            throw new InvalidDataException(
                $"NIF block {blockIndex} ({type}) did not decode through its NiAVObject transform{detail}");
        }

        if (nameValue is not NifStringValue name || flagsValue is not NifIntegerValue flags ||
            translationValue is not NifStructValue translationStruct ||
            rotationValue is not NifStructValue rotationStruct || scaleValue is not NifFloatValue scaleFloat)
        {
            throw new InvalidDataException(
                $"NIF block {blockIndex} ({type}) decoded its NiObjectNET/NiAVObject fields with unexpected shapes.");
        }

        var translation = new Vector3(
            RequireFinite(block, translationStruct, "Translation", "x"),
            RequireFinite(block, translationStruct, "Translation", "y"),
            RequireFinite(block, translationStruct, "Translation", "z"));
        var rotation = new float[9];
        for (var i = 0; i < rotation.Length; i++)
        {
            rotation[i] = RequireFinite(block, rotationStruct, "Rotation", Matrix33Fields[i]);
        }

        var scale = scaleFloat.Value;
        if (!float.IsFinite(scale))
        {
            throw NonFinite(block, "Scale", scale);
        }

        var transform = NifModelTransform.Resolve(translation, rotation, scale);
        if (!SceneAffineTransform.IsFiniteAffine(transform.Matrix))
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"NIF block {blockIndex} ({type}): its scale {scale:R} times its rotation overflows Float32 " +
                $"(Scale at offset 0x{SpanOffset(block, "Scale"):X})."));
        }

        var (childBlocks, nullOrdinals, slots) = ReadChildren(state, block);
        string text;
        string source;
        if (name.IsNone)
        {
            (text, source) = ("", "none");
        }
        else if (name.Text is { } resolved)
        {
            (text, source) = (resolved, "stringTable");
        }
        else
        {
            (text, source) = ("", "unresolved");
        }

        NifModelPaletteEntry? paletteEntry = null;
        if (palette.TryGet(blockIndex, out var entry))
        {
            paletteEntry = entry;
            if (text.Length == 0)
            {
                (text, source) = (entry.Name, NifModelPaletteNames.PaletteNameSource);
            }
        }

        return new NifModelNodeFacts(blockIndex, type, RoleFor(state, type), name, text, source, flags, translation,
            rotation, scale, transform, childBlocks, nullOrdinals, slots, paletteEntry);
    }

    private static (IReadOnlyList<int> Children, IReadOnlyList<int> NullOrdinals, int Slots) ReadChildren(
        NifModelReadState state, NifDecodedBlock block)
    {
        if (!state.Schema.Inherits(block.Type, NodeType) || !block.Root.TryGet("Children", out var value))
        {
            return (Array.Empty<int>(), Array.Empty<int>(), 0);
        }

        if (value is not NifArrayValue array)
        {
            throw new InvalidDataException(
                $"NIF block {block.Index} ({block.Type}) decoded Children as {value.Kind}, not an array.");
        }

        var children = new List<int>(array.Count);
        var nulls = new List<int>();
        for (var ordinal = 0; ordinal < array.Count; ordinal++)
        {
            if (array.Items[ordinal] is not NifRefValue reference)
            {
                throw new InvalidDataException(
                    $"NIF block {block.Index} ({block.Type}) Children[{ordinal}] is not a block reference.");
            }

            if (reference.IsNone)
            {
                nulls.Add(ordinal);
                continue;
            }

            var target = reference.Index;
            if ((uint)target >= (uint)state.Blocks.Count ||
                !state.Schema.Inherits(state.Blocks[target].Type, AvObjectType))
            {
                throw new InvalidDataException(
                    $"NIF block {block.Index} ({block.Type}) Children[{ordinal}] = {target} is not an NiAVObject block.");
            }

            children.Add(target);
        }

        return (children.AsReadOnly(), nulls.AsReadOnly(), array.Count);
    }

    private static SceneNodeRole RoleFor(NifModelReadState state, string type)
    {
        var schema = state.Schema;
        if (schema.Inherits(type, NodeType))
        {
            return SceneNodeRole.Transform;
        }

        if (schema.Inherits(type, "NiParticles"))
        {
            return SceneNodeRole.Helper;
        }

        return schema.Inherits(type, "NiGeometry") ? SceneNodeRole.Transform : SceneNodeRole.Helper;
    }

    private static float RequireFinite(NifDecodedBlock block, NifStructValue owner, string field, string component)
    {
        if (!owner.TryGet(component, out var value) || value is not NifFloatValue single)
        {
            throw new InvalidDataException(
                $"NIF block {block.Index} ({block.Type}) has no float '{field}.{component}'.");
        }

        return float.IsFinite(single.Value) ? single.Value : throw NonFinite(block, field + "." + component, single.Value);
    }

    private static InvalidDataException NonFinite(NifDecodedBlock block, string path, float value)
    {
        return new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
            $"NIF block {block.Index} ({block.Type}), field '{path}' at offset 0x{SpanOffset(block, path):X}: " +
            $"the transform value {value:R} (0x{BitConverter.SingleToUInt32Bits(value):X8}) is not finite."));
    }

    private static int SpanOffset(NifDecodedBlock block, string path)
    {
        foreach (var span in block.Spans)
        {
            if (string.Equals(span.Path, path, StringComparison.Ordinal))
            {
                return span.Offset;
            }
        }

        return block.Offset;
    }

    /// <summary>The mutable state of one hierarchy walk.</summary>
    private sealed class Walk
    {
        private readonly NifModelPaletteNames _palette;
        private readonly NifModelReadState _state;

        public Walk(NifModelReadState state, NifModelPaletteNames palette, int blockCount)
        {
            _state = state;
            _palette = palette;
            OnPath = new bool[blockCount];
            Occurrences = new List<int>?[blockCount];
        }

        public Dictionary<int, NifModelNodeFacts> Facts { get; } = [];

        public List<SceneNode?> Nodes { get; } = [];

        public bool[] OnPath { get; }

        public List<int>?[] Occurrences { get; }

        public Stack<Frame> Stack { get; } = new();

        /// <summary>Starts a new occurrence of <paramref name="block" /> and returns its pre-order node index.</summary>
        public int Enter(int block)
        {
            if (Nodes.Count >= MaximumNodeOccurrences)
            {
                throw new InvalidDataException(
                    $"The NIF scene graph expands to more than {MaximumNodeOccurrences} node occurrences through " +
                    "instancing, beyond the reader's bound.");
            }

            if (!Facts.TryGetValue(block, out var facts))
            {
                facts = ReadFacts(_state, _palette, block);
                Facts.Add(block, facts);
            }

            var nodeIndex = Nodes.Count;
            Nodes.Add(null);
            var list = Occurrences[block];
            if (list is null)
            {
                list = [];
                Occurrences[block] = list;
            }

            list.Add(nodeIndex);
            OnPath[block] = true;
            Stack.Push(new Frame(facts, nodeIndex));
            return nodeIndex;
        }
    }

    /// <summary>One occurrence being walked: its facts, node index, next child and completed child node indices.</summary>
    private sealed class Frame
    {
        public Frame(NifModelNodeFacts facts, int nodeIndex)
        {
            Facts = facts;
            NodeIndex = nodeIndex;
        }

        public NifModelNodeFacts Facts { get; }

        public int NodeIndex { get; }

        public int Cursor { get; set; }

        public List<int> ChildNodes { get; } = [];
    }
}

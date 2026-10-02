using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 3: the name-to-block map animation targets bind through (plan section 1.7, owner ruling D12). Names are
///     compared as EXACT stored bytes, case-sensitive: case-insensitive matching rescues no retail name and would bind
///     'Bip01 L Hand' to a node named 'bip01 l hand'.
/// </summary>
/// <remarks>
///     <para>
///         A <c>.nif</c> (<see cref="ForFile" />) binds in the order plan section 1.7 gives: (1) the NiControllerManager's
///         NiDefaultAVObjectPalette, name to block (the engine binds this way; X360 files store null node names and
///         resolve only here); (2) otherwise the placed nodes' own NiObjectNET Names, the header string-table bytes; (3)
///         one track per occurrence of the bound block (<see cref="NifModelTargetMatch.Occurrences" />). A <c>.kf</c>
///         (<see cref="ForSkeleton" />) binds within its resolved skeleton's node names only.
///     </para>
///     <para>
///         The 1a palette index (<see cref="NifModelPaletteNames" />) maps block to name for display names and cannot be
///         extended without changing it, so this type reads the palette again in the other direction. A name the tier
///         holding it gives to two blocks is 'ambiguous target'; a palette that names one block twice with one name is
///         not. One block placed several times (instancing) is one block with several occurrences, not an ambiguity.
///     </para>
///     <para>
///         Keys are the Latin-1 text of the stored bytes. Latin-1 maps each byte to exactly one character (U+0000 to
///         U+00FF), so ordinal comparison of the keys is byte comparison.
///     </para>
/// </remarks>
internal sealed class NifModelTargetNames
{
    /// <summary>The reason for <see cref="NifModelTargetBlock.NoTargetName" />.</summary>
    public const string NoTargetNameReason = "controlled block names no target";

    /// <summary>The reason for <see cref="NifModelTargetBlock.UnresolvedTargetName" />.</summary>
    public const string UnresolvedTargetNameReason = "target name index outside the string table";

    /// <summary>The reason for <see cref="NifModelTargetBlock.AmbiguousTarget" /> (D12).</summary>
    public const string AmbiguousTargetReason = "ambiguous target";

    /// <summary>The reason for <see cref="NifModelTargetBlock.TargetNotInSkeleton" /> (D3).</summary>
    public const string TargetNotInSkeletonReason = "target not in the resolved skeleton (attachment node)";

    /// <summary>The reason for <see cref="NifModelTargetBlock.TargetNotInFile" />.</summary>
    public const string TargetNotInFileReason = "target not in the file";

    /// <summary>The reason for <see cref="NifModelTargetBlock.TargetNotPlaced" />.</summary>
    public const string TargetNotPlacedReason = "target names no placed node";

    /// <summary>The reason for <see cref="NifModelTargetBlock.PaletteUnreadable" />.</summary>
    public const string PaletteUnreadableReason = "the manager's object palette did not decode";

    private const string ManagerType = "NiControllerManager";
    private const string PaletteType = "NiDefaultAVObjectPalette";

    private readonly Dictionary<string, List<int>> _palette;
    private readonly Dictionary<string, List<int>> _objects;
    private readonly IReadOnlyList<IReadOnlyList<int>> _occurrencesByBlock;

    private NifModelTargetNames(
        NifModelTargetScope scope,
        Dictionary<string, List<int>> palette,
        Dictionary<string, List<int>> objects,
        IReadOnlyList<IReadOnlyList<int>> occurrencesByBlock,
        bool paletteUnreadable)
    {
        Scope = scope;
        _palette = palette;
        _objects = objects;
        _occurrencesByBlock = occurrencesByBlock;
        PaletteUnreadable = paletteUnreadable;
    }

    /// <summary>What the map binds names within.</summary>
    public NifModelTargetScope Scope { get; }

    /// <summary>True when the manager's palette could not be read, so every lookup is blocked.</summary>
    public bool PaletteUnreadable { get; }

    /// <summary>The number of distinct palette names.</summary>
    public int PaletteNameCount => _palette.Count;

    /// <summary>The number of distinct node names.</summary>
    public int ObjectNameCount => _objects.Count;

    /// <summary>Builds a map from explicit names (the pure core the two factories share).</summary>
    /// <param name="scope">What the map binds names within.</param>
    /// <param name="paletteNames">The first tier (empty for a skeleton or a <c>.nif</c> without a palette).</param>
    /// <param name="objectNames">The second tier: placed nodes' own names.</param>
    /// <param name="occurrencesByBlock">For every block, its document node indices (empty when it produced none).</param>
    /// <param name="paletteUnreadable">True when a palette exists but could not be read (every lookup is then blocked).</param>
    /// <returns>The map.</returns>
    public static NifModelTargetNames Build(
        NifModelTargetScope scope,
        IEnumerable<NifModelTargetName> paletteNames,
        IEnumerable<NifModelTargetName> objectNames,
        IReadOnlyList<IReadOnlyList<int>> occurrencesByBlock,
        bool paletteUnreadable = false)
    {
        ArgumentNullException.ThrowIfNull(paletteNames);
        ArgumentNullException.ThrowIfNull(objectNames);
        ArgumentNullException.ThrowIfNull(occurrencesByBlock);
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown target scope.");
        }

        return new NifModelTargetNames(scope, Index(paletteNames), Index(objectNames), occurrencesByBlock,
            paletteUnreadable);
    }

    /// <summary>
    ///     The map for a <c>.kf</c>'s resolved skeleton: every placed node whose NiObjectNET Name resolves to a header
    ///     string, by its exact bytes. A NULL or unresolved name contributes nothing.
    /// </summary>
    /// <param name="graph">The skeleton's node graph, read by the 1a node reader.</param>
    /// <returns>The map, scoped to <see cref="NifModelTargetScope.Skeleton" />.</returns>
    public static NifModelTargetNames ForSkeleton(NifModelNodeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return Build(NifModelTargetScope.Skeleton, [], ObjectNames(graph), graph.OccurrencesByBlock);
    }

    /// <summary>
    ///     The map for targets inside a <c>.nif</c> driven by one NiControllerManager: its Object Palette first, then the
    ///     placed nodes' own names.
    /// </summary>
    /// <param name="state">The read state.</param>
    /// <param name="managerBlock">The NiControllerManager block whose sequences bind.</param>
    /// <param name="graph">The file's node graph.</param>
    /// <returns>
    ///     The map, scoped to <see cref="NifModelTargetScope.File" />. When the manager names a palette that did not decode
    ///     completely (or its own palette ref did not decode), every lookup is <see cref="NifModelTargetBlock.PaletteUnreadable" />.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">The manager block index is outside the file.</exception>
    /// <exception cref="ArgumentException">The block is not an NiControllerManager.</exception>
    public static NifModelTargetNames ForFile(NifModelReadState state, int managerBlock, NifModelNodeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentOutOfRangeException.ThrowIfNegative(managerBlock);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(managerBlock, state.Blocks.Count);
        var manager = state.Blocks[managerBlock];
        if (!state.Schema.Inherits(manager.Type, ManagerType))
        {
            throw new ArgumentException(
                $"Block {managerBlock} ({manager.Type}) is not an {ManagerType}.", nameof(managerBlock));
        }

        List<NifModelTargetName> palette = [];
        var unreadable = false;
        if (!manager.Root.TryGet("Object Palette", out var value) || value is not NifRefValue reference)
        {
            unreadable = true;
        }
        else if (!reference.IsNone && !TryReadPaletteNames(state, reference.Index, out palette))
        {
            unreadable = true;
        }

        return Build(NifModelTargetScope.File, palette, ObjectNames(graph), graph.OccurrencesByBlock, unreadable);
    }

    /// <summary>Binds one name by its exact bytes.</summary>
    /// <param name="name">The stored name bytes.</param>
    /// <returns>The bound block and its occurrences, or the typed reason it does not bind.</returns>
    public NifModelTargetMatch Match(ReadOnlySpan<byte> name)
    {
        var raw = name.ToArray();
        if (PaletteUnreadable)
        {
            return Blocked(raw, NifModelTargetBlock.PaletteUnreadable);
        }

        var key = Encoding.Latin1.GetString(name);
        if (_palette.TryGetValue(key, out var paletteBlocks))
        {
            return Bind(raw, paletteBlocks, NifModelTargetSource.Palette);
        }

        if (_objects.TryGetValue(key, out var objectBlocks))
        {
            return Bind(raw, objectBlocks, NifModelTargetSource.ObjectName);
        }

        return Blocked(raw, Scope == NifModelTargetScope.Skeleton
            ? NifModelTargetBlock.TargetNotInSkeleton
            : NifModelTargetBlock.TargetNotInFile);
    }

    /// <summary>
    ///     Binds a controlled block's Node Name, a header string-table index (<see cref="NifControlledBlockView.NodeNameIndex" />)
    ///     resolved against the raw table of the file that stores the controlled block.
    /// </summary>
    /// <param name="nameIndex">The stored index (-1 for the NULL string).</param>
    /// <param name="strings">The raw header string table of the file holding the controlled block.</param>
    /// <returns>The match; NoTargetName for -1, UnresolvedTargetName for an index outside the table.</returns>
    public NifModelTargetMatch Match(int nameIndex, NifHeaderStringTable strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        if (!NifAnimationStrings.TryGetRaw(strings, nameIndex, out var bytes, out var isNone))
        {
            return Blocked([], NifModelTargetBlock.UnresolvedTargetName);
        }

        return isNone ? Blocked([], NifModelTargetBlock.NoTargetName) : Match(bytes.Span);
    }

    /// <summary>The native-only reason text of a blocked target.</summary>
    /// <param name="block">The reason; not <see cref="NifModelTargetBlock.None" />.</param>
    /// <returns>The reason text.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The reason is None or not defined.</exception>
    public static string Reason(NifModelTargetBlock block)
    {
        return block switch
        {
            NifModelTargetBlock.NoTargetName => NoTargetNameReason,
            NifModelTargetBlock.UnresolvedTargetName => UnresolvedTargetNameReason,
            NifModelTargetBlock.AmbiguousTarget => AmbiguousTargetReason,
            NifModelTargetBlock.TargetNotInSkeleton => TargetNotInSkeletonReason,
            NifModelTargetBlock.TargetNotInFile => TargetNotInFileReason,
            NifModelTargetBlock.TargetNotPlaced => TargetNotPlacedReason,
            NifModelTargetBlock.PaletteUnreadable => PaletteUnreadableReason,
            _ => throw new ArgumentOutOfRangeException(nameof(block), block, "No reason for this target outcome.")
        };
    }

    /// <summary>A stable machine code for a blocked target, for native-state payloads.</summary>
    /// <param name="block">The reason; not <see cref="NifModelTargetBlock.None" />.</param>
    /// <returns>The code.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The reason is None or not defined.</exception>
    public static string Code(NifModelTargetBlock block)
    {
        return block switch
        {
            NifModelTargetBlock.NoTargetName => "noTargetName",
            NifModelTargetBlock.UnresolvedTargetName => "unresolvedTargetName",
            NifModelTargetBlock.AmbiguousTarget => "ambiguousTarget",
            NifModelTargetBlock.TargetNotInSkeleton => "targetNotInSkeleton",
            NifModelTargetBlock.TargetNotInFile => "targetNotInFile",
            NifModelTargetBlock.TargetNotPlaced => "targetNotPlaced",
            NifModelTargetBlock.PaletteUnreadable => "paletteUnreadable",
            _ => throw new ArgumentOutOfRangeException(nameof(block), block, "No code for this target outcome.")
        };
    }

    /// <summary>Indexes names by their exact bytes; each key keeps its distinct blocks in first-seen order.</summary>
    private static Dictionary<string, List<int>> Index(IEnumerable<NifModelTargetName> names)
    {
        var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var key = Encoding.Latin1.GetString(name.RawName.Span);
            if (!index.TryGetValue(key, out var blocks))
            {
                index.Add(key, [name.Block]);
            }
            else if (!blocks.Contains(name.Block))
            {
                blocks.Add(name.Block);
            }
        }

        return index;
    }

    /// <summary>Every placed node's resolved NiObjectNET Name, in block order.</summary>
    private static List<NifModelTargetName> ObjectNames(NifModelNodeGraph graph)
    {
        var names = new List<NifModelTargetName>(graph.FactsByBlock.Count);
        foreach (var (block, facts) in graph.FactsByBlock.OrderBy(static pair => pair.Key))
        {
            if (facts.NameValue.IsResolved)
            {
                names.Add(new NifModelTargetName(facts.NameValue.RawBytes, block));
            }
        }

        return names;
    }

    /// <summary>
    ///     Reads a completely decoded NiDefaultAVObjectPalette's entries (nif.xml AVObject: Name SizedString, AV Object
    ///     Ptr) with their exact name bytes. An entry naming no object, or a block outside the file, names block -1.
    /// </summary>
    /// <returns>False when the block is not a palette, did not decode completely, or an entry has an unexpected shape.</returns>
    private static bool TryReadPaletteNames(NifModelReadState state, int paletteIndex,
        out List<NifModelTargetName> names)
    {
        names = [];
        if ((uint)paletteIndex >= (uint)state.Blocks.Count)
        {
            return false;
        }

        var palette = state.Blocks[paletteIndex];
        if (!state.Schema.Inherits(palette.Type, PaletteType) || !palette.IsComplete ||
            !palette.Root.TryGet("Objs", out var objectsValue) || objectsValue is not NifArrayValue objects)
        {
            return false;
        }

        foreach (var item in objects.Items)
        {
            if (item is not NifStructValue entry ||
                !entry.TryGet("Name", out var nameValue) || nameValue is not NifSizedStringValue name ||
                !entry.TryGet("AV Object", out var targetValue) || targetValue is not NifRefValue target)
            {
                names = [];
                return false;
            }

            var block = target.IsNone || (uint)target.Index >= (uint)state.Blocks.Count ? -1 : target.Index;
            names.Add(new NifModelTargetName(name.RawBytes, block));
        }

        return true;
    }

    private NifModelTargetMatch Bind(byte[] raw, List<int> blocks, NifModelTargetSource source)
    {
        var candidates = blocks.ToArray();
        if (candidates.Length > 1)
        {
            return new NifModelTargetMatch(raw, NifModelTargetBlock.AmbiguousTarget, source, -1, [], candidates);
        }

        var block = candidates[0];
        if (block < 0 || block >= _occurrencesByBlock.Count || _occurrencesByBlock[block].Count == 0)
        {
            return new NifModelTargetMatch(raw, NifModelTargetBlock.TargetNotPlaced, source, -1, [], candidates);
        }

        return new NifModelTargetMatch(raw, NifModelTargetBlock.None, source, block, _occurrencesByBlock[block],
            candidates);
    }

    private static NifModelTargetMatch Blocked(byte[] raw, NifModelTargetBlock block)
    {
        return new NifModelTargetMatch(raw, block, NifModelTargetSource.None, -1, [], []);
    }
}

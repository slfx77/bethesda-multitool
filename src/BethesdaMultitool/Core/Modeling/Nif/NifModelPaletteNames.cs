using System.Globalization;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The object names NiDefaultAVObjectPalette blocks supply (plan section 3, "Nodes" and "Everything else"): for
///     each NiAVObject block a palette names, that name. <see cref="NifModelNodeReader" /> uses it as a node's display name
///     only where it adds information, that is when the node's own NiObjectNET Name is null, unresolved or empty (the
///     null Xbox names the plan names); a node's own name stays authoritative and a differing palette name is kept in
///     native state only.
/// </summary>
/// <remarks>
///     Only palettes reachable from the footer roots through Ref links (normally through the root's
///     NiControllerManager Object Palette) and decoded exactly are read. When two palette entries give one block
///     different names, neither is used and a diagnostic names both. A palette whose names became display names is
///     Typed (<see cref="Dispositions" />); since cut-1b slice 10 a palette the animation stage bound a target through
///     is Typed too, and any other takes the plan 2.1 table row
///     (<see cref="NifModelAnimationCoverage.PaletteNoBindingReason" />).
/// </remarks>
internal sealed class NifModelPaletteNames
{
    /// <summary>The NameSource recorded for a display name taken from a palette.</summary>
    public const string PaletteNameSource = "palette";

    /// <summary>Diagnostic code for two palette entries that name one block differently.</summary>
    public const string ConflictDiagnostic = "bmt.nif.palette-name-conflict";

    private const string PaletteType = "NiDefaultAVObjectPalette";

    private readonly Dictionary<int, NifModelPaletteEntry> _byBlock;

    private NifModelPaletteNames(Dictionary<int, NifModelPaletteEntry> byBlock, IReadOnlyList<SceneDiagnostic> diagnostics)
    {
        _byBlock = byBlock;
        Diagnostics = diagnostics;
    }

    /// <summary>No palette names.</summary>
    public static NifModelPaletteNames Empty { get; } = new([], Array.Empty<SceneDiagnostic>());

    /// <summary>Diagnostics for conflicting palette names.</summary>
    public IReadOnlyList<SceneDiagnostic> Diagnostics { get; }

    /// <summary>The palette name of one block, when exactly one name is given for it.</summary>
    public bool TryGet(int block, out NifModelPaletteEntry entry)
    {
        return _byBlock.TryGetValue(block, out entry);
    }

    /// <summary>Reads every reachable, exactly decoded palette.</summary>
    /// <param name="state">The read state.</param>
    /// <param name="reachable">Which blocks are reachable from the footer roots through Ref links.</param>
    /// <param name="cancellationToken">Observed per palette.</param>
    public static NifModelPaletteNames Read(NifModelReadState state, IReadOnlyList<bool> reachable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(reachable);
        var byBlock = new Dictionary<int, NifModelPaletteEntry>();
        var conflicts = new Dictionary<int, NifModelPaletteEntry>();
        var sink = new NifModelDiagnosticSink();
        foreach (var block in state.Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reachable[block.Index] || !block.IsComplete || !state.Schema.Inherits(block.Type, PaletteType) ||
                !block.Root.TryGet("Objs", out var objectsValue) || objectsValue is not NifArrayValue objects)
            {
                continue;
            }

            foreach (var item in objects.Items)
            {
                if (item is not NifStructValue entry ||
                    !entry.TryGet("Name", out var nameValue) || nameValue is not NifSizedStringValue name ||
                    !entry.TryGet("AV Object", out var targetValue) || targetValue is not NifRefValue target ||
                    target.IsNone || (uint)target.Index >= (uint)state.Blocks.Count || name.RawBytes.Length == 0)
                {
                    continue;
                }

                var candidate = new NifModelPaletteEntry(block.Index, name.Text, name.RawBytes);
                if (conflicts.ContainsKey(target.Index))
                {
                    continue;
                }

                if (!byBlock.TryGetValue(target.Index, out var existing))
                {
                    byBlock.Add(target.Index, candidate);
                }
                else if (!existing.RawName.Span.SequenceEqual(candidate.RawName.Span))
                {
                    byBlock.Remove(target.Index);
                    conflicts.Add(target.Index, existing);
                    sink.Add(ConflictDiagnostic, string.Create(CultureInfo.InvariantCulture,
                        $"Block {target.Index} is named '{existing.Name}' by palette block {existing.PaletteBlock} and " +
                        $"'{candidate.Name}' by palette block {candidate.PaletteBlock}; neither becomes its display " +
                        $"name."));
                }
            }
        }

        return byBlock.Count == 0 && conflicts.Count == 0 ? Empty : new NifModelPaletteNames(byBlock, sink.ToList());
    }

    /// <summary>Typed for every palette that supplied at least one placed node's display name.</summary>
    public static IReadOnlyDictionary<int, NifModelBlockDisposition> Dispositions(NifModelNodeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var dispositions = new Dictionary<int, NifModelBlockDisposition>();
        foreach (var facts in graph.FactsByBlock.Values)
        {
            if (string.Equals(facts.NameSource, PaletteNameSource, StringComparison.Ordinal) &&
                facts.Palette is { } palette)
            {
                dispositions[palette.PaletteBlock] = NifModelBlockDisposition.Typed;
            }
        }

        return dispositions;
    }
}

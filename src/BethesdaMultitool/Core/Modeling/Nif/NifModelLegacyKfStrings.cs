using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut 2: the string table <see cref="NifModelAnimationSource" /> synthesizes for a 20.0.0.4 <c>.kf</c>
///     (<see cref="NifModelProbe.IsLegacyKfKey" />), whose header stores no string table: every NiControllerSequence is
///     read through the Oblivion view (<see cref="NifControllerSequenceNameTrackReader.TryReadOblivionSequenceView" />)
///     and its inline Name, its non-empty Accum Root Name and each controlled block's five palette entries (resolved to
///     their stored bytes by <see cref="NifControllerSequenceNameTrackReader.TryResolvePaletteStringBytes" />, the one
///     palette rule) become the entries of a <see cref="NifHeaderStringTable" />, deduplicated by exact bytes in
///     first-seen order. Each sequence is then mapped into the 20.2.0.7 <see cref="NifControllerSequenceView" /> the
///     binder consumes, its string fields carrying the synthesized indices.
/// </summary>
/// <remarks>
///     <para>
///         The mapping is the one substitution point (owner question D-kf1, answered here): <see cref="NifModelAnimationSource.Strings" />
///         and <see cref="NifModelAnimationSource.TryReadSequence" /> hand the binder, the extras, the native rows and
///         the skeleton provenance the same shapes they take for a 20.2.0.7 stream, so target matching, the clock and
///         curve mapping, the source policy and the events are the same code. Nothing is lost: the Oblivion view stays
///         reachable through <see cref="NifModelAnimationSource.TryReadLegacySequence" />, and the clip extras record the
///         stored palette refs and offsets beside the synthesized indices (<see cref="NifModelAnimationExtras" />).
///     </para>
///     <para>
///         Index rules, in the terms <see cref="NifAnimationStrings" /> already gives them: a resolved string is its
///         table index; an empty StringOffset sentinel (0xFFFFFFFF, or 0x0000FFFF as the resolver honors it) is
///         <see cref="NifAnimationStrings.NoString" /> (-1), as is an empty Accum Root Name (the engine's 'no root');
///         a non-sentinel offset that does not resolve through the palette is <see cref="UnresolvedIndex" />, which
///         every consumer refuses exactly as it refuses a 20.2.0.7 index outside the table (so the same 'outside the
///         string table' reasons fire; none of the five FNV-shipped files' 1,825 offsets is unresolved, measured; the
///         identity reaches the Oblivion <c>.kf</c> too, whose offsets are not measured, and an unresolved one keeps its
///         block native rather than throwing).
///     </para>
/// </remarks>
internal sealed class NifModelLegacyKfStrings
{
    /// <summary>The synthesized index of a non-sentinel palette offset that does not resolve (outside every table).</summary>
    public const int UnresolvedIndex = -2;

    private const string SequenceType = "NiControllerSequence";

    private readonly Dictionary<string, int> _indexByText = new(StringComparer.Ordinal);
    private readonly List<byte[]> _entries = [];
    private readonly Dictionary<int, NifOblivionControllerSequenceView> _legacy = new();
    private readonly Dictionary<int, NifControllerSequenceView> _mapped = new();

    private NifModelLegacyKfStrings()
    {
    }

    /// <summary>The synthesized table (its offset is the header's, where a string table would sit; nothing is stored there).</summary>
    public NifHeaderStringTable Table { get; private set; } = new(0, 0, []);

    /// <summary>
    ///     Builds the table and the mapped views for a 20.0.0.4 <c>.kf</c>; null when the identity is not the legacy
    ///     <c>.kf</c> key, so callers keep the header's own table.
    /// </summary>
    /// <param name="bytes">The whole file.</param>
    /// <param name="info">NifParser's header and block table.</param>
    /// <param name="tableOffset">The header offset recorded on the synthesized table (the legacy header's string-table position).</param>
    /// <returns>The strings, or null.</returns>
    public static NifModelLegacyKfStrings? TryBuild(byte[] bytes, NifInfo info, int tableOffset)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(info);
        if (!NifModelProbe.IsLegacyKfKey(info.BinaryVersion, info.UserVersion, info.BsVersion, info.IsBigEndian))
        {
            return null;
        }

        var strings = new NifModelLegacyKfStrings();
        var pending = new List<(int Block, NifOblivionControllerSequenceView View)>();
        foreach (var block in info.Blocks)
        {
            if (block.TypeName != SequenceType ||
                !NifControllerSequenceNameTrackReader.TryReadOblivionSequenceView(bytes, info, block, out var view))
            {
                continue;
            }

            strings._legacy[block.Index] = view;
            pending.Add((block.Index, view));
        }

        var maxLength = 0u;
        foreach (var (block, view) in pending)
        {
            strings._mapped[block] = strings.Map(bytes, info, view);
        }

        foreach (var entry in strings._entries)
        {
            maxLength = Math.Max(maxLength, (uint)entry.Length);
        }

        strings.Table = new NifHeaderStringTable(tableOffset, maxLength, strings._entries.ToArray());
        return strings;
    }

    /// <summary>The Oblivion view of a sequence block, or null when the block is not a readable sequence.</summary>
    public NifOblivionControllerSequenceView? Legacy(int block)
    {
        return _legacy.GetValueOrDefault(block);
    }

    /// <summary>The mapped 20.2.0.7-shaped view of a sequence block, or null when the block is not a readable sequence.</summary>
    public NifControllerSequenceView? Mapped(int block)
    {
        return _mapped.GetValueOrDefault(block);
    }

    private NifControllerSequenceView Map(byte[] bytes, NifInfo info, NifOblivionControllerSequenceView view)
    {
        var name = Intern(view.NameBytes);
        var accumRoot = view.AccumRootNameBytes.Length == 0 ? NifAnimationStrings.NoString : Intern(view.AccumRootNameBytes);
        var blocks = new NifControlledBlockView[view.ControlledBlocks.Length];
        for (var i = 0; i < blocks.Length; i++)
        {
            var block = view.ControlledBlocks[i];
            blocks[i] = new NifControlledBlockView(
                block.Offset,
                block.InterpolatorRef,
                block.ControllerRef,
                block.Priority,
                Palette(bytes, info, block.StringPaletteRef, block.NodeNameOffset),
                Palette(bytes, info, block.StringPaletteRef, block.PropertyTypeOffset),
                Palette(bytes, info, block.StringPaletteRef, block.ControllerTypeOffset),
                Palette(bytes, info, block.StringPaletteRef, block.ControllerIdOffset),
                Palette(bytes, info, block.StringPaletteRef, block.InterpolatorIdOffset));
        }

        return new NifControllerSequenceView(
            name,
            view.ArrayGrowBy,
            blocks,
            view.WeightBits,
            view.TextKeysRef,
            view.RawCycle,
            view.FrequencyBits,
            view.StartTimeBits,
            view.StopTimeBits,
            view.ManagerRef,
            accumRoot,
            null,
            null,
            view.TailExact);
    }

    /// <summary>One StringOffset as a synthesized index: -1 for a sentinel, the entry's index when it resolves, else <see cref="UnresolvedIndex" />.</summary>
    private int Palette(byte[] bytes, NifInfo info, int paletteRef, uint offset)
    {
        if (NifControllerSequenceNameTrackReader.IsEmptyPaletteOffset(offset))
        {
            return NifAnimationStrings.NoString;
        }

        return NifControllerSequenceNameTrackReader.TryResolvePaletteStringBytes(bytes, info, paletteRef, offset,
            out var raw)
            ? Intern(raw)
            : UnresolvedIndex;
    }

    /// <summary>The index of the entry with exactly these bytes, adding it in first-seen order.</summary>
    private int Intern(ReadOnlyMemory<byte> raw)
    {
        var key = Encoding.Latin1.GetString(raw.Span);
        if (_indexByText.TryGetValue(key, out var index))
        {
            return index;
        }

        index = _entries.Count;
        _entries.Add(raw.ToArray());
        _indexByText.Add(key, index);
        return index;
    }
}

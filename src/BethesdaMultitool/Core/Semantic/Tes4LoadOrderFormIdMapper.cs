using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;

namespace BethesdaMultitool.Core.Semantic;

/// <summary>
///     Rebases TES4-family (Oblivion → Starfield) plugin FormIDs from file-local mod indices to
///     load-order-global ones before merging. Inside a plugin, a FormID's high byte indexes that
///     file's OWN master list (index &lt; master count → that master's record; otherwise the file's
///     own record) — so every DLC's new records ship with the same raw <c>0x01</c> prefix and would
///     collide in a FormID-keyed merge (DLCCoast's Far Harbor cells overwritten by DLCNukaWorld's,
///     etc.). Mapping each source's high byte through its master list into the shared load order
///     gives every file a disjoint block while keeping cross-file overrides (index → the same
///     master) folding together, matching engine/xEdit load-order FormID resolution.
///     <para>
///         The optional primary file is merged UNSTAMPED elsewhere, so it keeps its raw file-local
///         FormIDs and the load order must be arranged AROUND it: each of its masters takes the slot
///         matching its index in the primary's MAST list, and the primary itself sits at its own
///         master count. (A base-game primary has no masters and so still lands at 0, which is why
///         this looked correct for years.) Pinning the primary to slot 0 unconditionally — as this
///         did until 2026-08-17 — shifted every master record up one slot (a lone
///         master with no masters of its own went <c>0x00xxxxxx</c> → <c>0x01xxxxxx</c>) while the
///         primary's references stayed at <c>0x00xxxxxx</c>, so they resolved to nothing AND the
///         relocated master records collided with the primary's own <c>0x01</c> range, where the
///         last-merged primary won. Adding a master to the Load Order therefore left placements no
///         more resolvable than leaving it out. With masters first, both sides are identity no-ops
///         and the references land where the records actually are.
///         Masters referenced but absent from the load set get stable synthetic slots past the real
///         files so two plugins sharing a missing master still fold together.
///     </para>
///     <para>
///         Only applies when a primary is supplied. A memory-dump primary passes null (a dump is
///         self-contained and owns the unstamped <c>0x00</c> range), and TES3 collections return
///         early — <see cref="Tes3LoadOrderNamespacer" /> keeps its own slot-0 reservation, which
///         this ordering does not touch.
///     </para>
/// </summary>
internal sealed class Tes4LoadOrderFormIdMapper
{
    private const int HeaderReadBytes = 8192;

    private readonly Dictionary<string, int> _slotByFileName;
    private readonly Dictionary<string, int> _missingMasterSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, IReadOnlyList<string>> _mastersReader;
    private int _nextMissingSlot;

    private Tes4LoadOrderFormIdMapper(
        Dictionary<string, int> slotByFileName,
        int firstMissingSlot,
        Func<string, IReadOnlyList<string>> mastersReader)
    {
        _slotByFileName = slotByFileName;
        _nextMissingSlot = firstMissingSlot;
        _mastersReader = mastersReader;
    }

    /// <summary>
    ///     Builds a mapper for an ordered plugin set, or null when there is nothing to disambiguate
    ///     (fewer than two files in play). <paramref name="primaryFilePath" /> is the externally-merged
    ///     primary plugin the load order is arranged around: its masters anchor the low slots and the
    ///     primary itself sits at its own master count (see the class remarks). It must be a
    ///     resolvable FULL PATH, not just a file name — the primary's MAST list is read from disk to
    ///     place the anchors, and a value that cannot be opened silently degrades to the
    ///     pre-2026-08-17 primary-at-slot-0 layout.
    ///     <paramref name="mastersReader" /> overrides the on-disk TES4-header master-list read (tests).
    /// </summary>
    public static Tes4LoadOrderFormIdMapper? TryCreate(
        IReadOnlyList<string> orderedFilePaths,
        string? primaryFilePath = null,
        Func<string, IReadOnlyList<string>>? mastersReader = null)
    {
        var hasPrimary = !string.IsNullOrWhiteSpace(primaryFilePath);
        var slotCount = orderedFilePaths.Count + (hasPrimary ? 1 : 0);
        if (slotCount < 2)
        {
            return null;
        }

        var reader = mastersReader ?? ReadMasters;
        var slots = new Dictionary<string, int>(slotCount, StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<int>();

        if (hasPrimary)
        {
            // The primary keeps its raw FormIDs, so its own high bytes already name slots by
            // position: each master at its index in the primary's MAST list, the primary's own
            // records at masterCount. Anchor those first and let the rest fill in around them.
            // A base-game primary (no masters) still lands at 0, as it did before.
            var primaryMasters = reader(primaryFilePath!);
            for (var i = 0; i < primaryMasters.Count; i++)
            {
                // Reserve the index even when the name is a duplicate (TryAdd fails): the primary's
                // raw references still use this high byte for the earlier occurrence, so handing the
                // hole to an unrelated filler would alias them onto the wrong file's records. A
                // reserved-but-vacant slot merely leaves those references dangling.
                slots.TryAdd(Path.GetFileName(primaryMasters[i]), i);
                used.Add(i);
            }

            slots[Path.GetFileName(primaryFilePath!)] = primaryMasters.Count;
            used.Add(primaryMasters.Count);
        }

        var next = 0;
        foreach (var path in orderedFilePaths)
        {
            if (slots.ContainsKey(Path.GetFileName(path)))
            {
                continue;
            }

            while (used.Contains(next))
            {
                next++;
            }

            slots[Path.GetFileName(path)] = next;
            used.Add(next);
        }

        // Synthetic slots for masters referenced but absent from the load set must start past EVERY
        // occupied slot: anchoring can place slots at or above slotCount, so a slotCount-based seed
        // could hand an absent master an occupied slot and alias two files into one global block.
        var firstMissingSlot = used.Count == 0 ? slotCount : used.Max() + 1;
        return new Tes4LoadOrderFormIdMapper(slots, firstMissingSlot, reader);
    }

    /// <summary>
    ///     Returns <paramref name="records" /> with every registered FormID property mapped from
    ///     <paramref name="filePath" />'s local mod indices to global load-order slots. Identity (and
    ///     allocation-free) for TES3 collections, non-plugin sources, and files whose local indices
    ///     already equal their global ones (e.g. the base master at slot 0).
    /// </summary>
    public RecordCollection Namespaced(RecordCollection records, string filePath)
    {
        if (records.IsTes3 || !IsPluginFile(filePath) ||
            !_slotByFileName.TryGetValue(Path.GetFileName(filePath), out var ownSlot))
        {
            return records;
        }

        var masters = _mastersReader(filePath);
        // Local index h → global slot: h names masters[h] for h < count, the file's own records above.
        var map = new byte[masters.Count + 1];
        var identity = ownSlot == masters.Count;
        for (var h = 0; h < masters.Count; h++)
        {
            var slot = ResolveMasterSlot(masters[h]);
            map[h] = (byte)Math.Min(slot, 0xFF);
            identity &= map[h] == h;
        }

        map[masters.Count] = (byte)Math.Min(ownSlot, 0xFF);
        if (identity)
        {
            return records;
        }

        return RecordCollectionFormIdRebaser.Rebase(records, formId =>
        {
            // 0 = null reference, 0xFFFFFFFF = unset sentinel — never remap. 0xFF-prefixed runtime
            // FormIDs never occur in plugin files (only in dumps/saves, which skip this mapper).
            if (formId is 0 or 0xFFFFFFFF)
            {
                return formId;
            }

            var local = (int)(formId >> 24);
            // Indices past the master count all mean "this file's own record" (xEdit clamps the same way).
            var global = map[Math.Min(local, masters.Count)];
            return ((uint)global << 24) | (formId & 0x00FFFFFF);
        });
    }

    private int ResolveMasterSlot(string masterFileName)
    {
        if (_slotByFileName.TryGetValue(masterFileName, out var slot))
        {
            return slot;
        }

        if (!_missingMasterSlots.TryGetValue(masterFileName, out slot))
        {
            slot = _nextMissingSlot++;
            _missingMasterSlots[masterFileName] = slot;
        }

        return slot;
    }

    private static bool IsPluginFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return extension.Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".esp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".esl", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ReadMasters(string filePath)
    {
        try
        {
            // Share generously: the primary is usually already open (often memory-mapped) by the
            // caller that is asking about it. A sharing violation here would be caught below and
            // return an empty master list, which silently reinstates the slot-0 bug this class
            // documents — so it must not be allowed to happen for a benign reason.
            using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[Math.Min(HeaderReadBytes, stream.Length)];
            // Fill the buffer, never a single Read: Stream.Read may legally return short (network
            // paths, filter drivers), and the header parser treats a truncated prefix as a VALID
            // shorter MAST list — silently wrong anchors rather than an error.
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return EsmParser.ParseFileHeader(buffer.AsSpan(0, read))?.Masters ?? [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}

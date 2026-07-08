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
///         The optional primary file occupies global slot 0 (its records are merged unstamped
///         elsewhere, exactly like the TES3 namespacer's unstamped base range); the set's sources
///         then start at slot 1. Masters referenced but absent from the load set get stable synthetic
///         slots past the real files so two plugins sharing a missing master still fold together.
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
        int slotCount,
        Func<string, IReadOnlyList<string>> mastersReader)
    {
        _slotByFileName = slotByFileName;
        _nextMissingSlot = slotCount;
        _mastersReader = mastersReader;
    }

    /// <summary>
    ///     Builds a mapper for an ordered plugin set, or null when there is nothing to disambiguate
    ///     (fewer than two files in play). <paramref name="primaryFileName" /> is the externally-merged
    ///     primary plugin that owns global slot 0, when the set supplements one.
    ///     <paramref name="mastersReader" /> overrides the on-disk TES4-header master-list read (tests).
    /// </summary>
    public static Tes4LoadOrderFormIdMapper? TryCreate(
        IReadOnlyList<string> orderedFilePaths,
        string? primaryFileName = null,
        Func<string, IReadOnlyList<string>>? mastersReader = null)
    {
        var hasPrimary = !string.IsNullOrWhiteSpace(primaryFileName);
        var slotCount = orderedFilePaths.Count + (hasPrimary ? 1 : 0);
        if (slotCount < 2)
        {
            return null;
        }

        var slots = new Dictionary<string, int>(slotCount, StringComparer.OrdinalIgnoreCase);
        if (hasPrimary)
        {
            slots[Path.GetFileName(primaryFileName!)] = 0;
        }

        for (var i = 0; i < orderedFilePaths.Count; i++)
        {
            slots.TryAdd(Path.GetFileName(orderedFilePaths[i]), i + (hasPrimary ? 1 : 0));
        }

        return new Tes4LoadOrderFormIdMapper(slots, slotCount, mastersReader ?? ReadMasters);
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
            using var stream = File.OpenRead(filePath);
            var buffer = new byte[Math.Min(HeaderReadBytes, stream.Length)];
            var read = stream.Read(buffer, 0, buffer.Length);
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

using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;

namespace BethesdaMultitool.Core.Actors;

/// <summary>Generates a bounded inventory preview while retaining unresolved branches and source ownership.</summary>
internal sealed class ActorInventoryGenerator
{
    internal const int MaximumRows = 2048;
    internal const int MaximumWork = 65536;
    private const int MaximumDepth = 16;
    private readonly RecordCollection _records;
    private readonly IReadOnlyDictionary<uint, LeveledListRecord> _lists;
    private readonly ActorInventoryIndex _index;
    private readonly Dictionary<uint, List<LeveledEntry>> _eligible = [];
    private readonly HashSet<uint> _ancestors = [];
    private readonly List<ActorInventoryEntry> _rows = [];
    private readonly List<InventoryItem> _items = [];
    private readonly List<string> _notices = [];
    private readonly ActorInventoryRandom _random;
    private readonly CancellationToken _cancellation;
    private readonly uint _actor;
    private readonly uint _source;
    private readonly ushort _level;
    private readonly uint _seed;
    private bool _generated;
    private int _work;
    private string? _notice;

    /// <summary>Captures one generation's parsed snapshot, seed, explicit level and cancellation.</summary>
    /// <param name="records">Parsed records; callers must retain and avoid mutating them during generation.</param>
    /// <param name="lists">Final-override leveled-list index already built by the inspector.</param>
    /// <param name="index">Immutable concrete-item and global index for the same parsed snapshot.</param>
    /// <param name="actor">Selected actor's FormID.</param>
    /// <param name="source">Actor supplying the inherited or authored inventory.</param>
    /// <param name="level">Explicit nonzero preview level.</param>
    /// <param name="seed">Repeatable unsigned seed.</param>
    /// <param name="cancellation">Cancels before each recursive visit, list scan and roll.</param>
    internal ActorInventoryGenerator(RecordCollection records, IReadOnlyDictionary<uint, LeveledListRecord> lists,
        ActorInventoryIndex index, uint actor, uint source, ushort level, uint seed, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfZero(level);
        _records = records;
        _lists = lists;
        _actor = actor;
        _source = source;
        _level = level;
        _seed = seed;
        _random = new ActorInventoryRandom(seed);
        _cancellation = cancellation;
        _index = index;
    }

    /// <summary>Processes authored entries once in order and returns their concrete output and visible trace.</summary>
    /// <param name="inventory">Source inventory declarations including parent ownership.</param>
    /// <param name="initialNotice">Unresolved template notice from the inspector, if present.</param>
    /// <returns>Trace rows, exact concrete inventory, and the first unresolved/truncation notice.</returns>
    /// <exception cref="InvalidOperationException">This single-use generator was already run.</exception>
    internal (IReadOnlyList<ActorInventoryEntry> Rows, ActorInventoryGeneration Generation, string? Notice)
        Generate(IReadOnlyList<InventoryItem> inventory, string? initialNotice)
    {
        if (_generated) throw new InvalidOperationException("An inventory generator can run only once.");
        _generated = true;
        _notice = initialNotice;
        if (initialNotice is not null) _notices.Add(initialNotice);
        foreach (var item in inventory)
        {
            if (!SpendWork()) break;
            Expand(item.ItemFormId, item.Count, item, 0, null, false);
        }
        return (_rows.AsReadOnly(), new ActorInventoryGeneration(_seed, _level, _items.AsReadOnly(), _notice is null)
            { Notices = _notices.AsReadOnly() }, _notice);
    }

    /// <summary>Expands one declaration; limits and missing records remain visible without fabricating an item.</summary>
    /// <param name="id">Item or leveled-list identity.</param>
    /// <param name="count">Positive multiplicity inherited through the selected branch.</param>
    /// <param name="owner">Parent CNTO ownership retained on concrete output.</param>
    /// <param name="depth">Current nesting depth, bounded before further recursion.</param>
    /// <param name="requiredLevel">Authored level of the selected entry, if this is a child.</param>
    /// <param name="generated">Whether a leveled choice produced this item.</param>
    private void Expand(uint id, long count, InventoryItem owner, int depth, ushort? requiredLevel, bool generated)
    {
        if (!SpendWork()) return;
        _lists.TryGetValue(id, out var list);
        var rowIndex = _rows.Count;
        var status = _source == _actor ? "Authored" : "Inherited";
        if (generated) status = "Generated";
        if (list is not null) status = "Leveled";
        _rows.Add(new ActorInventoryEntry(id, Name(id), count, _source, depth, status,
            requiredLevel, list?.ChanceNone, list?.Flags, list?.GlobalFormId, owner.OwnerFormId, owner.ItemCondition));
        if (count <= 0) { Fail(rowIndex, "InvalidCount"); return; }
        if (list is null)
        {
            if (!_index.Contains(id)) { Fail(rowIndex, "Unresolved"); return; }
            if (count > int.MaxValue) { Fail(rowIndex, "CountOverflow"); return; }
            _items.Add(owner with { ItemFormId = id, Count = (int)count });
            return;
        }
        if (depth >= MaximumDepth) { Fail(rowIndex, "Truncated"); return; }
        if (_ancestors.Contains(id)) { Fail(rowIndex, "Cycle"); return; }
        if (list.ListType != "LVLI") { Fail(rowIndex, "UnsupportedList"); return; }
        if ((list.Flags & ~7) != 0) { Fail(rowIndex, "UnsupportedFlags"); return; }
        float chance = list.ChanceNone;
        if (list.GlobalFormId is { } global && global != 0 && !_index.TryGetGlobal(global, out chance))
        {
            Fail(rowIndex, "GlobalUnresolved");
            return;
        }
        if (!float.IsFinite(chance) || chance is < 0 or > 100) { Fail(rowIndex, "InvalidChance"); return; }
        _rows[rowIndex] = _rows[rowIndex] with { ResolvedChanceNone = chance };
        if (!_eligible.TryGetValue(id, out var entries))
        {
            if (!SpendWork(list.Entries.Count)) { Fail(rowIndex, "Truncated"); return; }
            entries = LeveledItemEligibility.Select(list.Entries, list.Flags, _level, useAll: true);
            _eligible[id] = entries;
        }
        if (entries.Count == 0)
        {
            _rows[rowIndex] = _rows[rowIndex] with { Status = "NoEligible" };
            return;
        }
        _ancestors.Add(id);
        try
        {
            var useAll = (list.Flags & 4) != 0;
            var each = !useAll && (list.Flags & 2) != 0;
            var rolls = each ? count : 1;
            var multiplier = each ? 1 : count;
            for (long roll = 0; roll < rolls; roll++)
            {
                if (!SpendWork()) break;
                if (_random.ChanceNone(chance))
                {
                    if (rolls == 1) _rows[rowIndex] = _rows[rowIndex] with { Status = "ChanceNone" };
                    else _rows.Add(_rows[rowIndex] with { Count = 1, Depth = depth + 1, Status = "ChanceNone" });
                    continue;
                }
                if (useAll)
                {
                    foreach (var entry in entries)
                    {
                        if (!SpendWork()) break;
                        ExpandEntry(entry, multiplier, owner, depth, rowIndex);
                    }
                }
                else ExpandEntry(entries[_random.Next(entries.Count)], multiplier, owner, depth, rowIndex);
            }
        }
        finally { _ancestors.Remove(id); }
    }

    /// <summary>Multiplies counts with an overflow guard before visiting a selected child.</summary>
    /// <param name="entry">A positive-count eligible entry.</param>
    /// <param name="multiplier">Positive parent multiplicity.</param>
    /// <param name="owner">Parent ownership.</param>
    /// <param name="depth">Parent depth.</param>
    /// <param name="parentRow">Existing trace row to mark when multiplication overflows.</param>
    private void ExpandEntry(LeveledEntry entry, long multiplier, InventoryItem owner, int depth, int parentRow)
    {
        if (multiplier > long.MaxValue / entry.Count) { Fail(parentRow, "CountOverflow"); return; }
        Expand(entry.FormId, multiplier * entry.Count, owner, depth + 1, entry.Level, true);
    }

    /// <summary>Enforces the total visit/scan/roll and trace-row budgets and observes cancellation.</summary>
    /// <param name="amount">Nonnegative work units requested by the caller.</param>
    /// <returns>Whether the work fits within both budgets.</returns>
    private bool SpendWork(int amount = 1)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (_rows.Count >= MaximumRows || amount > MaximumWork - _work)
        {
            MarkIncomplete("Truncated");
            _work = MaximumWork;
            return false;
        }
        _work += amount;
        return true;
    }

    /// <summary>Marks one existing trace row and the generation incomplete while retaining successful siblings.</summary>
    /// <param name="row">An already materialized trace row.</param>
    /// <param name="reason">Invariant reason key.</param>
    private void Fail(int row, string reason)
    {
        _rows[row] = _rows[row] with { Status = reason };
        MarkIncomplete(reason);
    }

    /// <summary>Retains every distinct failure reason and the legacy first-notice value.</summary>
    /// <param name="reason">Invariant reason key.</param>
    private void MarkIncomplete(string reason)
    {
        _notice ??= reason;
        if (!_notices.Contains(reason, StringComparer.Ordinal)) _notices.Add(reason);
    }

    /// <summary>Returns a source name or the explicit unresolved FormID.</summary>
    /// <param name="id">Record identity.</param>
    /// <returns>The source label or an invariant hexadecimal fallback.</returns>
    private string Name(uint id) => _records.FormIdToDisplayName.GetValueOrDefault(id)
        ?? _records.FormIdToEditorId.GetValueOrDefault(id) ?? $"0x{id:X8}";
}

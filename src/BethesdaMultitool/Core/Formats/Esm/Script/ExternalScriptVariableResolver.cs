using System.Collections.Concurrent;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Script;

/// <summary>An observed SCRI, runtime script-owner, or placed-reference base link.</summary>
internal sealed record ScriptOwnerLink(uint Owner, uint Target);

/// <summary>
/// Resolves only explicit owner links and agreeing variable tables. A referenced quest or an
/// EditorID ending in REF is not ownership evidence. Conflicts retain the numeric operand.
/// </summary>
internal sealed class ExternalScriptVariableResolver
{
    private readonly Dictionary<uint, ScriptRecord[]> _scripts;
    private readonly Dictionary<uint, uint[]> _links;
    private readonly ConcurrentDictionary<(uint Owner, ushort Index), ScriptExternalVariableBinding> _cache = new();

    /// <summary>
    /// Builds links from this collection's identities, after selection and FormID rebasing. Parser-context
    /// links belong to the source file's namespace and must not be reused for a merged view.
    /// </summary>
    internal static ExternalScriptVariableResolver FromRecords(RecordCollection records)
    {
        // Keep absent SCRI targets as zero: two copies, one with a script and one without, disagree.
        // No ownership is inferred from EditorIDs, script text, SCRO references or nearby records.
        var links = records.Cells.SelectMany(c => c.PlacedObjects).Concat(records.MapMarkers)
            .Where(r => r.RecordType is "REFR" or "ACHR" or "ACRE")
            .Select(r => new ScriptOwnerLink(r.FormId, r.BaseFormId))
            .Concat(records.Npcs.Select(r => new ScriptOwnerLink(r.FormId, r.Script ?? 0)))
            .Concat(records.Creatures.Select(r => new ScriptOwnerLink(r.FormId, r.Script ?? 0)))
            .Concat(records.Activators.Select(r => new ScriptOwnerLink(r.FormId, r.Script ?? 0)))
            .Concat(records.Containers.Select(r => new ScriptOwnerLink(r.FormId, r.Script ?? 0)))
            .Concat(records.Terminals.Select(r => new ScriptOwnerLink(r.FormId, r.ScriptFormId ?? 0)))
            .Concat(records.Doors.Select(r => new ScriptOwnerLink(r.FormId, r.Script ?? 0)))
            .Concat(records.Lights.Select(r => new ScriptOwnerLink(r.FormId, r.Script ?? 0)))
            .Concat(records.Furniture.Select(r => new ScriptOwnerLink(r.FormId, r.Script ?? 0)))
            .Concat(records.Quests.Select(r => new ScriptOwnerLink(r.FormId, r.Script ?? 0)))
            .Concat(records.Ammo.Select(r => new ScriptOwnerLink(r.FormId, r.ScriptFormId ?? 0)))
            .Concat(records.Weapons.Select(r => new ScriptOwnerLink(r.FormId, r.ScriptFormId ?? 0)))
            .Concat(records.Armor.Select(r => new ScriptOwnerLink(r.FormId, r.ScriptFormId ?? 0)))
            .Concat(records.Books.Select(r => new ScriptOwnerLink(r.FormId, r.ScriptFormId ?? 0)))
            .Concat(records.MiscItems.Select(r => new ScriptOwnerLink(r.FormId, r.ScriptFormId ?? 0)))
            .Concat(records.Consumables.Select(r => new ScriptOwnerLink(r.FormId, r.ScriptFormId ?? 0)))
            .Concat(records.CaravanCards.Select(r => new ScriptOwnerLink(r.FormId, r.ScriptFormId)))
            .Concat(records.Challenges.Select(r => new ScriptOwnerLink(r.FormId, r.Script)));
        return new ExternalScriptVariableResolver(records.Scripts, links);
    }

    internal ExternalScriptVariableResolver(IEnumerable<ScriptRecord> scripts, IEnumerable<ScriptOwnerLink> links)
    {
        var records = scripts.ToArray();
        _scripts = records.Where(s => s.FormId != 0).GroupBy(s => s.FormId)
            .ToDictionary(g => g.Key, g => g.ToArray());
        _links = links.Concat(records.Where(s => s.OwnerQuestFormId is > 0)
                .Select(s => new ScriptOwnerLink(s.OwnerQuestFormId!.Value, s.FormId)))
            .Where(l => l.Owner != 0)
            .GroupBy(l => l.Owner).ToDictionary(g => g.Key, g => g.Select(l => l.Target).Distinct().ToArray());
    }

    internal ScriptExternalVariableBinding Resolve(uint owner, ushort index)
    {
        if (_cache.TryGetValue((owner, index), out var cached)) return cached;
        var candidates = new Dictionary<uint, uint[]>();
        var incomplete = false;
        Visit(owner, []);
        var ids = candidates.Keys.Order().ToArray();
        ScriptExternalVariableBinding result;
        if (ids.Length != 1 || incomplete)
        {
            result = new(owner, index, ids.Length == 0 ? "owner-unresolved" : "owner-conflict",
                null, null, ids, []);
        }
        else
        {
            var scriptId = ids[0];
            var copies = _scripts[scriptId];
            var locals = copies.Select(s => s.Variables.Where(v => v.Index == index).ToArray()).ToArray();
            var names = locals.SelectMany(v => v).Select(v => v.Name).Distinct(StringComparer.Ordinal).ToArray();
            var types = locals.SelectMany(v => v).Select(v => v.Type).Distinct().ToArray();
            var resolved = copies.All(s => !s.IsIncompleteExecutableBundle && !s.HasMalformedSerializedTable)
                           && locals.All(v => v.Length == 1) && names.Length == 1
                           && !string.IsNullOrWhiteSpace(names[0]) && types.Length == 1;
            var status = resolved ? "resolved" : locals.All(v => v.Length == 0)
                ? "variable-unresolved" : "variable-conflict";
            result = new(owner, index, status, scriptId, resolved ? names[0] : null,
                ids, candidates[scriptId]);
        }
        _cache.TryAdd((owner, index), result);
        return result;

        void Visit(uint id, List<uint> chain)
        {
            if (chain.Count >= 32 || chain.Contains(id)) { incomplete = true; return; }
            var next = new List<uint>(chain) { id };
            if (_scripts.ContainsKey(id)) candidates.TryAdd(id, next.ToArray());
            if (_links.TryGetValue(id, out var targets))
                foreach (var target in targets) Visit(target, next);
            else if (!_scripts.ContainsKey(id)) incomplete = true;
        }
    }

    internal Func<uint, ushort, string?> Track(List<ScriptExternalVariableBinding> bindings) => (owner, index) =>
    {
        var binding = Resolve(owner, index);
        if (!bindings.Any(b => b.OwnerFormId == owner && b.VariableIndex == index)) bindings.Add(binding);
        return binding.Name;
    };
}

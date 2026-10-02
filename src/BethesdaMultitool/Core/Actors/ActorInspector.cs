using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Actors;

/// <summary>Indexes parsed actor data without requiring meshes, graphics devices, or game archives.</summary>
internal sealed class ActorInspector
{
    private const int MaximumDepth = 16;
    private const int MaximumRows = 2048;
    private readonly RecordCollection _records;
    private readonly Dictionary<uint, NpcRecord> _npcs;
    private readonly Dictionary<uint, CreatureRecord> _creatures;
    private readonly Dictionary<uint, LeveledListRecord> _lists;
    private readonly ActorSourceIdentity _identity;
    private readonly ActorTemplateResolver _templates;
    private static readonly string[] AttributeNames =
        ["Strength", "Perception", "Endurance", "Charisma", "Intelligence", "Agility", "Luck"];
    private ActorInventoryIndex? _generationIndex;

    /// <summary>Builds actor and leveled-list indexes in linear time, preserving the collection's final override.</summary>
    internal ActorInspector(RecordCollection records, ActorSourceIdentity? sourceIdentity = null)
    {
        _records = records;
        _identity = sourceIdentity ?? new ActorSourceIdentity();
        _templates = new ActorTemplateResolver(records, _identity);
        _npcs = [];
        _creatures = [];
        _lists = [];
        foreach (var npc in records.Npcs) _npcs[npc.FormId] = npc;
        foreach (var creature in records.Creatures) _creatures[creature.FormId] = creature;
        foreach (var list in records.LeveledLists) _lists[list.FormId] = list;
    }

    /// <summary>Reads an actor's authored statistics and bounded inventory candidate tree.</summary>
    /// <param name="formId">The actor's resolved FormID.</param>
    /// <param name="creature">Whether the selected actor belongs to the creature family.</param>
    /// <param name="playerLevel">Optional level for leveled-list eligibility.</param>
    /// <param name="seed">Optional repeatable preview seed; requires an explicit nonzero level.</param>
    /// <param name="cancellationToken">Cancellation observed before inspection and during generation.</param>
    /// <returns>The actor detail, or null when the collection does not contain the selected actor.</returns>
    /// <exception cref="ArgumentException">A seed was supplied without a nonzero preview level.</exception>
    internal ActorInspection? Inspect(uint formId, bool creature, ushort? playerLevel = null,
        uint? seed = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (seed.HasValue && playerLevel is null or 0)
            throw new ArgumentException("Seeded inventory requires an explicit nonzero preview level.", nameof(playerLevel));
        List<ActorStatistic> statistics;
        var sourceActor = formId;
        string? notice = null;
        string name;
        if (!creature && _npcs.TryGetValue(formId, out var npc))
        {
            name = npc.FullName ?? npc.EditorId ?? Name(formId);
            statistics = NpcStatistics(npc);
        }
        else if (creature && _creatures.TryGetValue(formId, out var actor))
        {
            name = actor.FullName ?? actor.EditorId ?? Name(formId);
            statistics = CreatureStatistics(actor);
        }
        else return null;

        var groups = Enum.GetValues<ActorTemplateGroup>()
            .Select(group => _templates.Resolve(formId, creature, group, playerLevel)).ToArray();
        var inventoryGroup = groups.Single(group => group.Group == ActorTemplateGroup.UseInventory);
        List<InventoryItem> inventory = [];
        if (inventoryGroup.Status == "UnsupportedGame")
        {
            // Keep the existing local declarations usable without applying FNV inheritance to another engine.
            inventory = creature ? _creatures[formId].Inventory : _npcs[formId].Inventory;
        }
        else if (inventoryGroup.IsResolved && inventoryGroup.SourceActor is { } resolvedActor)
        {
            sourceActor = resolvedActor;
            inventory = creature ? _creatures[sourceActor].Inventory : _npcs[sourceActor].Inventory;
        }
        else notice = inventoryGroup.Status switch
        {
            "TemplateCycle" => "TemplateCycle",
            "Truncated" => "Truncated",
            _ => "TemplateUnresolved"
        };

        var effective = EffectiveStatistics(groups, creature);
        ActorInspection Enrich(ActorInspection inspection) => inspection with
        {
            Game = _records.Game,
            TemplateChain = groups.SelectMany(g => g.Chain).DistinctBy(h => h.FormId).ToArray(),
            TemplateGroups = groups,
            EffectiveStatistics = effective,
            Masters = _identity.Masters,
            IsPartialCapture = _identity.IsPartialCapture,
            TemplateSemantics = _records.Game == BethesdaGame.Unknown
                ? "FO3/FNV layout assumed; input game identity is unknown"
                : _records.Game is BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas ? "FO3/FNV" : "UnsupportedGame",
            Calculated =
            [
                new("RuntimeHealth", "NotComputed", creature
                    ? "Static DATA health is reported separately; difficulty, active effects and scripts are not evaluated."
                    : "The engine-derived health formula and runtime modifiers are not evaluated."),
                new("RuntimeDamage", "NotComputed", "Attack, difficulty, target resistance, perks and active effects require runtime state."),
                new("EffectiveLevel", "NotComputed", "A PC-level multiplier is not evaluated; a fixed authored Level remains a static field.")
            ]
        };

        if (seed is { } selectedSeed)
        {
            var index = Volatile.Read(ref _generationIndex);
            if (index is null)
            {
                var created = new ActorInventoryIndex(_records, cancellationToken);
                index = Interlocked.CompareExchange(ref _generationIndex, created, null) ?? created;
            }
            var generator = new ActorInventoryGenerator(_records, _lists, index, formId, sourceActor,
                playerLevel!.Value, selectedSeed, cancellationToken);
            var generated = generator.Generate(inventory, notice);
            return Enrich(new ActorInspection(formId, name, creature ? "CREA" : "NPC_", statistics,
                generated.Rows, generated.Notice) { Generation = generated.Generation });
        }

        var rows = new List<ActorInventoryEntry>();
        foreach (var item in inventory)
        {
            if (rows.Count >= MaximumRows) { notice = "Truncated"; break; }
            Expand(item.ItemFormId, item.Count, sourceActor, 0, playerLevel, null,
                sourceActor == formId ? "Authored" : "Inherited", item, [], rows, ref notice);
        }
        return Enrich(new ActorInspection(formId, name, creature ? "CREA" : "NPC_", statistics, rows, notice));
    }

    /// <summary>Collects local NPC declarations without replacing them with inherited values.</summary>
    private List<ActorStatistic> NpcStatistics(NpcRecord npc)
    {
        var values = new List<ActorStatistic>();
        AddBaseStats(values, npc.Stats);
        Add(values, "BaseHealth", npc.BaseHealth);
        Add(values, "Height", npc.Height);
        Add(values, "Weight", npc.Weight);
        AddReference(values, "Race", npc.Race);
        AddReference(values, "Class", npc.Class);
        AddReference(values, "Voice", npc.VoiceType);
        AddReference(values, "Template", npc.Template);
        AddReference(values, "CombatStyle", npc.CombatStyleFormId);
        AddAttributes(values, npc.SpecialStats);
        return values;
    }

    /// <summary>Collects local creature DATA and ACBS fields; absent health stays absent.</summary>
    private List<ActorStatistic> CreatureStatistics(CreatureRecord actor)
    {
        var values = new List<ActorStatistic>();
        AddBaseStats(values, actor.Stats);
        Add(values, "CreatureType", actor.CreatureType);
        Add(values, "CombatSkill", actor.CombatSkill);
        Add(values, "MagicSkill", actor.MagicSkill);
        Add(values, "StealthSkill", actor.StealthSkill);
        Add(values, "Health", actor.Health);
        Add(values, "AttackDamage", actor.AttackDamage);
        Add(values, "Scale", actor.BaseScale);
        AddReference(values, "Template", actor.Template);
        AddReference(values, "Voice", actor.VoiceType);
        AddAttributes(values, actor.Attributes);
        return values;
    }

    private static void AddAttributes(List<ActorStatistic> values, byte[]? attributes)
    {
        if (attributes is null) return;
        for (var i = 0; i < Math.Min(AttributeNames.Length, attributes.Length); i++)
            Add(values, AttributeNames[i], attributes[i]);
    }

    /// <summary>Reports only field groups whose mapping is established; all other groups retain chain status.</summary>
    private List<ActorEffectiveStatistic> EffectiveStatistics(IReadOnlyList<ActorTemplateGroupResolution> groups, bool creature)
    {
        var result = new List<ActorEffectiveStatistic>();
        var statKeys = new[] { "Level", "LevelMultiplier", "LevelEncoded", "MinimumLevel", "MaximumLevel", "Fatigue", "Speed",
            creature ? "Health" : "BaseHealth" }
            .Concat(creature ? ["CombatSkill", "MagicSkill", "StealthSkill", "AttackDamage", "Scale"] : Array.Empty<string>())
            .Concat(AttributeNames).ToHashSet(StringComparer.Ordinal);
        var traitKeys = new HashSet<string>(creature
            ? ["CreatureType", "Karma", "Disposition", "Voice"]
            : ["Karma", "Disposition", "Race", "Class", "Voice"], StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var keys = group.Group switch
            {
                ActorTemplateGroup.UseStats => statKeys,
                ActorTemplateGroup.UseTraits => traitKeys,
                ActorTemplateGroup.UseAIData => new HashSet<string>(["BarterGold"], StringComparer.Ordinal),
                _ => null
            };
            if (keys is null) continue;
            if (!group.IsResolved || group.SourceActor is not { } source)
            {
                foreach (var key in keys.Order(StringComparer.Ordinal))
                    result.Add(new(key, null, "Unresolved", group.Group, null, group.SourcePlugin, Reason: group.Status));
                continue;
            }
            var values = creature ? CreatureStatistics(_creatures[source]) : NpcStatistics(_npcs[source]);
            foreach (var value in values.Where(v => keys.Contains(v.Key)))
                result.Add(new(value.Key, value.Value, group.Status, group.Group, source,
                    group.SourcePlugin, value.Reference));
            var healthKey = creature ? "Health" : "BaseHealth";
            if (group.Group == ActorTemplateGroup.UseStats && values.All(v => v.Key != healthKey))
                result.Add(new(healthKey, null, "Unresolved", group.Group, source, group.SourcePlugin,
                    Reason: _identity.IsPartialCapture ? "FieldNotCaptured" : "FieldAbsent"));
        }
        return result;
    }

    /// <summary>Expands eligible branches with cycle, depth, row-count, and count-overflow bounds.</summary>
    private void Expand(uint itemId, long count, uint source, int depth, ushort? playerLevel,
        ushort? requiredLevel, string status, InventoryItem ownership, HashSet<uint> ancestors,
        List<ActorInventoryEntry> rows, ref string? notice)
    {
        if (rows.Count >= MaximumRows) { notice = "Truncated"; return; }
        var isList = _lists.TryGetValue(itemId, out var list);
        var cycle = isList && ancestors.Contains(itemId);
        rows.Add(new ActorInventoryEntry(itemId, Name(itemId), count, source, depth,
            cycle ? "Cycle" : status, requiredLevel, list?.ChanceNone, list?.Flags, list?.GlobalFormId,
            ownership.OwnerFormId, ownership.ItemCondition));
        if (!isList || cycle || count <= 0) return;
        if (depth >= MaximumDepth) { notice = "Truncated"; return; }
        if (playerLevel is null) { notice ??= "LevelRequired"; return; }
        if (list!.ListType != "LVLI") { notice ??= "TemplateUnresolved"; return; }
        ancestors.Add(itemId);
        var eligible = LeveledItemEligibility.Select(list.Entries, list.Flags, playerLevel.Value);
        foreach (var entry in eligible)
        {
            if (rows.Count >= MaximumRows) { notice = "Truncated"; break; }
            if (count > long.MaxValue / entry.Count) { notice = "CountOverflow"; continue; }
            Expand(entry.FormId, count * entry.Count, source, depth + 1, playerLevel, entry.Level,
                "Candidate", ownership, ancestors, rows, ref notice);
        }
        ancestors.Remove(itemId);
    }

    /// <summary>Returns a source-supplied item name or its explicit unresolved FormID.</summary>
    private string Name(uint formId) => _records.FormIdToDisplayName.GetValueOrDefault(formId)
        ?? _records.FormIdToEditorId.GetValueOrDefault(formId) ?? $"0x{formId:X8}";

    /// <summary>Appends a present scalar using invariant formatting for GUI and CLI consumers.</summary>
    private static void Add(List<ActorStatistic> statistics, string key, object? value)
    {
        if (value is not null) statistics.Add(new ActorStatistic(key, Convert.ToString(value, CultureInfo.InvariantCulture)!));
    }

    /// <summary>Appends a record reference with its name while retaining the navigable FormID.</summary>
    private void AddReference(List<ActorStatistic> statistics, string key, uint? value)
    {
        if (value is { } formId) statistics.Add(new ActorStatistic(key, Name(formId), formId));
    }

    /// <summary>Copies authored ACBS values without claiming calculated game-time statistics.</summary>
    private void AddBaseStats(List<ActorStatistic> statistics, ActorBaseSubrecord? stats)
    {
        if (stats is null) return;
        Add(statistics, "Flags", $"0x{stats.Flags:X8}");
        // FO3/FNV bit 0x80, proven by TESNPC::CopyFromTemplateForm at 0x824461A0.
        // Do not reuse the legacy cross-game ActorBaseFlags table (its labels differ).
        var usesFnvLayout = _records.Game is BethesdaGame.Unknown or BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas;
        var multiplier = usesFnvLayout && (stats.Flags & 0x80) != 0;
        // xEdit FNV NPC_/CREA ACBS: signed itS16 with wbDiv(1000). This is stored encoding, not spawn-level evaluation.
        Add(statistics, multiplier ? "LevelMultiplier" : "Level",
            multiplier ? stats.Level / 1000m : stats.Level);
        if (multiplier) Add(statistics, "LevelEncoded", stats.Level);
        Add(statistics, "MinimumLevel", stats.CalcMin);
        Add(statistics, "MaximumLevel", stats.CalcMax);
        Add(statistics, "Fatigue", stats.FatigueBase);
        Add(statistics, "BarterGold", stats.BarterGold);
        Add(statistics, "Speed", stats.SpeedMultiplier);
        Add(statistics, "Karma", stats.KarmaAlignment);
        Add(statistics, "Disposition", stats.DispositionBase);
        Add(statistics, "TemplateFlags", $"0x{stats.TemplateFlags:X4}");
        if (usesFnvLayout)
            Add(statistics, "TemplateFlagNames", FlagRegistry.DecodeFlagNamesWithHex(stats.TemplateFlags, FlagRegistry.TemplateUseFlags));
    }
}

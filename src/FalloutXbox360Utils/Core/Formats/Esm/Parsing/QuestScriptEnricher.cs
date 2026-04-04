using System.Diagnostics;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Enriches quest records with script variables, related NPCs from dialogue speakers,
///     and builds runtime object-to-script cross-reference mappings.
///     Extracted from RecordParser.ParseAll.
/// </summary>
internal static class QuestScriptEnricher
{
    /// <summary>
    ///     Build runtime object-to-script mappings for DMP cross-reference chains.
    ///     In memory dumps, ESM records are freed at load time so the ESM-based
    ///     BuildCrossReferenceChains finds nothing. Runtime struct readers extract
    ///     Script FormIDs from C++ object pointers (NPC_, CREA, CONT, ACTI, DOOR, FURN) instead.
    /// </summary>
    public static void BuildRuntimeScriptMappings(
        RecordParserContext context,
        ScriptRecordHandler scripts,
        List<NpcRecord> npcs,
        List<CreatureRecord> creatures,
        List<ContainerRecord> containers,
        List<ActivatorRecord> activators,
        List<DoorRecord> doors,
        List<FurnitureRecord> furniture)
    {
        if (context.RuntimeReader == null)
        {
            return;
        }

        var runtimeObjectToScript = new Dictionary<uint, uint>();
        foreach (var npc in npcs.Where(n => n.Script is > 0))
        {
            runtimeObjectToScript.TryAdd(npc.FormId, npc.Script!.Value);
        }

        foreach (var creature in creatures.Where(c => c.Script is > 0))
        {
            runtimeObjectToScript.TryAdd(creature.FormId, creature.Script!.Value);
        }

        foreach (var container in containers.Where(c => c.Script is > 0))
        {
            runtimeObjectToScript.TryAdd(container.FormId, container.Script!.Value);
        }

        foreach (var activator in activators.Where(a => a.Script is > 0))
        {
            runtimeObjectToScript.TryAdd(activator.FormId, activator.Script!.Value);
        }

        foreach (var door in doors.Where(d => d.Script is > 0))
        {
            runtimeObjectToScript.TryAdd(door.FormId, door.Script!.Value);
        }

        foreach (var furn in furniture.Where(f => f.Script is > 0))
        {
            runtimeObjectToScript.TryAdd(furn.FormId, furn.Script!.Value);
        }

        if (runtimeObjectToScript.Count > 0)
        {
            scripts.SetRuntimeObjectScriptMappings(runtimeObjectToScript);
            Logger.Instance.Debug(
                $"  [Semantic] Runtime obj->script: {runtimeObjectToScript.Count} mappings " +
                $"(NPCs: {npcs.Count(n => n.Script is > 0)}, " +
                $"Creatures: {creatures.Count(c => c.Script is > 0)}, " +
                $"Containers: {containers.Count(c => c.Script is > 0)}, " +
                $"Activators: {activators.Count(a => a.Script is > 0)}, " +
                $"Doors: {doors.Count(d => d.Script is > 0)}, " +
                $"Furniture: {furniture.Count(f => f.Script is > 0)})");
        }
    }

    /// <summary>
    ///     Enrich quests with PathwayD backfill, script variables, and related NPCs from dialogue.
    /// </summary>
    public static void EnrichQuests(
        RecordParserContext context,
        List<QuestRecord> quests,
        List<ScriptRecord> scripts,
        List<DialogueRecord> dialogues,
        Stopwatch phaseSw)
    {
        phaseSw.Restart();

        // PathwayD: backfill quests discoverable only through script OwnerQuestFormId
        var questFormIdSet = new HashSet<uint>(quests.Select(q => q.FormId));
        var pathwayDCount = 0;
        foreach (var script in scripts)
        {
            if (script.OwnerQuestFormId is > 0 && !questFormIdSet.Contains(script.OwnerQuestFormId.Value))
            {
                var qfid = script.OwnerQuestFormId.Value;
                quests.Add(new QuestRecord
                {
                    FormId = qfid,
                    EditorId = context.GetEditorId(qfid),
                    FullName = context.FormIdToFullName.GetValueOrDefault(qfid),
                    Offset = 0,
                    IsBigEndian = true
                });
                questFormIdSet.Add(qfid);
                pathwayDCount++;
            }
        }

        // Build script lookups for variable cross-referencing
        var scriptByFormId = scripts.ToDictionary(s => s.FormId, s => s);
        var scriptByOwnerQuest = new Dictionary<uint, ScriptRecord>();
        foreach (var script in scripts)
        {
            if (script.OwnerQuestFormId is > 0)
            {
                scriptByOwnerQuest.TryAdd(script.OwnerQuestFormId.Value, script);
            }
        }

        // Collect related NPCs from dialogue speaker attribution
        var questToSpeakers = new Dictionary<uint, HashSet<uint>>();
        foreach (var dialogue in dialogues)
        {
            if (dialogue.QuestFormId is > 0 && dialogue.SpeakerFormId is > 0)
            {
                if (!questToSpeakers.TryGetValue(dialogue.QuestFormId.Value, out var speakers))
                {
                    speakers = [];
                    questToSpeakers[dialogue.QuestFormId.Value] = speakers;
                }

                speakers.Add(dialogue.SpeakerFormId.Value);
            }
        }

        // Enrich quests with variables and related NPCs
        var variablesLinked = 0;
        var npcsLinked = 0;
        for (var i = 0; i < quests.Count; i++)
        {
            var quest = quests[i];
            List<ScriptVariableInfo>? variables = null;
            List<uint>? relatedNpcs = null;

            // Variables: try direct path (SCRI -> SCPT), then reverse (OwnerQuestFormId)
            if (quest.Script is > 0 && scriptByFormId.TryGetValue(quest.Script.Value, out var directScript)
                                    && directScript.Variables.Count > 0)
            {
                variables = directScript.Variables;
            }
            else if (scriptByOwnerQuest.TryGetValue(quest.FormId, out var ownerScript)
                     && ownerScript.Variables.Count > 0)
            {
                variables = ownerScript.Variables;
            }

            // Related NPCs from dialogue speakers
            if (questToSpeakers.TryGetValue(quest.FormId, out var speakerSet) && speakerSet.Count > 0)
            {
                relatedNpcs = speakerSet.ToList();
            }

            if (variables != null || relatedNpcs != null)
            {
                quests[i] = quest with
                {
                    Variables = variables ?? quest.Variables,
                    RelatedNpcFormIds = relatedNpcs ?? quest.RelatedNpcFormIds
                };
                if (variables != null)
                {
                    variablesLinked++;
                }

                if (relatedNpcs != null)
                {
                    npcsLinked++;
                }
            }
        }

        Logger.Instance.Debug(
            $"  [Semantic] Quest enrichment: {phaseSw.Elapsed} " +
            $"(PathwayD: {pathwayDCount}, Variables: {variablesLinked}, Related NPCs: {npcsLinked})");
    }
}

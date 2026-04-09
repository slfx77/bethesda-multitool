using System.Globalization;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Aggregates record data from multiple memory dumps for cross-dump comparison.
///     Processes dumps sequentially, formatting records and indexing by FormID.
/// </summary>
internal static class CrossDumpAggregator
{
    /// <summary>
    ///     Aggregate record data from multiple dumps into a cross-dump index.
    ///     Uses PE TimeDateStamp from game module for build dates when available.
    /// </summary>
    internal static CrossDumpRecordIndex Aggregate(
        List<(string FilePath, RecordCollection Records, FormIdResolver Resolver, MinidumpInfo? Info)> dumps)
    {
        var index = new CrossDumpRecordIndex();

        // Sort by build date (PE timestamp) falling back to file date
        var ordered = dumps
            .Select(d =>
            {
                var fi = new FileInfo(d.FilePath);
                var fileDate = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;

                // Use PE timestamp from game module if available
                var buildDate = fileDate;
                if (d.Info != null)
                {
                    var gameModule = d.Info.FindGameModule();
                    if (gameModule != null && gameModule.TimeDateStamp != 0)
                    {
                        buildDate = DateTimeOffset.FromUnixTimeSeconds(gameModule.TimeDateStamp).UtcDateTime;
                    }
                }

                var shortName = Path.GetFileNameWithoutExtension(d.FilePath);
                var isDmp = d.FilePath.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase);
                return (d.FilePath, d.Records, d.Resolver, Date: buildDate, ShortName: shortName, IsDmp: isDmp);
            })
            .OrderBy(d => d.Date)
            .ToList();

        // Canonical labels: FormID → display label.
        // Ensures all dialogue from the same NPC/quest shares one group label
        // even if the display name changed between builds.
        var speakerLabels = new Dictionary<uint, string>();
        var questLabels = new Dictionary<uint, string>();
        // Canonical worldspace labels: cells from the same worldspace should all use
        // the same label even if early DMPs resolve the worldspace name differently
        // than later ESMs (e.g., WastelandNVOLD vs WastelandNV for the same FormID).
        var worldspaceLabels = new Dictionary<uint, string>();
        // Track cells whose group was set by an ESM (authoritative). DMP cell records
        // may have a misread WorldspaceFormId from runtime struct parsing, so they
        // should not overwrite ESM-sourced grouping.
        var cellGroupFromEsm = new HashSet<uint>();

        for (var dumpIdx = 0; dumpIdx < ordered.Count; dumpIdx++)
        {
            var dump = ordered[dumpIdx];
            index.Dumps.Add(new DumpSnapshot(
                Path.GetFileName(dump.FilePath),
                dump.Date,
                dump.ShortName,
                dump.IsDmp));

            // Build reverse indexes once per dump for enriched reports
            var factionMembers = dump.Records.BuildFactionMembersIndex();
            var keyLockedDoors = dump.Records.BuildKeyToLockedDoorsMap();
            var modToWeapon = dump.Records.BuildModToWeaponMap();

            foreach (var (typeName, formId, _, _, record) in
                     RecordTextFormatter.EnumerateAll(dump.Records))
            {
                // Build structured report (primary path for all output formats)
                var report = RecordTextFormatter.BuildReport(record, dump.Resolver,
                    factionMembers, keyLockedDoors, modToWeapon);
                if (report == null) continue;

                if (!index.StructuredRecords.TryGetValue(typeName, out var structFormIdMap))
                {
                    structFormIdMap = new Dictionary<uint, Dictionary<int, RecordReport>>();
                    index.StructuredRecords[typeName] = structFormIdMap;
                }

                if (!structFormIdMap.TryGetValue(formId, out var structDumpMap))
                {
                    structDumpMap = new Dictionary<int, RecordReport>();
                    structFormIdMap[formId] = structDumpMap;
                }

                structDumpMap[dumpIdx] = report;

                // Compute group key and store grid coords for cells
                if (record is CellRecord c)
                {
                    if (!index.RecordGroups.TryGetValue(typeName, out var gm))
                    {
                        gm = new Dictionary<uint, string>();
                        index.RecordGroups[typeName] = gm;
                    }

                    // Group key is based on worldspace FormID (or "Interior" / "Unknown").
                    // Use placeholder "WS:0xFORMID" during aggregation, then replace with
                    // resolved names in a finalization pass. Track the best name per FormID.
                    string newGroupKey;
                    if (c.IsInterior)
                    {
                        newGroupKey = "Interior Cells";
                    }
                    else if (c.WorldspaceFormId.HasValue)
                    {
                        var wsFid = c.WorldspaceFormId.Value;
                        newGroupKey = $"WS:0x{wsFid:X8}";

                        // Track the best display label for this worldspace FormID.
                        // Prefer display names over EditorIDs; later dumps overwrite earlier.
                        // Filter out resolver placeholder strings like "(none)".
                        var wsDisplayName = dump.Resolver.ResolveDisplayName(wsFid);
                        var wsEditorId = dump.Resolver.ResolveEditorId(wsFid);
                        var bestName = PickRealName(wsDisplayName, wsEditorId);
                        if (bestName != null)
                        {
                            // Only overwrite if we don't already have a real name,
                            // or if the new name is also real (let later dumps win)
                            worldspaceLabels[wsFid] = bestName;
                        }
                    }
                    else
                    {
                        newGroupKey = "Exterior Cells (Unknown Worldspace)";
                    }

                    if (!gm.TryGetValue(formId, out var existingGroup))
                    {
                        gm[formId] = newGroupKey;
                        if (!dump.IsDmp) cellGroupFromEsm.Add(formId);
                    }
                    else if (!dump.IsDmp)
                    {
                        // ESMs are authoritative — overwrite any DMP-sourced group.
                        // (DMP cell parser may misread WorldspaceFormId from runtime structs.)
                        gm[formId] = newGroupKey;
                        cellGroupFromEsm.Add(formId);
                    }
                    else if (!cellGroupFromEsm.Contains(formId))
                    {
                        // DMP→DMP upgrade: only allow Interior or Unknown→worldspace
                        if (c.IsInterior && existingGroup != "Interior Cells")
                        {
                            gm[formId] = "Interior Cells";
                        }
                        else if (existingGroup == "Exterior Cells (Unknown Worldspace)"
                                 && c.WorldspaceFormId.HasValue)
                        {
                            gm[formId] = newGroupKey;
                        }
                    }

                    // Store grid coordinates for CSS grid tile map (latest wins —
                    // ESM coords are authoritative over DMP-inferred coords)
                    if (c.GridX.HasValue && c.GridY.HasValue)
                    {
                        index.CellGridCoords[formId] = (c.GridX.Value, c.GridY.Value);
                    }

                    // Heightmap storage is handled separately from ESM LAND records
                    // (see DmpCompareCommand after Aggregate call) for complete coverage.
                }

                // Compute dialogue groups (by quest and by NPC) and per-record metadata
                if (record is DialogueRecord d)
                {
                    // Quest groups
                    if (!index.RecordGroups.TryGetValue("Dialogue_Quest", out var questGroups))
                    {
                        questGroups = new Dictionary<uint, string>();
                        index.RecordGroups["Dialogue_Quest"] = questGroups;
                    }

                    if (!questGroups.TryGetValue(formId, out var existingQuestGroup))
                    {
                        questGroups[formId] = d.QuestFormId.HasValue
                            ? ResolveQuestGroupLabel(d.QuestFormId.Value, dump.Resolver, questLabels)
                            : "(No Quest)";
                    }
                    else if (existingQuestGroup == "(No Quest)" && d.QuestFormId.HasValue)
                    {
                        questGroups[formId] =
                            ResolveQuestGroupLabel(d.QuestFormId.Value, dump.Resolver, questLabels);
                    }
                    else if (d.QuestFormId.HasValue)
                    {
                        // Update canonical label with better name resolution from later dumps
                        ResolveQuestGroupLabel(d.QuestFormId.Value, dump.Resolver, questLabels);
                    }

                    // NPC groups — use a speaker FormID-keyed canonical name so all
                    // dialogue lines from the same NPC share a single group label
                    // even if the NPC's display name changed between builds.
                    if (!index.RecordGroups.TryGetValue("Dialogue_NPC", out var npcGroups))
                    {
                        npcGroups = new Dictionary<uint, string>();
                        index.RecordGroups["Dialogue_NPC"] = npcGroups;
                    }

                    // Insert on first sighting; upgrade "(No Speaker)" if a later dump
                    // provides speaker or quest attribution.
                    var hasExistingNpcGroup = npcGroups.TryGetValue(formId, out var existingNpcGroup);
                    var canUpgradeNoSpeaker = hasExistingNpcGroup
                        && existingNpcGroup == "(No Speaker)"
                        && (d.SpeakerFormId.HasValue || d.QuestFormId.HasValue);
                    if (!hasExistingNpcGroup || canUpgradeNoSpeaker)
                    {
                        npcGroups[formId] = ResolveSpeakerGroupLabel(d, dump.Resolver, speakerLabels);
                    }

                    // Per-record metadata for table columns — update on each dump
                    // so later dumps can fill in missing quest/topic/speaker info
                    if (!index.RecordMetadata.TryGetValue(typeName, out var metaMap))
                    {
                        metaMap = new Dictionary<uint, Dictionary<string, string>>();
                        index.RecordMetadata[typeName] = metaMap;
                    }

                    if (!metaMap.TryGetValue(formId, out var meta))
                    {
                        meta = new Dictionary<string, string>();
                        metaMap[formId] = meta;
                    }

                    // Update metadata from each dump — latest wins for best name resolution.
                    // Also tracks name changes across builds for history display.
                    if (d.QuestFormId.HasValue)
                    {
                        meta["questFormId"] = $"0x{d.QuestFormId.Value:X8}";
                        var qEid = dump.Resolver.GetEditorId(d.QuestFormId.Value) ?? "";
                        var qName = dump.Resolver.GetDisplayName(d.QuestFormId.Value) ?? "";
                        if (!string.IsNullOrEmpty(qEid)) meta["questEditorId"] = qEid;
                        if (!string.IsNullOrEmpty(qName)) meta["questName"] = qName;
                    }

                    if (d.TopicFormId.HasValue)
                    {
                        meta["topicFormId"] = $"0x{d.TopicFormId.Value:X8}";
                        var tEid = dump.Resolver.GetEditorId(d.TopicFormId.Value) ?? "";
                        var tName = dump.Resolver.GetDisplayName(d.TopicFormId.Value) ?? "";
                        if (!string.IsNullOrEmpty(tEid)) meta["topicEditorId"] = tEid;
                        if (!string.IsNullOrEmpty(tName)) meta["topicName"] = tName;
                    }

                    if (d.SpeakerFormId.HasValue)
                    {
                        meta["speakerFormId"] = $"0x{d.SpeakerFormId.Value:X8}";
                        var sEid = dump.Resolver.GetEditorId(d.SpeakerFormId.Value) ?? "";
                        var sName = dump.Resolver.GetDisplayName(d.SpeakerFormId.Value) ?? "";
                        if (!string.IsNullOrEmpty(sEid)) meta["speakerEditorId"] = sEid;
                        if (!string.IsNullOrEmpty(sName)) meta["speakerName"] = sName;
                    }
                }
            }
        }

        // Finalize: replace cell group placeholder keys ("WS:0xFORMID") with the
        // best-resolved worldspace name. All cells from the same worldspace FormID
        // will end up with the same group label, regardless of which dump's resolver
        // first saw them. Falls back to "Worldspace 0xFORMID" if no name was resolved.
        if (index.RecordGroups.TryGetValue("Cell", out var cellGroups))
        {
            foreach (var (cellFormId, currentKey) in cellGroups.ToList())
            {
                if (currentKey.StartsWith("WS:0x", StringComparison.Ordinal))
                {
                    var hex = currentKey[5..];
                    if (uint.TryParse(hex, NumberStyles.HexNumber, null, out var wsFid))
                    {
                        cellGroups[cellFormId] = worldspaceLabels.TryGetValue(wsFid, out var label)
                            ? label
                            : $"Worldspace 0x{wsFid:X8}";
                    }
                }
            }
        }

        // Finalize: sync all dialogue group labels to their canonical versions.
        // Speaker/quest labels may have been upgraded by later dumps, but earlier
        // dialogue records still reference the old label string.
        if (index.RecordGroups.TryGetValue("Dialogue_NPC", out var finalNpcGroups))
        {
            // Build reverse map: old label → canonical label
            var speakerFormIdToLabel = speakerLabels;
            foreach (var (dialogueFormId, currentLabel) in finalNpcGroups.ToList())
            {
                // Find the speaker FormID for this dialogue line from metadata
                if (index.RecordMetadata.TryGetValue("Dialogue", out var metas)
                    && metas.TryGetValue(dialogueFormId, out var m)
                    && m.TryGetValue("speakerFormId", out var spkHex)
                    && uint.TryParse(spkHex.Replace("0x", ""), NumberStyles.HexNumber,
                        null, out var spkFid)
                    && speakerFormIdToLabel.TryGetValue(spkFid, out var canonicalLabel)
                    && canonicalLabel != currentLabel)
                {
                    finalNpcGroups[dialogueFormId] = canonicalLabel;
                }
            }
        }

        if (index.RecordGroups.TryGetValue("Dialogue_Quest", out var finalQuestGroups))
        {
            foreach (var (dialogueFormId, currentLabel) in finalQuestGroups.ToList())
            {
                if (index.RecordMetadata.TryGetValue("Dialogue", out var metas)
                    && metas.TryGetValue(dialogueFormId, out var m)
                    && m.TryGetValue("questFormId", out var qstHex)
                    && uint.TryParse(qstHex.Replace("0x", ""), NumberStyles.HexNumber,
                        null, out var qstFid)
                    && questLabels.TryGetValue(qstFid, out var canonicalLabel)
                    && canonicalLabel != currentLabel)
                {
                    finalQuestGroups[dialogueFormId] = canonicalLabel;
                }
            }
        }

        return index;
    }

    /// <summary>
    ///     Returns true if a resolver-returned name is a real name (not null, empty,
    ///     or a placeholder like "(none)").
    /// </summary>
    private static bool IsRealName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name == "(none)") return false;
        return true;
    }

    /// <summary>
    ///     Resolve a quest group label using canonical quest labels.
    ///     Tracks name history when a quest's display name changes across builds.
    /// </summary>
    private static string ResolveQuestGroupLabel(
        uint questFormId, FormIdResolver resolver, Dictionary<uint, string> questLabels)
    {
        var questEditorId = resolver.GetEditorId(questFormId);
        var questDisplayName = resolver.GetDisplayName(questFormId);

        if (!questLabels.TryGetValue(questFormId, out var existingLabel))
        {
            var label = BuildQuestLabel(questFormId, questDisplayName, questEditorId);
            questLabels[questFormId] = label;
            return label;
        }

        // Update label with better resolution from later dumps
        if (!string.IsNullOrEmpty(questDisplayName) && !existingLabel.Contains(" \u2192 "))
        {
            var betterLabel = $"{questDisplayName} ({questEditorId ?? Fmt.FIdAlways(questFormId)})";
            if (betterLabel != existingLabel)
            {
                // Only record name history if the OLD label also had a display name
                // (contained parens = "Name (EditorID)" format). If it was EditorID-only,
                // this is just better resolution, not a real name change.
                var hadDisplayName = existingLabel.Contains('(');
                if (hadDisplayName)
                {
                    var parenIdx = existingLabel.LastIndexOf('(');
                    var oldName = existingLabel[..parenIdx].TrimEnd();
                    if (oldName != questDisplayName)
                    {
                        questLabels[questFormId] =
                            $"{questDisplayName} \u2192 {oldName} ({questEditorId ?? Fmt.FIdAlways(questFormId)})";
                    }
                    else
                    {
                        questLabels[questFormId] = betterLabel;
                    }
                }
                else
                {
                    // Silent upgrade from EditorID-only to Name (EditorID)
                    questLabels[questFormId] = betterLabel;
                }
            }
        }

        return questLabels[questFormId];
    }

    /// <summary>
    ///     Resolve the NPC group label for a dialogue record, using canonical speaker labels
    ///     or falling back to quest for speaker-less generic dialogue.
    ///     Updates the speaker label if a better name is resolved in a later dump.
    /// </summary>
    private static string ResolveSpeakerGroupLabel(
        DialogueRecord d, FormIdResolver resolver, Dictionary<uint, string> speakerLabels)
    {
        if (d.SpeakerFormId.HasValue)
        {
            // Always try to improve the speaker label with better name resolution
            var speakerName = resolver.GetBestName(d.SpeakerFormId.Value);
            var speakerEditorId = resolver.GetEditorId(d.SpeakerFormId.Value);

            if (!speakerLabels.TryGetValue(d.SpeakerFormId.Value, out var existingLabel))
            {
                // First time — create label
                var label = BuildSpeakerLabel(d.SpeakerFormId.Value, speakerName, speakerEditorId);
                speakerLabels[d.SpeakerFormId.Value] = label;
                return label;
            }

            // Update label with better resolution from later dumps
            if (!string.IsNullOrEmpty(speakerName))
            {
                var betterLabel = $"{speakerName} (0x{d.SpeakerFormId.Value:X8})";
                if (betterLabel != existingLabel && !existingLabel.Contains(" \u2192 "))
                {
                    var oldName = existingLabel.Replace($" (0x{d.SpeakerFormId.Value:X8})", "");
                    // Only record history if the old name was a real display name,
                    // not an EditorID fallback or a FormID-only placeholder like "NPC 0x..."
                    var oldWasFallback = string.Equals(oldName, speakerEditorId,
                                             StringComparison.OrdinalIgnoreCase)
                                         || oldName.StartsWith("NPC 0x", StringComparison.Ordinal);
                    if (!oldWasFallback && oldName != speakerName)
                    {
                        speakerLabels[d.SpeakerFormId.Value] =
                            $"{speakerName} \u2192 {oldName} (0x{d.SpeakerFormId.Value:X8})";
                    }
                    else
                    {
                        // Silent upgrade: EditorID → display name, or same name
                        speakerLabels[d.SpeakerFormId.Value] = betterLabel;
                    }
                }
            }

            return speakerLabels[d.SpeakerFormId.Value];
        }

        if (d.QuestFormId.HasValue)
        {
            var questName = resolver.GetBestName(d.QuestFormId.Value);
            var questEditorId = resolver.GetEditorId(d.QuestFormId.Value);
            var questLabel = PickRealName(questName, questEditorId);
            return questLabel != null ? $"{questLabel} (quest)" : $"Quest 0x{d.QuestFormId.Value:X8}";
        }

        return "(No Speaker)";
    }

    private static string? PickRealName(string? displayName, string? editorId)
    {
        if (IsRealName(displayName))
        {
            return displayName;
        }

        return IsRealName(editorId) ? editorId : null;
    }

    private static string BuildQuestLabel(uint questFormId, string? displayName, string? editorId)
    {
        if (!string.IsNullOrEmpty(displayName))
        {
            return $"{displayName} ({editorId ?? Fmt.FIdAlways(questFormId)})";
        }

        return !string.IsNullOrEmpty(editorId) ? editorId : $"Quest 0x{questFormId:X8}";
    }

    private static string BuildSpeakerLabel(uint speakerFormId, string? displayName, string? editorId)
    {
        if (!string.IsNullOrEmpty(displayName))
        {
            return $"{displayName} (0x{speakerFormId:X8})";
        }

        return !string.IsNullOrEmpty(editorId)
            ? $"{editorId} (0x{speakerFormId:X8})"
            : $"NPC 0x{speakerFormId:X8}";
    }
}

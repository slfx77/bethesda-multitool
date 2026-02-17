namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Pure comparison logic for DMP-vs-ESM dialogue tree verification.
///     No I/O or UI dependencies — operates entirely on DialogueTreeResult data.
/// </summary>
internal static class DialogueVerifier
{
    /// <summary>
    ///     Summary statistics from comparing two dialogue trees.
    /// </summary>
    public record VerificationResult
    {
        public int TopicsMatched { get; init; }
        public int TopicsMissing { get; init; }
        public int TopicsExtra { get; init; }
        public int InfosMatched { get; init; }
        public int InfosMissing { get; init; }
        public int InfosExtra { get; init; }
        public int FlowMatches { get; init; }
        public int FlowMismatches { get; init; }
        public int ResponseTextMatches { get; init; }
        public int ResponseTextMissing { get; init; }
        public int AddTopicMatches { get; init; }
        public int AddTopicMismatches { get; init; }
        public int SaidOnceCount { get; init; }
        public int TotalDmpInfos { get; init; }
        public List<TopicDiff> TopicDiffs { get; init; } = [];
    }

    /// <summary>
    ///     A single difference found between DMP and ESM dialogue trees.
    /// </summary>
    public record TopicDiff(uint TopicFormId, string? TopicName, string DiffType, string Detail);

    /// <summary>
    ///     Compare a DMP dialogue tree against an ESM reference tree.
    ///     Returns statistics on matches, mismatches, and coverage.
    /// </summary>
    public static VerificationResult Compare(
        DialogueTreeResult dmpTree, DialogueTreeResult esmTree,
        uint? questFilter = null)
    {
        var dmpTopics = CollectAllTopics(dmpTree);
        var esmTopics = CollectAllTopics(esmTree);

        if (questFilter.HasValue)
        {
            dmpTopics = FilterByQuest(dmpTopics, dmpTree, questFilter.Value);
            esmTopics = FilterByQuest(esmTopics, esmTree, questFilter.Value);
        }

        var diffs = new List<TopicDiff>();
        int topicsMatched = 0, topicsMissing = 0, topicsExtra = 0;
        int infosMatched = 0, infosMissing = 0, infosExtra = 0;
        int flowMatches = 0, flowMismatches = 0;
        int responseTextMatches = 0, responseTextMissing = 0;
        int addTopicMatches = 0, addTopicMismatches = 0;
        int saidOnceCount = 0, totalDmpInfos = 0;

        // Count SaidOnce across all DMP INFOs
        foreach (var (_, dmpTopic) in dmpTopics)
        {
            foreach (var info in dmpTopic.InfoChain)
            {
                totalDmpInfos++;
                if (info.Info.SaidOnce)
                {
                    saidOnceCount++;
                }
            }
        }

        // Check each ESM topic against DMP
        foreach (var (esmFormId, esmTopic) in esmTopics)
        {
            if (!dmpTopics.TryGetValue(esmFormId, out var dmpTopic))
            {
                topicsMissing++;
                diffs.Add(new TopicDiff(esmFormId, esmTopic.TopicName, "Missing",
                    $"ESM topic not found in DMP ({esmTopic.InfoChain.Count} INFOs)"));
                infosMissing += esmTopic.InfoChain.Count;
                continue;
            }

            topicsMatched++;

            // Compare INFOs within topic
            var esmInfoIds = new HashSet<uint>(esmTopic.InfoChain.Select(i => i.Info.FormId));
            var dmpInfoIds = new HashSet<uint>(dmpTopic.InfoChain.Select(i => i.Info.FormId));
            var dmpInfoByFormId = dmpTopic.InfoChain.ToDictionary(i => i.Info.FormId);

            var sharedInfos = esmInfoIds.Intersect(dmpInfoIds).ToList();
            var missingInfos = esmInfoIds.Except(dmpInfoIds).ToList();
            var extraInfos = dmpInfoIds.Except(esmInfoIds).ToList();

            infosMatched += sharedInfos.Count;
            infosMissing += missingInfos.Count;
            infosExtra += extraInfos.Count;

            if (missingInfos.Count > 0)
            {
                diffs.Add(new TopicDiff(esmFormId, esmTopic.TopicName, "MissingINFOs",
                    $"{missingInfos.Count} INFOs in ESM but not DMP: " +
                    string.Join(", ", missingInfos.Take(5).Select(id => $"0x{id:X8}"))));
            }

            if (extraInfos.Count > 0)
            {
                diffs.Add(new TopicDiff(esmFormId, esmTopic.TopicName, "ExtraINFOs",
                    $"{extraInfos.Count} INFOs in DMP but not ESM: " +
                    string.Join(", ", extraInfos.Take(5).Select(id => $"0x{id:X8}"))));
            }

            // Compare TCLT flow links for each shared INFO
            foreach (var esmInfo in esmTopic.InfoChain)
            {
                if (!dmpInfoByFormId.TryGetValue(esmInfo.Info.FormId, out var dmpInfo))
                {
                    continue;
                }

                // TCLT comparison
                var esmTclt = new HashSet<uint>(esmInfo.Info.LinkToTopics);
                var dmpTclt = new HashSet<uint>(dmpInfo.Info.LinkToTopics);
                if (esmTclt.SetEquals(dmpTclt))
                {
                    flowMatches++;
                }
                else
                {
                    flowMismatches++;
                    var missing = esmTclt.Except(dmpTclt).ToList();
                    var extra = dmpTclt.Except(esmTclt).ToList();
                    var detail = "";
                    if (missing.Count > 0)
                    {
                        detail += $"missing TCLT: {string.Join(", ", missing.Select(id => $"0x{id:X8}"))}";
                    }

                    if (extra.Count > 0)
                    {
                        detail += (detail.Length > 0 ? "; " : "") +
                                  $"extra TCLT: {string.Join(", ", extra.Select(id => $"0x{id:X8}"))}";
                    }

                    diffs.Add(new TopicDiff(esmFormId, esmTopic.TopicName, "FlowMismatch",
                        $"INFO 0x{esmInfo.Info.FormId:X8}: {detail}"));
                }

                // NAME/AddTopics comparison
                var esmAdd = new HashSet<uint>(esmInfo.Info.AddTopics);
                var dmpAdd = new HashSet<uint>(dmpInfo.Info.AddTopics);
                if (esmAdd.SetEquals(dmpAdd))
                {
                    addTopicMatches++;
                }
                else if (esmAdd.Count > 0 || dmpAdd.Count > 0)
                {
                    addTopicMismatches++;
                }

                // Response text comparison
                var esmHasText = esmInfo.Info.Responses.Any(r => !string.IsNullOrEmpty(r.Text));
                var dmpHasText = dmpInfo.Info.Responses.Any(r => !string.IsNullOrEmpty(r.Text));
                if (esmHasText && dmpHasText)
                {
                    responseTextMatches++;
                }
                else if (esmHasText && !dmpHasText)
                {
                    responseTextMissing++;
                }
            }
        }

        // Check for DMP topics not in ESM
        foreach (var (dmpFormId, dmpTopic) in dmpTopics)
        {
            if (!esmTopics.ContainsKey(dmpFormId))
            {
                topicsExtra++;
                diffs.Add(new TopicDiff(dmpFormId, dmpTopic.TopicName, "Extra",
                    $"DMP topic not found in ESM ({dmpTopic.InfoChain.Count} INFOs)"));
            }
        }

        return new VerificationResult
        {
            TopicsMatched = topicsMatched,
            TopicsMissing = topicsMissing,
            TopicsExtra = topicsExtra,
            InfosMatched = infosMatched,
            InfosMissing = infosMissing,
            InfosExtra = infosExtra,
            FlowMatches = flowMatches,
            FlowMismatches = flowMismatches,
            ResponseTextMatches = responseTextMatches,
            ResponseTextMissing = responseTextMissing,
            AddTopicMatches = addTopicMatches,
            AddTopicMismatches = addTopicMismatches,
            SaidOnceCount = saidOnceCount,
            TotalDmpInfos = totalDmpInfos,
            TopicDiffs = diffs
        };
    }

    /// <summary>
    ///     Collect all topics from a DialogueTreeResult into a flat FormID-keyed dictionary.
    /// </summary>
    private static Dictionary<uint, TopicDialogueNode> CollectAllTopics(DialogueTreeResult tree)
    {
        var result = new Dictionary<uint, TopicDialogueNode>();

        foreach (var (_, quest) in tree.QuestTrees)
        {
            foreach (var topic in quest.Topics)
            {
                result.TryAdd(topic.TopicFormId, topic);
            }
        }

        foreach (var topic in tree.OrphanTopics)
        {
            result.TryAdd(topic.TopicFormId, topic);
        }

        return result;
    }

    /// <summary>
    ///     Filter topics to only those belonging to a specific quest.
    /// </summary>
    private static Dictionary<uint, TopicDialogueNode> FilterByQuest(
        Dictionary<uint, TopicDialogueNode> topics, DialogueTreeResult tree, uint questFormId)
    {
        if (!tree.QuestTrees.TryGetValue(questFormId, out var quest))
        {
            return new Dictionary<uint, TopicDialogueNode>();
        }

        var result = new Dictionary<uint, TopicDialogueNode>();
        foreach (var topic in quest.Topics)
        {
            if (topics.ContainsKey(topic.TopicFormId))
            {
                result[topic.TopicFormId] = topic;
            }
        }

        return result;
    }
}

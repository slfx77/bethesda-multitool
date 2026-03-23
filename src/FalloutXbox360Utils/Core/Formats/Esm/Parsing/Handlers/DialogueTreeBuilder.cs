using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Builds hierarchical dialogue trees: Quest -> Topic -> INFO chains with cross-topic links.
///     Uses TopicFormId, QuestFormId, and linking subrecords (TCLT/TCLF/AddTopics) to build
///     a navigable tree structure.
/// </summary>
internal sealed class DialogueTreeBuilder(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;

    /// <summary>
    ///     Build hierarchical dialogue trees from dialogue, topic, and quest records.
    /// </summary>
    internal DialogueTreeResult BuildDialogueTrees(
        List<DialogueRecord> dialogues,
        List<DialogTopicRecord> topics,
        List<QuestRecord> quests)
    {
        // Build indices
        var (infosByTopic, unlinkedInfos) = BuildInfosByTopicIndex(dialogues);
        var topicById = topics
            .GroupBy(t => t.FormId)
            .ToDictionary(g => g.Key, g => g.First());
        var questById = quests
            .GroupBy(q => q.FormId)
            .ToDictionary(g => g.Key, g => g.First());

        // Sort INFOs within each topic by InfoIndex
        foreach (var (_, infos) in infosByTopic)
        {
            infos.Sort((a, b) => a.InfoIndex.CompareTo(b.InfoIndex));
        }

        // Build TopicDialogueNode for each known topic
        var topicNodes = CreateTopicDialogueNodes(infosByTopic, topics, topicById);

        // Cross-link: fill in ChoiceTopics (TCLT) and AddedTopics (NAME) for each InfoDialogueNode
        CrossLinkInfoNodes(topicNodes);

        // Reverse-link: use TCLF (link-FROM) subrecords to infer missing TCLT connections
        ReverseLinkFromTclf(topicNodes);

        // Group topics by quest
        var (questTrees, orphanTopics) = GroupTopicsByQuest(topicNodes, questById);

        // Create orphan topic nodes for unlinked INFOs (no TopicFormId)
        CreateOrphanTopicNodes(unlinkedInfos, questTrees, orphanTopics, questById);

        // Sort topics within each quest by priority then name
        SortTopicsWithinQuests(questTrees);

        LogTopicInfoCoverageStats(topicNodes);

        return new DialogueTreeResult
        {
            QuestTrees = questTrees,
            OrphanTopics = orphanTopics
        };
    }

    /// <summary>
    ///     Build an index of dialogues by their TopicFormId.
    /// </summary>
    private static (Dictionary<uint, List<DialogueRecord>> infosByTopic, List<DialogueRecord> unlinkedInfos)
        BuildInfosByTopicIndex(List<DialogueRecord> dialogues)
    {
        var infosByTopic = new Dictionary<uint, List<DialogueRecord>>();
        var unlinkedInfos = new List<DialogueRecord>();

        foreach (var d in dialogues)
        {
            if (d.TopicFormId.HasValue && d.TopicFormId.Value != 0)
            {
                if (!infosByTopic.TryGetValue(d.TopicFormId.Value, out var list))
                {
                    list = [];
                    infosByTopic[d.TopicFormId.Value] = list;
                }

                list.Add(d);
            }
            else
            {
                unlinkedInfos.Add(d);
            }
        }

        return (infosByTopic, unlinkedInfos);
    }

    /// <summary>
    ///     Create TopicDialogueNode for each known topic.
    /// </summary>
    private Dictionary<uint, TopicDialogueNode> CreateTopicDialogueNodes(
        Dictionary<uint, List<DialogueRecord>> infosByTopic,
        List<DialogTopicRecord> topics,
        Dictionary<uint, DialogTopicRecord> topicById)
    {
        var topicNodes = new Dictionary<uint, TopicDialogueNode>();

        // Include all topics that have INFOs or ESM DIAL records
        var allTopicIds = new HashSet<uint>(infosByTopic.Keys);
        foreach (var t in topics)
        {
            allTopicIds.Add(t.FormId);
        }

        foreach (var topicId in allTopicIds)
        {
            topicById.TryGetValue(topicId, out var topic);
            var topicName = topic?.FullName ?? topic?.EditorId ?? _context.ResolveFormName(topicId);

            var infos = infosByTopic.GetValueOrDefault(topicId, []);
            var infoNodes = infos.Select(info => new InfoDialogueNode
            {
                Info = info,
                ChoiceTopics = [],
                AddedTopics = []
            }).ToList();

            topicNodes[topicId] = new TopicDialogueNode
            {
                Topic = topic,
                TopicFormId = topicId,
                TopicName = topicName,
                InfoChain = infoNodes
            };
        }

        return topicNodes;
    }

    /// <summary>
    ///     Cross-link: fill in ChoiceTopics (TCLT) and AddedTopics (NAME) for each InfoDialogueNode.
    /// </summary>
    private static void CrossLinkInfoNodes(Dictionary<uint, TopicDialogueNode> topicNodes)
    {
        foreach (var (_, topicNode) in topicNodes)
        {
            foreach (var infoNode in topicNode.InfoChain)
            {
                foreach (var tcltId in infoNode.Info.LinkToTopics)
                {
                    if (topicNodes.TryGetValue(tcltId, out var choiceNode))
                    {
                        infoNode.ChoiceTopics.Add(choiceNode);
                    }
                }

                foreach (var addId in infoNode.Info.AddTopics)
                {
                    if (topicNodes.TryGetValue(addId, out var addedNode))
                    {
                        infoNode.AddedTopics.Add(addedNode);
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Use TCLF (link-FROM) subrecords to infer missing TCLT choice links.
    ///     TCLF on an INFO says "these source topics can lead to my parent topic."
    ///     We reverse this: for each source topic, add the target topic as a choice
    ///     on its INFOs (if not already present from TCLT).
    /// </summary>
    private static void ReverseLinkFromTclf(Dictionary<uint, TopicDialogueNode> topicNodes)
    {
        // Step 1: Build reverse index -- sourceTopicFormId -> set of targetTopicFormIds
        var reverseIndex = new Dictionary<uint, HashSet<uint>>();

        foreach (var (_, topicNode) in topicNodes)
        {
            foreach (var infoNode in topicNode.InfoChain)
            {
                foreach (var sourceTopicId in infoNode.Info.LinkFromTopics)
                {
                    if (!reverseIndex.TryGetValue(sourceTopicId, out var targets))
                    {
                        targets = [];
                        reverseIndex[sourceTopicId] = targets;
                    }

                    targets.Add(topicNode.TopicFormId);
                }
            }
        }

        if (reverseIndex.Count == 0)
        {
            return;
        }

        // Step 2: For each source topic, add target topics as choices on its INFOs
        foreach (var (sourceTopicId, targetTopicIds) in reverseIndex)
        {
            if (!topicNodes.TryGetValue(sourceTopicId, out var sourceTopicNode))
            {
                continue;
            }

            foreach (var infoNode in sourceTopicNode.InfoChain)
            {
                foreach (var targetTopicId in targetTopicIds)
                {
                    if (!topicNodes.TryGetValue(targetTopicId, out var targetTopicNode))
                    {
                        continue;
                    }

                    // Skip if already linked (from TCLT)
                    var alreadyLinked = false;
                    foreach (var existing in infoNode.ChoiceTopics)
                    {
                        if (existing.TopicFormId == targetTopicId)
                        {
                            alreadyLinked = true;
                            break;
                        }
                    }

                    if (!alreadyLinked)
                    {
                        infoNode.ChoiceTopics.Add(targetTopicNode);
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Group topics by their associated quest.
    /// </summary>
    private (Dictionary<uint, QuestDialogueNode> questTrees, List<TopicDialogueNode> orphanTopics)
        GroupTopicsByQuest(
            Dictionary<uint, TopicDialogueNode> topicNodes,
            Dictionary<uint, QuestRecord> questById)
    {
        var questTrees = new Dictionary<uint, QuestDialogueNode>();
        var orphanTopics = new List<TopicDialogueNode>();

        foreach (var (_, topicNode) in topicNodes)
        {
            var questId = DetermineQuestIdForTopic(topicNode);

            if (questId.HasValue && questId.Value != 0)
            {
                var questNode = GetOrCreateQuestNode(questTrees, questId.Value, questById);
                questNode.Topics.Add(topicNode);
            }
            else
            {
                orphanTopics.Add(topicNode);
            }
        }

        return (questTrees, orphanTopics);
    }

    /// <summary>
    ///     Determine the quest FormId for a topic from its topic record or INFO records.
    /// </summary>
    private static uint? DetermineQuestIdForTopic(TopicDialogueNode topicNode)
    {
        var questId = topicNode.Topic?.QuestFormId;
        if (!questId.HasValue || questId.Value == 0)
        {
            questId = topicNode.InfoChain
                .Select(i => i.Info.QuestFormId)
                .FirstOrDefault(q => q.HasValue && q.Value != 0);
        }

        return questId;
    }

    /// <summary>
    ///     Get or create a QuestDialogueNode for the given quest FormId.
    /// </summary>
    private QuestDialogueNode GetOrCreateQuestNode(
        Dictionary<uint, QuestDialogueNode> questTrees,
        uint questId,
        Dictionary<uint, QuestRecord> questById)
    {
        if (!questTrees.TryGetValue(questId, out var questNode))
        {
            questById.TryGetValue(questId, out var quest);
            questNode = new QuestDialogueNode
            {
                QuestFormId = questId,
                QuestName = quest?.FullName ?? quest?.EditorId ?? _context.ResolveFormName(questId),
                Topics = []
            };
            questTrees[questId] = questNode;
        }

        return questNode;
    }

    /// <summary>
    ///     Create orphan topic nodes for unlinked INFOs (no TopicFormId).
    /// </summary>
    private void CreateOrphanTopicNodes(
        List<DialogueRecord> unlinkedInfos,
        Dictionary<uint, QuestDialogueNode> questTrees,
        List<TopicDialogueNode> orphanTopics,
        Dictionary<uint, QuestRecord> questById)
    {
        if (unlinkedInfos.Count == 0)
        {
            return;
        }

        // Group unlinked INFOs by quest, create synthetic topic nodes
        var unlinkedByQuest = unlinkedInfos
            .GroupBy(d => d.QuestFormId ?? 0)
            .OrderBy(g => g.Key);

        foreach (var group in unlinkedByQuest)
        {
            var syntheticTopic = CreateSyntheticTopicNode(group);

            if (group.Key != 0)
            {
                var questNode = GetOrCreateQuestNode(questTrees, group.Key, questById);
                questNode.Topics.Add(syntheticTopic);
            }
            else
            {
                orphanTopics.Add(syntheticTopic);
            }
        }
    }

    /// <summary>
    ///     Create a synthetic topic node for a group of unlinked INFOs.
    /// </summary>
    private static TopicDialogueNode CreateSyntheticTopicNode(IGrouping<uint, DialogueRecord> group)
    {
        var infoNodes = group
            .OrderBy(d => d.InfoIndex)
            .ThenBy(d => d.EditorId ?? "")
            .Select(info => new InfoDialogueNode
            {
                Info = info,
                ChoiceTopics = [],
                AddedTopics = []
            }).ToList();

        return new TopicDialogueNode
        {
            Topic = null,
            TopicFormId = 0,
            TopicName = "(Unlinked Responses)",
            InfoChain = infoNodes
        };
    }

    /// <summary>
    ///     Sort topics within each quest by priority (descending) then by name.
    /// </summary>
    private static void SortTopicsWithinQuests(Dictionary<uint, QuestDialogueNode> questTrees)
    {
        foreach (var (_, questNode) in questTrees)
        {
            questNode.Topics.Sort((a, b) =>
            {
                var pa = a.Topic?.Priority ?? 0f;
                var pb = b.Topic?.Priority ?? 0f;
                var cmp = pb.CompareTo(pa); // Higher priority first
                return cmp != 0 ? cmp : string.Compare(a.TopicName, b.TopicName, StringComparison.OrdinalIgnoreCase);
            });
        }
    }

    /// <summary>
    ///     Logs diagnostic stats about topics with expected responses but 0 INFOs.
    /// </summary>
    private static void LogTopicInfoCoverageStats(Dictionary<uint, TopicDialogueNode> topicNodes)
    {
        var emptyTopics = topicNodes.Values
            .Where(t => t.InfoChain.Count == 0 && t.Topic is { ResponseCount: > 0 })
            .ToList();

        if (emptyTopics.Count == 0)
        {
            return;
        }

        var totalTopics = topicNodes.Count;
        var totalWithInfos = topicNodes.Values.Count(t => t.InfoChain.Count > 0);

        // Breakdown by FullName (top 10)
        var byName = emptyTopics
            .GroupBy(t => t.Topic?.FullName ?? "(no name)")
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => $"{g.Key} ({g.Count()})")
            .ToList();

        // Breakdown by TopicType
        var byType = emptyTopics
            .GroupBy(t => t.Topic?.TopicTypeName ?? "Unknown")
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}: {g.Count()}")
            .ToList();

        Logger.Instance.Debug(
            $"  [Semantic] Topic INFO coverage: {totalWithInfos}/{totalTopics} topics have INFOs, " +
            $"{emptyTopics.Count} topics with expected responses but 0 INFOs " +
            $"(by name: {string.Join(", ", byName)}) " +
            $"(by type: {string.Join(", ", byType)})");
    }
}

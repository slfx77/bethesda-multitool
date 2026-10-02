using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;

namespace BethesdaMultitool.Core.Semantic.LoadOrder;

/// <summary>Rebuilds a static graph after selection. Captured runtime ordering is not a plugin override rule.</summary>
internal static class SelectedDialogueViewBuilder
{
    internal static DialogueTreeResult Build(RecordCollection records)
    {
        var context = new RecordParserContext(new EsmRecordScanResult { Game = records.Game }, records.FormIdToEditorId);
        var tree = new DialogueTreeBuilder(context).BuildDialogueTrees(records.Dialogues, records.DialogTopics, records.Quests);
        var topicIds = records.DialogTopics.Select(t => t.FormId).ToHashSet();
        var infoIds = records.Dialogues.Select(i => i.FormId).ToHashSet();
        var questIds = records.Quests.Select(q => q.FormId).ToHashSet();
        var actorIds = records.Npcs.Select(n => n.FormId).Concat(records.Creatures.Select(c => c.FormId)).ToHashSet();
        var edges = new List<DialogueGraphEdge>();
        var issues = new List<DialogueOrderingIssue>();
        foreach (var info in records.Dialogues)
        {
            if (info.TopicFormId is > 0)
            {
                Edge(info.TopicFormId.Value, "topic-membership", info.RawParentTopicFormId == info.TopicFormId
                    ? "physical-GRUP" : "typed-association", topicIds);
            }
            if (info.QuestFormId is > 0) { Edge(info.QuestFormId.Value, "quest-membership", info.QuestAttributionSource ?? "typed-association", questIds); }
            if (info.SpeakerFormId is > 0) { Edge(info.SpeakerFormId.Value, "speaker-attribution", info.SpeakerAttributionSource ?? "typed-association", actorIds); }
            if (info.PreviousInfo is > 0) { Edge(info.PreviousInfo.Value, "previous-info", "explicit-PNAM", infoIds); }
            foreach (var target in info.LinkToTopics) { Edge(target, "choice-topic", "explicit-TCLT", topicIds); }
            foreach (var target in info.LinkFromTopics) { Edge(target, "source-topic", "explicit-TCLF", topicIds); }
            foreach (var target in info.AddTopics) { Edge(target, "added-topic", "explicit-NAME", topicIds); }
            foreach (var target in info.FollowUpInfos) { Edge(target, "follow-up-info", "observed-runtime", infoIds); }

            void Edge(uint target, string kind, string evidence, HashSet<uint> included) =>
                edges.Add(new DialogueGraphEdge(info.FormId, target, kind, evidence, included.Contains(target) ? "resolved" : "unresolved"));
        }
        foreach (var topic in tree.QuestTrees.Values.SelectMany(q => q.Topics).Concat(tree.OrphanTopics))
        {
            foreach (var node in topic.InfoChain)
            {
                foreach (var choice in node.ChoiceTopics.Where(c => !node.Info.LinkToTopics.Contains(c.TopicFormId)))
                {
                    edges.Add(new DialogueGraphEdge(node.Info.FormId, choice.TopicFormId, "choice-topic", "inferred-reverse-TCLF",
                        topicIds.Contains(choice.TopicFormId) ? "resolved" : "unresolved")
                    {
                        EvidenceInfoFormIds = choice.InfoChain
                            .Where(target => target.Info.LinkFromTopics.Contains(topic.TopicFormId))
                            .Select(target => target.Info.FormId).Distinct().ToArray()
                    });
                }
            }
            OrderForDisplay(topic, issues);
        }
        return tree with { Edges = edges, OrderingIssues = issues };
    }

    private static void OrderForDisplay(TopicDialogueNode topic, List<DialogueOrderingIssue> issues)
    {
        if (topic.InfoChain.Count == 0) { return; }
        var nodes = topic.InfoChain.ToDictionary(i => i.Info.FormId);
        var successors = new Dictionary<uint, List<uint>>();
        var degree = nodes.Keys.ToDictionary(id => id, _ => 0);
        foreach (var node in topic.InfoChain)
        {
            if (node.Info.PreviousInfo is not > 0) { continue; }
            var previous = node.Info.PreviousInfo.Value;
            if (!nodes.ContainsKey(previous))
            {
                issues.Add(new(topic.TopicFormId, "previous-info-outside-topic-or-unresolved", [node.Info.FormId, previous]));
                continue;
            }
            if (!successors.TryGetValue(previous, out var following)) { successors[previous] = following = []; }
            following.Add(node.Info.FormId);
            degree[node.Info.FormId]++;
        }
        foreach (var (previous, next) in successors.Where(p => p.Value.Count > 1))
        {
            issues.Add(new(topic.TopicFormId, "predecessor-fork", new[] { previous }.Concat(next).ToArray()));
        }
        var ready = new SortedSet<uint>(degree.Where(p => p.Value == 0).Select(p => p.Key));
        if (ready.Count > 1) { issues.Add(new(topic.TopicFormId, "unconstrained-display-order", ready.ToArray())); }
        var ordered = new List<InfoDialogueNode>(nodes.Count);
        while (ready.Count > 0)
        {
            var id = ready.Min;
            ready.Remove(id);
            ordered.Add(nodes[id]);
            if (!successors.TryGetValue(id, out var following)) { continue; }
            foreach (var next in following)
            {
                degree[next]--;
                if (degree[next] == 0) { ready.Add(next); }
            }
        }
        var visited = ordered.Select(n => n.Info.FormId).ToHashSet();
        var cyclic = nodes.Keys.Where(id => !visited.Contains(id)).Order().ToArray();
        if (cyclic.Length > 0)
        {
            issues.Add(new(topic.TopicFormId, "predecessor-cycle-or-dependent-on-cycle", cyclic));
            ordered.AddRange(cyclic.Select(id => nodes[id]));
        }
        topic.InfoChain.Clear();
        topic.InfoChain.AddRange(ordered);
    }
}

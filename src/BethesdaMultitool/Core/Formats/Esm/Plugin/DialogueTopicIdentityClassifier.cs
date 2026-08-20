using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin;

internal enum DialogueTopicIdentityKind
{
    MasterAnchor,
    SharedChildAnchor,
    PrototypeDistinct,
    Ambiguous
}

internal sealed record DialogueTopicIdentity(
    uint PrototypeDialFormId,
    DialogueTopicIdentityKind Kind,
    uint? MasterDialFormId,
    IReadOnlyList<uint> EvidenceInfoFormIds,
    string Reason)
{
    /// <summary>True when raw type-7 ancestry named this DIAL but no DIAL record was captured.</summary>
    public bool IsOrphanRawParentClaim { get; init; }

    /// <summary>Deterministic structural conflicts that prevented a stronger classification.</summary>
    public IReadOnlyList<string> Conflicts { get; init; } = [];
}

/// <summary>
///     Classifies DIAL identity from structural evidence only. EDID, FULL, prompt, and response
///     text are deliberately excluded: the prototype corpus reuses those values across unrelated
///     player choices. Classification is diagnostic in this slice and never rewrites output.
/// </summary>
internal static class DialogueTopicIdentityClassifier
{
    public static IReadOnlyList<DialogueTopicIdentity> Classify(
        IReadOnlyList<DialogTopicRecord> topics,
        IReadOnlyList<DialogueRecord> infos,
        NewVsOverrideClassifier classifier,
        MasterDialogueIndex masterIndex)
    {
        var results = new Dictionary<uint, DialogueTopicIdentity>();
        var claimsByMasterDial = new Dictionary<uint, List<uint>>();
        var sharedInfosByClaimedTopic = BuildSharedInfoIndex(infos, classifier);
        var rawInfosByClaimedTopic = BuildRawParentInfoIndex(infos);
        var topicScopes = topics
            .Where(static topic => topic.FormId != 0)
            .GroupBy(static topic => topic.FormId)
            .Select(BuildPrototypeTopicScope)
            .OrderBy(static scope => scope.FormId)
            .ToList();
        var capturedTopicIds = topicScopes.Select(static scope => scope.FormId).ToHashSet();

        foreach (var scope in topicScopes)
        {
            sharedInfosByClaimedTopic.TryGetValue(scope.FormId, out var relevantSharedInfos);
            relevantSharedInfos ??= [];
            var evidenceIds = EvidenceInfoIds(relevantSharedInfos);
            var hasExactMasterTopic = masterIndex.TryGetTopic(scope.FormId, out _);
            if (scope.Conflicts.Count > 0)
            {
                var masterDial = ResolveCandidateMasterDial(
                    scope.FormId, relevantSharedInfos, masterIndex);
                results[scope.FormId] = Ambiguous(
                    scope.FormId,
                    masterDial,
                    evidenceIds,
                    "Duplicate captures disagree on the prototype DIAL structural scope.",
                    scope.Conflicts);
                if (hasExactMasterTopic)
                {
                    RegisterClaim(claimsByMasterDial, scope.FormId, scope.FormId);
                }

                continue;
            }

            if (hasExactMasterTopic)
            {
                var exactInfoConflicts = DescribeDuplicateInfoScopeConflicts(relevantSharedInfos);
                exactInfoConflicts.AddRange(DescribeInfoEvidenceConflicts(
                    scope.FormId, relevantSharedInfos, false));
                results[scope.FormId] = new DialogueTopicIdentity(
                    scope.FormId,
                    DialogueTopicIdentityKind.MasterAnchor,
                    scope.FormId,
                    evidenceIds,
                    exactInfoConflicts.Count == 0
                        ? "Exact DIAL FormID exists in the retail master."
                        : "Exact DIAL FormID exists in the retail master, but shared INFO structural evidence conflicts.")
                {
                    Conflicts = exactInfoConflicts
                };
                RegisterClaim(claimsByMasterDial, scope.FormId, scope.FormId);
                continue;
            }

            if (classifier.IsOverride(scope.FormId))
            {
                results[scope.FormId] = Ambiguous(
                    scope.FormId, null, evidenceIds,
                    "FormID collides with a retail record that is not a DIAL.");
                continue;
            }

            if (relevantSharedInfos.Count == 0)
            {
                results[scope.FormId] = Distinct(scope.FormId,
                    "No exact shared INFO child provides a structural retail anchor.");
                continue;
            }

            var infoEvidenceConflicts = DescribeDuplicateInfoScopeConflicts(relevantSharedInfos);
            infoEvidenceConflicts.AddRange(DescribeInfoEvidenceConflicts(
                scope.FormId, relevantSharedInfos));
            if (infoEvidenceConflicts.Count > 0)
            {
                results[scope.FormId] = Ambiguous(
                    scope.FormId, null, evidenceIds,
                    "A shared INFO lacks one conflict-free raw type-7 parent for this prototype DIAL.",
                    infoEvidenceConflicts);
                continue;
            }

            var masterInfos = new List<(DialogueRecord Prototype, MasterDialogueInfo Master)>();
            var missingMasterInfo = false;
            foreach (var info in relevantSharedInfos)
            {
                if (!masterIndex.TryGetInfo(info.FormId, out var masterInfo))
                {
                    missingMasterInfo = true;
                    break;
                }

                masterInfos.Add((info, masterInfo));
            }

            if (missingMasterInfo)
            {
                results[scope.FormId] = Ambiguous(
                    scope.FormId, null, evidenceIds,
                    "A same-FormID child is not an INFO in the retail dialogue index.");
                continue;
            }

            var parentIds = masterInfos
                .Select(static pair => pair.Master.ParentDialFormId)
                .Distinct()
                .Order()
                .ToList();
            if (parentIds.Count != 1)
            {
                results[scope.FormId] = Ambiguous(
                    scope.FormId, null, evidenceIds,
                    "Shared INFO children map to more than one retail DIAL parent.",
                    [$"Retail parent candidates: {FormatFormIds(parentIds)}."]);
                continue;
            }

            var masterDialId = parentIds[0];
            if (!masterIndex.TryGetTopic(masterDialId, out var masterTopic))
            {
                results[scope.FormId] = Ambiguous(
                    scope.FormId, masterDialId, evidenceIds,
                    "The structural retail parent DIAL is unavailable.");
                continue;
            }

            if (!QuestScopeMatches(scope, masterTopic))
            {
                results[scope.FormId] = Ambiguous(
                    scope.FormId, masterDialId, evidenceIds,
                    "Prototype and retail DIAL quest scopes do not match exactly.");
                continue;
            }

            if (masterTopic.TopicType is not { } masterTopicType
                || scope.TopicTypes.Single() != masterTopicType)
            {
                results[scope.FormId] = Ambiguous(
                    scope.FormId, masterDialId, evidenceIds,
                    "Prototype and retail DIAL topic types do not match exactly.");
                continue;
            }

            if (!TopicSpeakerScopeMatches(scope, masterTopic))
            {
                results[scope.FormId] = Ambiguous(
                    scope.FormId, masterDialId, evidenceIds,
                    "Prototype and retail DIAL TNAM hard-speaker scopes do not match exactly.");
                continue;
            }

            if (masterInfos.Any(static pair => !SpeakerScopeMatches(pair.Prototype, pair.Master)))
            {
                results[scope.FormId] = Ambiguous(
                    scope.FormId, masterDialId, evidenceIds,
                    "Prototype and retail shared INFO hard-speaker scopes are incompatible.");
                continue;
            }

            results[scope.FormId] = new DialogueTopicIdentity(
                scope.FormId,
                DialogueTopicIdentityKind.SharedChildAnchor,
                masterDialId,
                evidenceIds,
                "All exact shared INFO children identify one compatible retail DIAL parent.");
            RegisterClaim(claimsByMasterDial, masterDialId, scope.FormId);
        }

        // Raw type-7 ancestry is still identity evidence when the corresponding DIAL record
        // was not captured. Exact retail labels remain exact anchors; every other orphan is
        // fail-closed because its quest/type/TNAM scope cannot be compared.
        foreach (var (rawParent, rawInfos) in rawInfosByClaimedTopic
                     .Where(pair => !capturedTopicIds.Contains(pair.Key))
                     .OrderBy(static pair => pair.Key))
        {
            var evidenceIds = EvidenceInfoIds(rawInfos);
            DialogueTopicIdentity identity;
            if (masterIndex.TryGetTopic(rawParent, out _))
            {
                identity = new DialogueTopicIdentity(
                    rawParent,
                    DialogueTopicIdentityKind.MasterAnchor,
                    rawParent,
                    evidenceIds,
                    "Raw type-7 ancestry names an exact retail DIAL, but no prototype DIAL record was captured.")
                {
                    IsOrphanRawParentClaim = true
                };
                RegisterClaim(claimsByMasterDial, rawParent, rawParent);
            }
            else if (classifier.IsOverride(rawParent))
            {
                identity = Ambiguous(
                        rawParent,
                        null,
                        evidenceIds,
                        "Raw type-7 ancestry names a retail non-DIAL FormID and no prototype DIAL record was captured.",
                        ["Missing captured DIAL structural scope."]) with
                    {
                        IsOrphanRawParentClaim = true
                    };
            }
            else
            {
                var masterParents = rawInfos
                    .Select(info => masterIndex.TryGetInfo(info.FormId, out var masterInfo)
                        ? masterInfo.ParentDialFormId
                        : 0)
                    .Where(static formId => formId != 0)
                    .Distinct()
                    .Order()
                    .ToList();
                var masterDial = masterParents.Count == 1 ? masterParents[0] : (uint?)null;
                var reason = masterParents.Count switch
                {
                    0 => "Raw type-7 ancestry names an uncaptured prototype DIAL without a shared retail child anchor.",
                    1 =>
                        "Shared INFO ancestry suggests one retail DIAL, but the prototype DIAL scope was not captured.",
                    _ =>
                        "Shared INFO ancestry suggests multiple retail DIALs and the prototype DIAL scope was not captured."
                };
                var conflicts = new List<string> { "Missing captured DIAL structural scope." };
                if (masterParents.Count > 1)
                {
                    conflicts.Add($"Retail parent candidates: {FormatFormIds(masterParents)}.");
                }

                identity = Ambiguous(
                        rawParent, masterDial, evidenceIds, reason, conflicts) with
                    {
                        IsOrphanRawParentClaim = true
                    };
            }

            results[rawParent] = identity;
        }

        // An exact master anchor is canonical and remains certain. Any structural alias that
        // competes with it (or with another structural alias) is ambiguous until tree-level
        // evidence can distinguish the captures.
        foreach (var (masterDialId, rawClaimants) in claimsByMasterDial)
        {
            var claimants = rawClaimants.Distinct().Order().ToList();
            if (claimants.Count <= 1)
            {
                continue;
            }

            foreach (var claimant in claimants)
            {
                var prior = results[claimant];
                if (prior.Kind != DialogueTopicIdentityKind.SharedChildAnchor)
                {
                    continue;
                }

                var conflict = $"Competing prototype DIAL claims for retail 0x{masterDialId:X8}: " +
                               $"{FormatFormIds(claimants)}.";
                results[claimant] = prior with
                {
                    Kind = DialogueTopicIdentityKind.Ambiguous,
                    Conflicts = prior.Conflicts.Concat([conflict]).ToList(),
                    Reason = "Multiple prototype DIAL claims compete for the same retail structural anchor."
                };
            }
        }

        return results.Values.OrderBy(static result => result.PrototypeDialFormId).ToList();
    }

    private static PrototypeTopicScope BuildPrototypeTopicScope(
        IGrouping<uint, DialogTopicRecord> captures)
    {
        var materialized = captures.ToList();
        var quests = materialized
            .Select(static topic => topic.QuestFormId.GetValueOrDefault())
            .Where(static formId => formId != 0)
            .ToHashSet();
        var observedMissingQuest = materialized.Any(static topic => topic.QuestFormId is null or 0);
        var topicTypes = materialized.Select(static topic => topic.TopicType).ToHashSet();
        var speakers = materialized
            .Select(static topic => topic.SpeakerFormId.GetValueOrDefault())
            .Where(static formId => formId != 0)
            .ToHashSet();
        var observedMissingSpeaker = materialized.Any(static topic => topic.SpeakerFormId is null or 0);
        var conflicts = new List<string>();
        if (quests.Count > 1)
        {
            conflicts.Add($"Duplicate QSTI scopes: {FormatFormIds(quests)}.");
        }
        else if (quests.Count == 1 && observedMissingQuest)
        {
            conflicts.Add($"Duplicate QSTI scopes disagree on presence: missing,{FormatFormIds(quests)}.");
        }

        if (topicTypes.Count > 1)
        {
            conflicts.Add("Duplicate DATA topic types: " +
                          string.Join(",", topicTypes.Order().Select(static value => value.ToString())) + ".");
        }

        if (speakers.Count > 1)
        {
            conflicts.Add($"Duplicate TNAM hard-speaker scopes: {FormatFormIds(speakers)}.");
        }
        else if (speakers.Count == 1 && observedMissingSpeaker)
        {
            conflicts.Add(
                $"Duplicate TNAM hard-speaker scopes disagree on presence: missing,{FormatFormIds(speakers)}.");
        }

        return new PrototypeTopicScope(captures.Key, quests, topicTypes, speakers, conflicts);
    }

    private static Dictionary<uint, List<DialogueRecord>> BuildSharedInfoIndex(
        IReadOnlyList<DialogueRecord> infos,
        NewVsOverrideClassifier classifier)
    {
        var result = new Dictionary<uint, List<DialogueRecord>>();
        foreach (var captures in infos
                     .Where(info => info.FormId != 0 && classifier.IsOverride(info.FormId))
                     .GroupBy(static info => info.FormId))
        {
            var materialized = captures.ToList();
            var claimedTopics = materialized
                .SelectMany(static info => info.RawParentTopicFormIds
                    .Append(info.TopicFormId.GetValueOrDefault()))
                .Where(static topic => topic != 0)
                .Distinct();
            foreach (var topic in claimedTopics)
            {
                foreach (var info in materialized)
                {
                    AddInfoClaim(result, topic, info);
                }
            }
        }

        return result;
    }

    private static Dictionary<uint, List<DialogueRecord>> BuildRawParentInfoIndex(
        IReadOnlyList<DialogueRecord> infos)
    {
        var result = new Dictionary<uint, List<DialogueRecord>>();
        foreach (var info in infos)
        {
            foreach (var rawParent in info.RawParentTopicFormIds
                         .Where(static formId => formId != 0)
                         .Distinct())
            {
                AddInfoClaim(result, rawParent, info);
            }
        }

        return result;
    }

    private static void AddInfoClaim(
        Dictionary<uint, List<DialogueRecord>> result,
        uint topic,
        DialogueRecord info)
    {
        if (!result.TryGetValue(topic, out var children))
        {
            children = [];
            result[topic] = children;
        }

        children.Add(info);
    }

    private static void RegisterClaim(
        Dictionary<uint, List<uint>> claimsByMasterDial,
        uint masterDial,
        uint prototypeDial)
    {
        if (!claimsByMasterDial.TryGetValue(masterDial, out var claimants))
        {
            claimants = [];
            claimsByMasterDial[masterDial] = claimants;
        }

        claimants.Add(prototypeDial);
    }

    private static uint? ResolveCandidateMasterDial(
        uint prototypeDial,
        IReadOnlyList<DialogueRecord> relevantSharedInfos,
        MasterDialogueIndex masterIndex)
    {
        if (masterIndex.TryGetTopic(prototypeDial, out _))
        {
            return prototypeDial;
        }

        var parents = relevantSharedInfos
            .Select(info => masterIndex.TryGetInfo(info.FormId, out var masterInfo)
                ? masterInfo.ParentDialFormId
                : 0)
            .Where(static formId => formId != 0)
            .Distinct()
            .ToList();
        return parents.Count == 1 ? parents[0] : null;
    }

    private static List<string> DescribeDuplicateInfoScopeConflicts(
        IReadOnlyList<DialogueRecord> infos)
    {
        var conflicts = new List<string>();
        foreach (var captures in infos
                     .GroupBy(static info => info.FormId)
                     .OrderBy(static group => group.Key))
        {
            var captureCount = 0;
            var topicFormIds = new HashSet<uint>();
            var questFormIds = new HashSet<uint>();
            var hardSpeakerFormIds = new HashSet<uint>();
            var observedMissingTopic = false;
            var observedMissingQuest = false;
            var observedMissingHardSpeaker = false;
            foreach (var capture in captures)
            {
                var evidence = GetInfoIdentityScopeEvidence(capture);
                captureCount += evidence.CaptureCount;
                topicFormIds.UnionWith(evidence.TopicFormIds);
                questFormIds.UnionWith(evidence.QuestFormIds);
                hardSpeakerFormIds.UnionWith(evidence.HardSpeakerFormIds);
                observedMissingTopic |= evidence.ObservedMissingTopic;
                observedMissingQuest |= evidence.ObservedMissingQuest;
                observedMissingHardSpeaker |= evidence.ObservedMissingHardSpeaker;
            }

            if (captureCount <= 1)
            {
                continue;
            }

            AddInfoScopeConflict(
                conflicts, captures.Key, "runtime topic", topicFormIds, observedMissingTopic);
            AddInfoScopeConflict(
                conflicts, captures.Key, "quest", questFormIds, observedMissingQuest);
            AddInfoScopeConflict(
                conflicts, captures.Key, "hard-speaker", hardSpeakerFormIds, observedMissingHardSpeaker);
        }

        return conflicts;
    }

    private static InfoIdentityScopeEvidence GetInfoIdentityScopeEvidence(DialogueRecord info)
    {
        if (info.IdentityScopeCaptureCount > 0)
        {
            return new InfoIdentityScopeEvidence(
                info.IdentityScopeCaptureCount,
                info.IdentityScopeTopicFormIds,
                info.IdentityScopeObservedMissingTopic,
                info.IdentityScopeQuestFormIds,
                info.IdentityScopeObservedMissingQuest,
                info.IdentityScopeHardSpeakerFormIds,
                info.IdentityScopeObservedMissingHardSpeaker);
        }

        var topic = info.TopicFormId.GetValueOrDefault();
        var quest = info.QuestFormId.GetValueOrDefault();
        var hardSpeaker = DialogueSpeakerBinding.GetExactSpeaker(info).GetValueOrDefault();
        return new InfoIdentityScopeEvidence(
            1,
            topic == 0 ? [] : [topic],
            topic == 0,
            quest == 0 ? [] : [quest],
            quest == 0,
            hardSpeaker == 0 ? [] : [hardSpeaker],
            hardSpeaker == 0);
    }

    private static void AddInfoScopeConflict(
        List<string> conflicts,
        uint infoFormId,
        string scopeName,
        HashSet<uint> formIds,
        bool observedMissing)
    {
        if (formIds.Count <= 1 && !(formIds.Count == 1 && observedMissing))
        {
            return;
        }

        var values = observedMissing
            ? $"missing,{FormatFormIds(formIds)}"
            : FormatFormIds(formIds);
        conflicts.Add($"Duplicate INFO 0x{infoFormId:X8} {scopeName} scopes disagree: {values}.");
    }

    private static List<string> DescribeInfoEvidenceConflicts(
        uint prototypeDial,
        IReadOnlyList<DialogueRecord> infos,
        bool requireRawParent = true)
    {
        var conflicts = new List<string>();
        foreach (var info in infos.OrderBy(static info => info.FormId))
        {
            if (info.HasAmbiguousRawParentTopic)
            {
                conflicts.Add($"INFO 0x{info.FormId:X8} has raw parents " +
                              $"{FormatFormIds(info.RawParentTopicFormIds)}.");
                continue;
            }

            if (info.RawParentTopicFormId is not { } rawParent)
            {
                if (requireRawParent)
                {
                    conflicts.Add($"INFO 0x{info.FormId:X8} has no unique raw parent.");
                }

                continue;
            }

            if (rawParent != prototypeDial)
            {
                conflicts.Add($"INFO 0x{info.FormId:X8} raw parent 0x{rawParent:X8} " +
                              $"does not match prototype DIAL 0x{prototypeDial:X8}.");
            }

            if (info.HasRawRuntimeTopicConflict)
            {
                conflicts.Add($"INFO 0x{info.FormId:X8} runtime parent " +
                              $"0x{info.TopicFormId.GetValueOrDefault():X8} conflicts with raw ancestry.");
            }
        }

        return conflicts;
    }

    private static List<uint> EvidenceInfoIds(IEnumerable<DialogueRecord> infos)
    {
        return infos.Select(static info => info.FormId)
            .Where(static formId => formId != 0)
            .Distinct()
            .Order()
            .ToList();
    }

    private static bool QuestScopeMatches(PrototypeTopicScope prototype, MasterDialogueTopic master)
    {
        return prototype.QuestFormIds.SetEquals(master.QuestFormIds);
    }

    private static bool TopicSpeakerScopeMatches(
        PrototypeTopicScope prototype,
        MasterDialogueTopic master)
    {
        return prototype.TopicSpeakerFormIds.SetEquals(master.TopicSpeakerFormIds);
    }

    private static bool SpeakerScopeMatches(DialogueRecord prototype, MasterDialogueInfo master)
    {
        var prototypeSpeaker = DialogueSpeakerBinding.GetExactSpeaker(prototype);
        return prototypeSpeaker is { } speaker
            ? master.ExactSpeakerFormIds.SetEquals([speaker])
            : master.ExactSpeakerFormIds.Count == 0;
    }

    private static string FormatFormIds(IEnumerable<uint> formIds)
    {
        return string.Join(",", formIds.Distinct().Order().Select(static formId => $"0x{formId:X8}"));
    }

    private static DialogueTopicIdentity Distinct(uint sourceDial, string reason)
    {
        return new DialogueTopicIdentity(
            sourceDial, DialogueTopicIdentityKind.PrototypeDistinct, null, [], reason);
    }

    private static DialogueTopicIdentity Ambiguous(
        uint sourceDial,
        uint? masterDial,
        IReadOnlyList<uint> evidence,
        string reason,
        IReadOnlyList<string>? conflicts = null)
    {
        return new DialogueTopicIdentity(
            sourceDial, DialogueTopicIdentityKind.Ambiguous, masterDial, evidence, reason)
        {
            Conflicts = conflicts ?? []
        };
    }

    private sealed record PrototypeTopicScope(
        uint FormId,
        IReadOnlySet<uint> QuestFormIds,
        IReadOnlySet<byte> TopicTypes,
        IReadOnlySet<uint> TopicSpeakerFormIds,
        IReadOnlyList<string> Conflicts);

    private sealed record InfoIdentityScopeEvidence(
        int CaptureCount,
        IReadOnlyList<uint> TopicFormIds,
        bool ObservedMissingTopic,
        IReadOnlyList<uint> QuestFormIds,
        bool ObservedMissingQuest,
        IReadOnlyList<uint> HardSpeakerFormIds,
        bool ObservedMissingHardSpeaker);
}

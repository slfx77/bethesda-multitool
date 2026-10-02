namespace BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;

/// <summary>
///     Result of building the full dialogue tree hierarchy from parsed data.
///     Organizes all dialogue into Quest → Topic → INFO chains.
/// </summary>
public record DialogueTreeResult
{
    /// <summary>Quest-level dialogue trees, keyed by quest FormID.</summary>
    public Dictionary<uint, QuestDialogueNode> QuestTrees { get; init; } = new();

    /// <summary>Topics with no identified quest parent.</summary>
    public List<TopicDialogueNode> OrphanTopics { get; init; } = [];

    /// <summary>Structural evidence, including unresolved links; these do not evaluate dialogue conditions.</summary>
    public IReadOnlyList<DialogueGraphEdge> Edges { get; init; } = [];

    /// <summary>Limits of the displayed predecessor-compatible ordering.</summary>
    public IReadOnlyList<DialogueOrderingIssue> OrderingIssues { get; init; } = [];
}

public sealed record DialogueGraphEdge(uint SourceInfoFormId, uint TargetFormId, string Kind,
    string Evidence, string Status)
{
    /// <summary>Other INFO owners supplying this inferred edge, in selected-record order.</summary>
    public IReadOnlyList<uint> EvidenceInfoFormIds { get; init; } = [];
}

public sealed record DialogueOrderingIssue(uint TopicFormId, string Code, IReadOnlyList<uint> InfoFormIds);

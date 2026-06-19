using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;

/// <summary>Which package event a <see cref="PackageEventAction" /> responds to (begin / end / on-change).</summary>
public enum PackageEventActionKind
{
    OnBegin,
    OnEnd,
    OnChange
}

/// <summary>
///     Serialized package event action block: POBA/POEA/POCA marker, INAM idle,
///     inline script block, and TNAM topic.
/// </summary>
public record PackageEventAction
{
    public PackageEventActionKind Kind { get; init; }
    public uint IdleFormId { get; init; }
    public uint TopicFormId { get; init; }
    public List<DialogueResultScript> Scripts { get; init; } = [];
}

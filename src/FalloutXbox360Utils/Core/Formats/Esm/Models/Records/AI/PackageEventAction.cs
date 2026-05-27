using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;

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

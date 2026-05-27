namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;

/// <summary>
///     Dialogue package data from PKDD. Layout matches the PDB PACK_DIALOGUE_DATA struct.
/// </summary>
public record PackageDialogueData
{
    public float Fov { get; init; }
    public uint TopicFormId { get; init; }
    public bool NoHeadtracking { get; init; }
    public bool DoNotControlTarget { get; init; }
    public bool SpeakerMoveTalk { get; init; }
    public float DistanceStartTalking { get; init; }
    public bool SayTo { get; init; }
    public uint TriggerType { get; init; }
}

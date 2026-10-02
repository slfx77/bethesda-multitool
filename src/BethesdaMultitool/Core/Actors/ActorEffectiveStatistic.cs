namespace BethesdaMultitool.Core.Actors;

/// <summary>An inherited static field, explicitly distinct from a calculated in-game actor value.</summary>
internal sealed record ActorEffectiveStatistic(string Key, string? Value, string Provenance,
    ActorTemplateGroup Group, uint? SourceActor, string? SourcePlugin, uint? Reference = null,
    string? Reason = null);

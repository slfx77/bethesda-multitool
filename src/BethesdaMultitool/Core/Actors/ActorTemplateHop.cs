namespace BethesdaMultitool.Core.Actors;

/// <summary>One observed actor in an inheritance trace; null flags mean no ACBS was captured.</summary>
internal sealed record ActorTemplateHop(uint FormId, string Kind, string? EditorId,
    ushort? TemplateFlags, uint? Template, string? SourcePlugin);

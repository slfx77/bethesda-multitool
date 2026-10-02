namespace BethesdaMultitool.Core.Formats.Dialogue.CreationKit;

/// <summary>A diagram edge between diagram node IDs, retained even when an endpoint is absent.</summary>
/// <param name="Id">The diagram-local link identifier.</param>
/// <param name="OriginId">The origin diagram-node ID, or null for an unconnected endpoint.</param>
/// <param name="DestinationId">The destination diagram-node ID, or null for an unconnected endpoint.</param>
/// <param name="Text">The authored link label.</param>
/// <param name="Points">The original route-point text in authored order.</param>
public sealed record DialogueViewLink(string Id, string? OriginId, string? DestinationId, string Text,
    IReadOnlyList<string> Points);

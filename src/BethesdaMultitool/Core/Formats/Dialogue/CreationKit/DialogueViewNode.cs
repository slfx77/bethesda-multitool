namespace BethesdaMultitool.Core.Formats.Dialogue.CreationKit;

/// <summary>An authored diagram node whose record references may be stale or unresolved.</summary>
/// <param name="Id">The diagram-local node identifier, not a plugin FormID.</param>
/// <param name="Kind">The original node class.</param>
/// <param name="Text">The authored labels and cell text.</param>
/// <param name="ToolTip">The unmodified tooltip text.</param>
/// <param name="Bounds">The authored position and size, when present.</param>
/// <param name="FormIds">Potential record references parsed from tooltips without load-order rebasing.</param>
public sealed record DialogueViewNode(string Id, string Kind, string Text, string ToolTip,
    DialogueViewBounds? Bounds, IReadOnlyList<uint> FormIds);

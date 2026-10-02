using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Actors;

/// <summary>A bounded static resolution, or an explicit reason no effective value can be chosen.</summary>
internal sealed record ActorTemplateGroupResolution(ActorTemplateGroup Group, string Status,
    uint? SourceActor, string? SourcePlugin, uint? UnresolvedFormId,
    IReadOnlyList<ActorTemplateHop> Chain, IReadOnlyList<LeveledEntry> Candidates, string? Detail = null)
{
    /// <summary>True only when a concrete actor supplied the selected group.</summary>
    internal bool IsResolved => Status is "Authored" or "Inherited";
}

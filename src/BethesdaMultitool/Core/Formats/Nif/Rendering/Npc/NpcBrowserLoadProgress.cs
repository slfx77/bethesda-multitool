namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;

/// <summary>CPU/I/O stages that prepare the Actors browser before any actor scene is selected.</summary>
internal enum NpcBrowserLoadStage
{
    DiscoveringArchives,
    ReadingEsm,
    ReusingRecordIndex,
    DecodingAppearanceRecords,
    IndexingMeshArchives,
    IndexingTextureArchives,
    Complete
}

/// <summary>
///     A start or completion notification for one Actors-browser preparation stage. A null
///     <see cref="Elapsed" /> marks stage start; a value marks completion and is also written to the
///     diagnostics log.
/// </summary>
internal sealed record NpcBrowserLoadProgress(
    NpcBrowserLoadStage Stage,
    string Message,
    TimeSpan? Elapsed = null,
    string? Detail = null);

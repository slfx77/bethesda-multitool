namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>The preview-pane surface a selected browser node is routed to by <see cref="AssetPreviewRouting" />.</summary>
internal enum AssetPreviewSurface
{
    /// <summary>Nothing to show: the placeholder and a "no preview" status.</summary>
    None,

    /// <summary>A texture or sprite, decoded at full size for the image preview control.</summary>
    Image,

    /// <summary>A Daggerfall sky set, shown as its backdrop frame.</summary>
    Sky,

    /// <summary>Shadowkey's models.huge, shown through the pack presenter.</summary>
    ShadowkeyPack,

    /// <summary>An admitted FLC/CEL movie, played through the native media preview.</summary>
    NativeVideo,

    /// <summary>Any other playable movie, played through the frame-timer preview and its transport.</summary>
    LegacyVideo,

    /// <summary>A playable sound, handed to the native audio panel.</summary>
    Audio,

    /// <summary>Not settled by name: ask the mesh and level probes, which read the file head.</summary>
    ContentProbe
}

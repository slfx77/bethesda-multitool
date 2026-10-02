namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     What the preview pane shows for a picture asset: every decoded frame at full size, the
///     description a caption quotes, or a decline reason when the asset was decodable but is not shown.
/// </summary>
/// <param name="Frames">Every frame in decoder order; empty when declined.</param>
/// <param name="Info">The first frame's stored size and the frame count; null when nothing was decoded.</param>
/// <param name="DeclineReasonKey">A localization key naming why the frames are withheld, or null when they are shown.</param>
internal sealed record AssetImagePreview(IReadOnlyList<AssetImageFrame> Frames, AssetImageInfo? Info, string? DeclineReasonKey)
{
    /// <summary>Whether the frames are withheld; <see cref="DeclineReasonKey" /> then says why.</summary>
    internal bool IsDeclined => DeclineReasonKey is not null;

    /// <summary>A preview with no frames and a reason, optionally describing the asset that was declined.</summary>
    /// <param name="key">The localization key of the decline message.</param>
    /// <param name="info">The declined asset's description, when it was decoded far enough to have one.</param>
    internal static AssetImagePreview Declined(string key, AssetImageInfo? info = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return new AssetImagePreview(Array.Empty<AssetImageFrame>(), info, key);
    }
}

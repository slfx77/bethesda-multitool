using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Concurrency;
using BethesdaMultitool.Core.Imaging;
using BethesdaMultitool.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Slfx77.Multitool.WinUI.Localization;

namespace BethesdaMultitool;

/// <summary>
///     Routes one selected node to exactly one preview surface and owns the full-size image surface.
///     <para>
///         <see cref="AssetPreviewRouting" /> makes the byte-free decision; the existing Show* methods
///         still run for every selection because each retires its own surface and carries the lifetime
///         rules (window-close gate, native hosts, deferred mesh viewer). Only a
///         <see cref="AssetPreviewSurface.ContentProbe" /> node reaches the mesh and level probes, which
///         read a file head.
///     </para>
/// </summary>
public sealed partial class AssetBrowserTab
{
    private readonly LatestOnlyJob _imageLoad = new();
    private AssetImagePreview? _imagePreview;
    private byte[][]? _imageFramesBgra;
    private int _imageFrameIndex;
    private bool _updatingImageFrame;
    private bool _mirroringTreeSelection;
    private AssetNode? _previewNode;

    /// <summary>The single entry point: shows <paramref name="node" /> on its surface and retires every other one.</summary>
    /// <param name="node">The selected current-source node; a folder shows the placeholder.</param>
    private void ShowPreview(AssetNode node)
    {
        _previewNode = node;
        var surface = AssetPreviewRouting.Select(_sourceSnapshot, node, _shadowkeyPresenter is not null);
        AssetPreviewPlaceholder.Visibility = Visibility.Collapsed;
        SetPreviewStatus(string.Empty);
        if (!SelectShadowkeyPack(node))
        {
            if (surface == AssetPreviewSurface.ContentProbe) ShowMeshOrLevel(node);
            else HideMesh();
        }

        ShowAudio(node);
        ShowVideo(node);
        ShowSky(node);
        if (surface == AssetPreviewSurface.Image) ShowImage(node);
        else HideImage();
        if (surface == AssetPreviewSurface.None) ShowNoPreview(node);
    }

    /// <summary>
    ///     Decodes every frame of a picture node at full size off the UI thread and shows frame zero.
    ///     <para>
    ///         The worker converts each frame to premultiplied BGRA (thread-agnostic); only the
    ///         dispatcher builds a bitmap, and only for the frame on screen. Frames stay resident for
    ///         the selected node alone; <see cref="AssetImagePreviewSource.MaximumFramePixels" /> caps
    ///         what is decoded at all.
    ///     </para>
    /// </summary>
    /// <param name="node">A node <see cref="AssetThumbnailSource.CanRender" /> admits.</param>
    private void ShowImage(AssetNode node)
    {
        var session = _session;
        if (session is null)
        {
            HideImage();
            return;
        }

        _imageLoad.Cancel();
        _imagePreview = null;
        _imageFramesBgra = null;
        _imageFrameIndex = 0;
        ImageFramePanel.Visibility = Visibility.Collapsed;
        ImagePreview.Clear();
        ImagePreview.SetLoading(true);
        ImagePreview.Visibility = Visibility.Visible;
        RuntimeLocalization.SetText(AssetPreviewStatusText, "AssetPreview_Decoding", node.Name);
        _ = RunPreviewAsync(_imageLoad,
            token => DecodeImagePreview(session, node, token),
            decoded =>
            {
                if (_disposed || !ReferenceEquals(_previewNode, node)) return;
                PresentImage(node, decoded.Preview, decoded.FramesBgra);
            });
    }

    /// <summary>Decodes the node and premultiplies every frame on the worker.</summary>
    /// <param name="session">The opened source the node belongs to.</param>
    /// <param name="node">The selected picture node.</param>
    /// <param name="token">The job's cancellation, checked between frames.</param>
    /// <returns>The decoded preview (null when the node has no picture) and its premultiplied frames.</returns>
    private static (AssetImagePreview? Preview, byte[][]? FramesBgra) DecodeImagePreview(
        AssetBrowseSession session, AssetNode node, CancellationToken token)
    {
        var preview = AssetImagePreviewSource.TryLoad(session, node, token);
        if (preview is null || preview.Frames.Count == 0)
        {
            return (preview, null);
        }

        var frames = new byte[preview.Frames.Count][];
        for (var i = 0; i < frames.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            frames[i] = PremultipliedBgra.FromRgba(preview.Frames[i].Rgba);
        }

        return (preview, frames);
    }

    /// <summary>Adopts a decoded result on the dispatcher: frames, a decline, or nothing to show.</summary>
    /// <param name="node">The node the result belongs to.</param>
    /// <param name="preview">The decoded preview, or null when the bytes were not a picture.</param>
    /// <param name="frames">The premultiplied frames, or null when nothing was decoded.</param>
    private void PresentImage(AssetNode node, AssetImagePreview? preview, byte[][]? frames)
    {
        if (preview is null || frames is null || frames.Length == 0)
        {
            HideImage();
            if (preview is { DeclineReasonKey: { } key, Info: { } declined })
            {
                AssetPreviewPlaceholder.Visibility = Visibility.Visible;
                RuntimeLocalization.SetText(AssetPreviewPlaceholder, key, node.Name, declined.Width, declined.Height);
                SetPreviewStatus(string.Empty);
                return;
            }

            ShowNoPreview(node);
            return;
        }

        _imagePreview = preview;
        _imageFramesBgra = frames;
        _updatingImageFrame = true;
        try
        {
            ImageFrame.Maximum = frames.Length - 1;
            ImageFrame.Value = 0;
        }
        finally { _updatingImageFrame = false; }
        ImageFramePanel.Visibility = frames.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        ShowImageFrame(0);
    }

    /// <summary>Builds the bitmap for one retained frame and describes it in the status line.</summary>
    /// <param name="index">The zero-based frame index within the retained node.</param>
    private void ShowImageFrame(int index)
    {
        if (_imagePreview is not { } preview || _imageFramesBgra is not { } frames || (uint)index >= (uint)frames.Length)
        {
            return;
        }

        _imageFrameIndex = index;
        var frame = preview.Frames[index];
        ImagePreview.SetSource(AssetBitmapFactory.FromPremultipliedBgra(frame.Width, frame.Height, frames[index]));
        ImagePreview.Visibility = Visibility.Visible;
        if (frames.Length > 1)
        {
            RuntimeLocalization.SetText(AssetPreviewStatusText, "AssetPreview_ImageFrames", frame.Width, frame.Height,
                index, frames.Length);
        }
        else
        {
            RuntimeLocalization.SetText(AssetPreviewStatusText, "AssetPreview_ImageFormat", frame.Width, frame.Height);
        }
    }

    /// <summary>Shows the whole-number frame the user chose, or restores the box to the frame on screen.</summary>
    /// <param name="sender">The frame box.</param>
    /// <param name="args">The committed value.</param>
    private void ImageFrame_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingImageFrame || _disposed || _imageFramesBgra is not { } frames) return;
        var value = args.NewValue;
        if (double.IsNaN(value) || !double.IsInteger(value) || value < 0 || value >= frames.Length)
        {
            _updatingImageFrame = true;
            try { sender.Value = _imageFrameIndex; }
            finally { _updatingImageFrame = false; }
            return;
        }

        var index = (int)value;
        if (index != _imageFrameIndex) ShowImageFrame(index);
    }

    /// <summary>Cancels a pending decode and drops the retained frames and the image surface.</summary>
    private void HideImage()
    {
        _imageLoad.Cancel();
        _imagePreview = null;
        _imageFramesBgra = null;
        _imageFrameIndex = 0;
        ImagePreview.Clear();
        ImagePreview.Visibility = Visibility.Collapsed;
        ImageFramePanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Cancels a pending mesh load and collapses the realized viewer, if any, without touching the gallery.</summary>
    private void HideMesh()
    {
        _meshLoad.Cancel();
        _meshViewer?.ClearScene();
        if (_meshViewer is not null) _meshViewer.Visibility = Visibility.Collapsed;
    }

    /// <summary>Shows the placeholder naming the node and its kind, or pointing at the Maps view for a 2D level.</summary>
    /// <param name="node">The node with no surface of its own.</param>
    private void ShowNoPreview(AssetNode node)
    {
        AssetPreviewPlaceholder.Visibility = Visibility.Visible;
        if (node.Kind != AssetNodeKind.Folder && AssetLevel2DSource.CanOpen(node))
        {
            RuntimeLocalization.SetText(AssetPreviewPlaceholder, "AssetPreview_ShownInMaps", node.Name);
        }
        else
        {
            RuntimeLocalization.SetText(AssetPreviewPlaceholder, "AssetPreview_NoPreview", node.Name,
                new ResourceArgument(AssetKindLabels.CaptionKey(node.Kind)));
        }

        SetPreviewStatus(string.Empty);
    }

    /// <summary>Returns the pane to its empty prompt when the source closes.</summary>
    private void ClearPreview()
    {
        _previewNode = null;
        HideImage();
        AssetPreviewPlaceholder.Visibility = Visibility.Visible;
        RuntimeLocalization.SetText(AssetPreviewPlaceholder, "AssetPreview_Empty.Text");
        SetPreviewStatus(string.Empty);
    }

    /// <summary>Writes untranslated preview status text, replacing any keyed binding.</summary>
    /// <param name="text">The message, or empty to clear the line.</param>
    private void SetPreviewStatus(string text) =>
        RuntimeLocalization.SetRaw(AssetPreviewStatusText, TextBlock.TextProperty, text);
}

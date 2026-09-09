using System.Collections.ObjectModel;
using Windows.Storage.Pickers;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Concurrency;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinRT.Interop;

namespace BethesdaMultitool;

/// <summary>
///     Browses one opened source — a loose folder, a single archive, or a whole classic game
///     install — as a tree, with panes for records, maps and raw assets.
///     <para>
///         The tab owns exactly one <see cref="AssetBrowseSession" /> at a time and disposes the
///         previous one when a new source is opened, because a session owns its filesystem (and
///         therefore its archive handles and memory maps). Opening parses archive tables and walks
///         every entry, so it runs off the UI thread.
///     </para>
/// </summary>
public sealed partial class AssetBrowserTab : UserControl, IDisposable
{
    /// <summary>Longest edge of a decoded thumbnail, in DEVICE pixels.</summary>
    private const int ThumbnailCellPixels = 96;

    private readonly LatestOnlyJob _audioLoad = new();

    private readonly ObservableCollection<AssetGalleryItem> _gallery = [];
    private readonly LatestOnlyJob _levelLoad = new();
    private readonly LatestOnlyJob _meshLoad = new();
    private readonly LatestOnlyJob _skyLoad = new();
    private readonly ThumbnailCache _thumbnails = new ThumbnailCache().RegisterWith(ResourceRegistry.Instance);
    private readonly LatestOnlyJob _videoLoad = new();
    private AssetAudioPlayer? _audio;
    private bool _disposed;
    private AssetThumbnailLoader? _loader;
    private BethesdaSceneViewerControl? _meshViewer;
    private (byte[] Riff, string Key)? _pendingAudio;
    private UnifiedAnalysisResult? _records;
    private AssetBrowseSession? _session;
    private int _sourceGeneration;
    private bool _suppressSeek;
    private AssetVideoPreview? _video;

    public AssetBrowserTab()
    {
        InitializeComponent();
        AssetGalleryView.ItemsSource = _gallery;
        AssetTreeView.SelectionChanged += AssetTreeView_SelectionChanged;
        RecordTreeView.SelectionChanged += RecordTreeView_SelectionChanged;

        _audio = new AssetAudioPlayer(DispatcherQueue);
        _audio.Progressed += (_, _) => UpdateTransport();
        _audio.Failed += (_, message) => AudioStatusText.Text = message;
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync() is { } path)
        {
            await OpenSourceAsync(path, () => AssetBrowseSession.OpenFolder(path));
        }
    }

    private async void OpenArchive_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        // ⚠ A WinUI FileOpenPicker cannot select an extension that is not listed here, so this
        // array is a hard gate on what the browser can open, not a convenience filter. .jar is how
        // all three TES Travels J2ME titles ship (the jar IS the install), .lmp is Dawnstar lump
        // storage, .arc the Oblivion PSP pack. Every one already parsed through ArchiveProbe; only
        // this list stood between them and the GUI.
        foreach (var extension in
                 new[] { ".bsa", ".ba2", ".bs6", ".bos", ".dat", ".pck", ".jar", ".lmp", ".arc" })
        {
            picker.FileTypeFilter.Add(extension);
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            // Claim it as a game first so the session carries an identity; an unrecognised
            // archive still opens exactly as before.
            await OpenSourceAsync(
                file.Path,
                () => AssetBrowseSession.TryOpenGameArchive(file.Path)
                      ?? AssetBrowseSession.OpenArchive(file.Path));
        }
    }

    private async void OpenGame_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync() is not { } path)
        {
            return;
        }

        // A game install mounts loose files and every archive its profile declares. When the
        // directory is not an install root the browser still opens it as a plain folder rather
        // than failing — the user asked to see what is there either way.
        await OpenSourceAsync(path,
            () => AssetBrowseSession.TryOpenGameRoot(path) ?? AssetBrowseSession.OpenFolder(path));
    }

    private static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>
    ///     Opens <paramref name="path" /> exactly as the Open flyout would — a recognised game root
    ///     or plain folder for a directory, a recognised game archive or plain archive for a file —
    ///     but without a picker. Backs the <c>--asset-source</c> launch argument.
    ///     <para>
    ///         ⛔ Scripted verification must reach this surface through this method, never through
    ///         the native picker. Driving that picker means synthetic keystrokes into whatever
    ///         window has focus, and an earlier attempt typed a path into the user's browser.
    ///     </para>
    /// </summary>
    public Task OpenFromLaunchArgumentAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (Directory.Exists(path))
        {
            return OpenSourceAsync(path,
                () => AssetBrowseSession.TryOpenGameRoot(path) ?? AssetBrowseSession.OpenFolder(path));
        }

        if (File.Exists(path))
        {
            return OpenSourceAsync(path,
                () => AssetBrowseSession.TryOpenGameArchive(path) ?? AssetBrowseSession.OpenArchive(path));
        }

        AssetTreeStatusText.Text = $"Could not open: not found: {path}";
        Logger.Instance.Warn("[AssetBrowser] --asset-source not found: {0}", path);
        return Task.CompletedTask;
    }

    private async Task OpenSourceAsync(string path, Func<AssetBrowseSession> open)
    {
        if (_disposed)
        {
            return;
        }

        var generation = ++_sourceGeneration;
        SourcePathTextBox.Text = path;
        OpenSourceButton.IsEnabled = false;
        AssetTreeStatusText.Text = "Opening...";
        try
        {
            var session = await Task.Run(open);
            if (_disposed || generation != _sourceGeneration)
            {
                session.Dispose();
                return;
            }

            CloseSource();
            generation = _sourceGeneration;
            _session = session;
            SourcePathTextBox.Text = path;

            // The loader's session cancels every in-flight decode, so it must be replaced with the
            // browse session it reads through — never after it.
            _loader = new AssetThumbnailLoader(
                DispatcherQueue, _thumbnails, ThumbnailCellPixels, XamlRoot?.RasterizationScale ?? 1.0);
            _loader.BeginSession(session);

            ShowTree(session);
            ShowGallery(session.Root);
            _ = LoadRecordsAsync(path, session);
        }
        catch (Exception ex)
        {
            if (!_disposed && generation == _sourceGeneration)
            {
                AssetTreeView.RootNodes.Clear();
                AssetTreeStatusText.Text = $"Could not open: {ex.Message}";
            }
        }
        finally
        {
            if (!_disposed && generation == _sourceGeneration)
            {
                OpenSourceButton.IsEnabled = true;
            }
        }
    }

    private void ShowTree(AssetBrowseSession session)
    {
        AssetTreeView.RootNodes.Clear();
        AssetTreeView.RootNodes.Add(BuildTreeNode(session.Root));

        var files = CountFiles(session.Root);
        AssetTreeStatusText.Text = $"{session.SourceLabel} — {files:N0} file(s)";
    }

    /// <summary>
    ///     Fills the gallery with the previewable files directly under <paramref name="folder" />.
    ///     <para>
    ///         Direct children only, deliberately: a game root holds tens of thousands of assets and
    ///         a flattened gallery would be neither navigable nor cheap. The tree is how you choose
    ///         scope; the gallery shows what that scope contains.
    ///     </para>
    /// </summary>
    private void ShowGallery(AssetNode folder)
    {
        foreach (var item in _gallery)
        {
            item.IsRealized = false;
        }

        _gallery.Clear();

        var shown = 0;
        var skipped = 0;
        foreach (var child in folder.Children)
        {
            if (child.Kind == AssetNodeKind.Folder)
            {
                continue;
            }

            if (AssetThumbnailSource.CanRender(child))
            {
                _gallery.Add(new AssetGalleryItem { Node = child });
                shown++;
            }
            else
            {
                skipped++;
            }
        }

        AssetGalleryStatusText.Text = shown == 0 && skipped == 0
            ? string.Empty
            : $"{shown:N0} previewable, {skipped:N0} other";
    }

    private void AssetTreeView_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (_session is null || sender.SelectedNode?.Content is not AssetNode node)
        {
            return;
        }

        // A file's own folder is the useful scope when a leaf is picked, so the gallery keeps
        // showing its siblings rather than emptying.
        ShowGallery(node.Kind == AssetNodeKind.Folder ? node : node.Parent ?? node);
        ShowMeshOrGallery(node);
        ShowAudio(node);
        ShowVideo(node);
        ShowLevel2D(node);
        ShowSky(node);
    }

    /// <summary>
    ///     Shows a Daggerfall sky set as the backdrop behind the level view.
    ///     <para>
    ///         Frame 0 of the first half is used. Which set maps to which region or weather is NOT
    ///         established, so nothing here claims one — the set is whichever file was selected.
    ///     </para>
    /// </summary>
    private void ShowSky(AssetNode node)
    {
        var session = _session;
        if (session is null || !ClassicSkySource.IsSky(node))
        {
            return;
        }

        _ = _skyLoad.RunAsync(
            token => ClassicSkySource.TryLoadFrame(session, node, 0, 0, token),
            texture =>
            {
                if (texture is null)
                {
                    SkyBackdropImage.Visibility = Visibility.Collapsed;
                    return;
                }

                SkyBackdropImage.Source = AssetBitmapFactory.FromPremultipliedBgra(
                    texture.Width, texture.Height, PremultipliedBgra.FromRgba(texture.Pixels));
                SkyBackdropImage.Visibility = Visibility.Visible;
                AssetGalleryView.Visibility = Visibility.Collapsed;
                AssetGalleryStatusText.Text = $"{node.Name} — sky backdrop {texture.Width}x{texture.Height}";
            });
    }

    /// <summary>
    ///     Renders a grid-authored classic level into the Map Viewer pane.
    ///     <para>
    ///         Decoding runs off the UI thread because these are large: Daggerfall's WOODS heightmap
    ///         is 1000x500 before scaling, and an Arena .MIF layer is LZHUF-compressed.
    ///     </para>
    /// </summary>
    private void ShowLevel2D(AssetNode node)
    {
        var session = _session;
        if (session is null || !AssetLevel2DSource.CanOpen(node))
        {
            _levelLoad.Cancel();
            _skyLoad.Cancel();
            SkyBackdropImage.Source = null;
            SkyBackdropImage.Visibility = Visibility.Collapsed;
            Level2dMap.SetSource(null);
            Level2dMap.Visibility = Visibility.Collapsed;
            MapViewerPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        _ = _levelLoad.RunAsync(
            token => AssetLevel2DSource.TryOpen(session, node, token),
            source =>
            {
                if (source is null)
                {
                    Level2dMap.SetSource(null);
                    Level2dMap.Visibility = Visibility.Collapsed;
                    MapViewerPlaceholder.Visibility = Visibility.Visible;
                    MapViewerPlaceholder.Text = $"{node.Name} could not be read as a level.";
                    return;
                }

                MapViewerPlaceholder.Visibility = Visibility.Collapsed;
                Level2dMap.Visibility = Visibility.Visible;
                Level2dMap.SetSource(source);
            },
            source => (source as IDisposable)?.Dispose());
    }

    /// <summary>
    ///     Opens a supported movie, or hides the video surface for anything else.
    ///     <para>
    ///         Indexed classic formats materialise their frames before playback; Bink, Smacker
    ///         and Interplay MVE decode on demand. Opening runs off the UI thread because the
    ///         eager formats can require substantial decoding work.
    ///     </para>
    /// </summary>
    private void ShowVideo(AssetNode node)
    {
        var session = _session;
        if (session is null || !ClassicVideoClip.CanOpenAnyVideo(node))
        {
            _videoLoad.Cancel();
            DisposeVideo();
            VideoPreviewImage.Visibility = Visibility.Collapsed;
            return;
        }

        AudioStatusText.Text = "Decoding movie...";
        AudioTransportPanel.Visibility = Visibility.Visible;
        _ = _videoLoad.RunAsync(
            token => ClassicVideoClip.TryOpenAnyVideo(session, node, token),
            clip =>
            {
                DisposeVideo();
                if (clip is null || clip.FrameCount == 0)
                {
                    AudioStatusText.Text = $"{node.Name} — could not be read as a movie";
                    VideoPreviewImage.Visibility = Visibility.Collapsed;
                    return;
                }

                _video = new AssetVideoPreview(clip);
                if (_video.ErrorMessage is { } error)
                {
                    DisposeVideo();
                    AudioStatusText.Text = $"{node.Name} — {error}";
                    VideoPreviewImage.Visibility = Visibility.Collapsed;
                    return;
                }

                _video.Progressed += (_, _) => UpdateTransport();
                _video.Failed += (_, message) => AudioStatusText.Text = $"{node.Name} — {message}";

                VideoPreviewImage.Source = _video.Surface;
                VideoPreviewImage.Visibility = Visibility.Visible;
                AssetGalleryView.Visibility = Visibility.Collapsed;
                AudioStatusText.Text = $"{clip.Width}×{clip.Height} · {clip.FrameCount:N0} frames";
                UpdateTransport();
            });
    }

    private void DisposeVideo()
    {
        _video?.Dispose();
        _video = null;
        VideoPreviewImage.Source = null;
    }

    /// <summary>
    ///     Loads and plays a single-sound asset, or hides the transport for anything else.
    ///     <para>
    ///         Selection does NOT auto-play: the decode happens on selection so the duration is
    ///         known, but sound only starts when the user presses play. A browser that shouts at you
    ///         for arrow-keying down a list of 459 Daggerfall effects is not a browser.
    ///     </para>
    /// </summary>
    private void ShowAudio(AssetNode node)
    {
        var session = _session;
        if (session is null || !AssetAudioSource.CanPlay(node))
        {
            _audioLoad.Cancel();
            _audio?.Stop();
            AudioTransportPanel.Visibility = Visibility.Collapsed;
            return;
        }

        AudioStatusText.Text = "Loading...";
        AudioTransportPanel.Visibility = Visibility.Visible;
        _ = _audioLoad.RunAsync(
            token => AssetAudioSource.TryLoad(session, node, token),
            sound =>
            {
                if (sound is not { } playable)
                {
                    AudioStatusText.Text = "Not a playable sound";
                    return;
                }

                _pendingAudio = (playable.Riff, $"{session.SourceLabel}|{node.VirtualPath}");
                AudioStatusText.Text = playable.SampleRate > 0
                    ? $"{playable.SampleRate:N0} Hz · {playable.BitsPerSample}-bit"
                    : node.Name;
            });
    }

    private void AudioPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_video is not null)
        {
            _video.TogglePause();
            return;
        }

        if (_audio is null)
        {
            return;
        }

        if (_audio.HasSound)
        {
            _audio.TogglePause();
            return;
        }

        if (_pendingAudio is { } pending)
        {
            _audio.Play(pending.Riff, pending.Key);
        }
    }

    private void AudioStop_Click(object sender, RoutedEventArgs e)
    {
        if (_video is not null)
        {
            _video.Stop();
            return;
        }

        _audio?.Stop();
    }

    private void AudioPosition_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // The timer writes this slider as playback advances; only a real user drag should seek.
        if (_suppressSeek)
        {
            return;
        }

        if (_video is not null)
        {
            _video.Seek(e.NewValue / 1000.0);
            return;
        }

        _audio?.Seek(e.NewValue / 1000.0);
    }

    /// <summary>Reflects whichever medium is live into the shared transport.</summary>
    private void UpdateTransport()
    {
        double fraction;
        bool playing;

        if (_video is { } video)
        {
            playing = video.IsPlaying;
            fraction = video.FrameCount > 1 ? (double)video.FrameIndex / (video.FrameCount - 1) : 0;
        }
        else if (_audio is { } audio)
        {
            playing = audio.IsPlaying;
            var duration = audio.Duration;
            fraction = duration > TimeSpan.Zero ? audio.Position / duration : 0;
        }
        else
        {
            return;
        }

        AudioPlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";

        _suppressSeek = true;
        AudioPositionSlider.Value = Math.Clamp(fraction * 1000.0, 0, 1000);
        _suppressSeek = false;
    }

    /// <summary>
    ///     Swaps the right-hand pane between the gallery and the 3D viewer, and loads the mesh off
    ///     the UI thread.
    ///     <para>
    ///         The decode runs through <see cref="LatestOnlyJob" /> so that clicking down a list of
    ///         meshes cannot leave a slow earlier one painting over a faster later one.
    ///     </para>
    /// </summary>
    private void ShowMeshOrGallery(AssetNode node)
    {
        var session = _session;
        var isLevel = session is not null && ClassicLevelPreviewSource.CanPreview(session, node);
        if (session is null || (!isLevel && !ClassicMeshPreviewSource.CanPreview(session, node)))
        {
            _meshLoad.Cancel();
            _meshViewer?.ClearScene();
            if (_meshViewer is not null)
            {
                _meshViewer.Visibility = Visibility.Collapsed;
            }

            AssetGalleryView.Visibility = Visibility.Visible;
            return;
        }

        AssetGalleryStatusText.Text = isLevel ? $"Assembling {node.Name}..." : $"Loading {node.Name}...";
        _ = _meshLoad.RunAsync(
            token => isLevel
                ? ClassicLevelPreviewSource.TryLoad(session, node, token)
                : ClassicMeshPreviewSource.TryLoad(session, node, token),
            scene =>
            {
                if (scene is null)
                {
                    AssetGalleryStatusText.Text = isLevel
                        ? $"{node.Name} — resolved no geometry against the meshes beside it"
                        : $"{node.Name} — could not be read as a mesh";
                    return;
                }

                // Realize the deferred control only now that there is something to show in it.
                _meshViewer ??= FindName(nameof(MeshSceneViewer)) as BethesdaSceneViewerControl;
                if (_meshViewer is null)
                {
                    AssetGalleryStatusText.Text = "3D viewer unavailable.";
                    return;
                }

                AssetGalleryView.Visibility = Visibility.Collapsed;
                _meshViewer.Visibility = Visibility.Visible;
                _meshViewer.SetScene(scene);
                AssetGalleryStatusText.Text = $"{node.Name} — {scene.Nodes.Count:N0} node(s)";
            });
    }

    /// <summary>
    ///     Queues a decode as each tile is realized, and marks recycled tiles so their in-flight
    ///     result is dropped rather than applied to whatever the container now shows.
    /// </summary>
    private void AssetGalleryView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not AssetGalleryItem item)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            item.IsRealized = false;
            item.Reset();
            return;
        }

        item.IsRealized = true;
        _loader?.Request(item);
    }

    /// <summary>
    ///     Loads the synthesized record set for a classic install into the Data Explorer.
    ///     <para>
    ///         Classic games have no plugin file, so the records come from
    ///         <see cref="ClassicGameAnalyzer" /> walking the whole install — which is slow enough
    ///         to belong off the UI thread, and expected to fail for a plain folder or a lone
    ///         archive. A failure here must not disturb the asset panes, which work regardless.
    ///     </para>
    /// </summary>
    private async Task LoadRecordsAsync(string path, AssetBrowseSession session)
    {
        RecordTreeView.RootNodes.Clear();
        RecordFieldsList.ItemsSource = null;
        RecordTreeView.Visibility = Visibility.Collapsed;
        RecordFieldsList.Visibility = Visibility.Collapsed;
        DataExplorerPlaceholder.Visibility = Visibility.Visible;
        DataExplorerPlaceholder.Text = "Reading records...";

        UnifiedAnalysisResult? result = null;
        try
        {
            result = await ClassicGameAnalyzer.LoadAsync(path);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException
                                      or IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Not a classic install, or one this build cannot synthesize records for.
        }

        if (_disposed || !ReferenceEquals(_session, session))
        {
            result?.Dispose();
            return;
        }

        _records?.Dispose();
        _records = result;

        var groups = result is null ? [] : RecordBrowserModel.Build(result.Records);
        if (groups.Count == 0)
        {
            DataExplorerPlaceholder.Text = "This source has no synthesized records.";
            return;
        }

        foreach (var group in groups)
        {
            var typeNode = new TreeViewNode { Content = group };
            foreach (var record in group.Records)
            {
                typeNode.Children.Add(new TreeViewNode { Content = new RecordBrowserItem(record) });
            }

            RecordTreeView.RootNodes.Add(typeNode);
        }

        DataExplorerPlaceholder.Visibility = Visibility.Collapsed;
        RecordTreeView.Visibility = Visibility.Visible;
        RecordFieldsList.Visibility = Visibility.Visible;
        RecordStatusText.Text = $"{groups.Sum(g => g.Records.Count):N0} record(s) across {groups.Count} type(s)";
    }

    private void RecordTreeView_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedNode?.Content is not RecordBrowserItem { Record: var record })
        {
            RecordFieldsList.ItemsSource = null;
            return;
        }

        var rows = new List<RecordFieldRow>
        {
            new("Record type", record.RecordType),
            new("FormID", $"0x{record.FormId:X8}")
        };

        if (!string.IsNullOrWhiteSpace(record.EditorId))
        {
            rows.Add(new RecordFieldRow("Editor ID", record.EditorId));
        }

        if (!string.IsNullOrWhiteSpace(record.FullName))
        {
            rows.Add(new RecordFieldRow("Name", record.FullName));
        }

        rows.AddRange(record.Fields.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .Select(f => RecordFieldRow.From(f.Key, f.Value)));

        RecordFieldsList.ItemsSource = rows;
        RecordStatusText.Text = RecordBrowserModel.DescribeRecord(record);
    }

    private static TreeViewNode BuildTreeNode(AssetNode node)
    {
        var treeNode = new TreeViewNode { Content = node };
        foreach (var child in node.Children)
        {
            treeNode.Children.Add(BuildTreeNode(child));
        }

        return treeNode;
    }

    private static int CountFiles(AssetNode node)
    {
        return node.Children.Count == 0 ? 1 : node.Children.Sum(CountFiles);
    }

    /// <summary>Releases the open source while keeping the tab available for another source.</summary>
    public void CloseSource()
    {
        _sourceGeneration++;
        // Loader first: it reads through the session, so tearing the session down under a running
        // decode is what would fault.
        _loader?.Dispose();
        _loader = null;
        _audioLoad.Cancel();
        _videoLoad.Cancel();
        _levelLoad.Cancel();
        _skyLoad.Cancel();
        SkyBackdropImage.Source = null;
        SkyBackdropImage.Visibility = Visibility.Collapsed;
        Level2dMap.SetSource(null);
        Level2dMap.Visibility = Visibility.Collapsed;
        MapViewerPlaceholder.Visibility = Visibility.Visible;
        _audio?.Stop();
        DisposeVideo();
        _pendingAudio = null;
        AudioTransportPanel.Visibility = Visibility.Collapsed;
        _meshLoad.Cancel();
        _meshViewer?.ClearScene();
        _session?.Dispose();
        _session = null;

        _records?.Dispose();
        _records = null;
        RecordTreeView.RootNodes.Clear();
        RecordFieldsList.ItemsSource = null;
        RecordTreeView.Visibility = Visibility.Collapsed;
        RecordFieldsList.Visibility = Visibility.Collapsed;
        DataExplorerPlaceholder.Visibility = Visibility.Visible;
        RecordStatusText.Text = string.Empty;

        _gallery.Clear();
        AssetTreeView.RootNodes.Clear();
        AssetTreeStatusText.Text = "No source opened.";
        AssetGalleryStatusText.Text = string.Empty;
        SourcePathTextBox.Text = string.Empty;
    }

    /// <summary>Releases the tab's workers, playback and rendering resources at window shutdown.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _audioLoad.Dispose();
        _videoLoad.Dispose();
        _levelLoad.Dispose();
        _skyLoad.Dispose();
        _meshLoad.Dispose();
        CloseSource();
        _audio?.Dispose();
        _audio = null;
        _meshViewer?.Dispose();
        _meshViewer = null;
        _thumbnails.Dispose();
    }
}

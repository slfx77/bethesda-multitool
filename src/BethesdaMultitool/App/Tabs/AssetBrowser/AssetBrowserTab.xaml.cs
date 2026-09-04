using Windows.Storage.Pickers;
using BethesdaMultitool.Core.AssetBrowse;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
public sealed partial class AssetBrowserTab : UserControl
{
    private AssetBrowseSession? _session;

    public AssetBrowserTab()
    {
        InitializeComponent();
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
        foreach (var extension in new[] { ".bsa", ".ba2", ".bs6", ".bos", ".dat", ".pck" })
        {
            picker.FileTypeFilter.Add(extension);
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await OpenSourceAsync(file.Path, () => AssetBrowseSession.OpenArchive(file.Path));
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
        await OpenSourceAsync(path, () => AssetBrowseSession.TryOpenGameRoot(path) ?? AssetBrowseSession.OpenFolder(path));
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async Task OpenSourceAsync(string path, Func<AssetBrowseSession> open)
    {
        SourcePathTextBox.Text = path;
        OpenSourceButton.IsEnabled = false;
        AssetTreeStatusText.Text = "Opening...";
        try
        {
            var session = await Task.Run(open);
            _session?.Dispose();
            _session = session;
            ShowTree(session);
        }
        catch (Exception ex)
        {
            AssetTreeView.RootNodes.Clear();
            AssetTreeStatusText.Text = $"Could not open: {ex.Message}";
        }
        finally
        {
            OpenSourceButton.IsEnabled = true;
        }
    }

    private void ShowTree(AssetBrowseSession session)
    {
        AssetTreeView.RootNodes.Clear();
        AssetTreeView.RootNodes.Add(BuildTreeNode(session.Root));

        var files = CountFiles(session.Root);
        AssetTreeStatusText.Text = $"{session.SourceLabel} — {files:N0} file(s)";
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

    /// <summary>Releases the open source. The window calls this on shutdown.</summary>
    public void CloseSource()
    {
        _session?.Dispose();
        _session = null;
        AssetTreeView.RootNodes.Clear();
        AssetTreeStatusText.Text = "No source opened.";
        SourcePathTextBox.Text = string.Empty;
    }
}

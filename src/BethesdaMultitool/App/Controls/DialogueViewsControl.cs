using System.Globalization;
using Windows.Storage.Pickers;
using BethesdaMultitool.Core.Formats.Dialogue.CreationKit;
using BethesdaMultitool.Localization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinRT.Interop;

namespace BethesdaMultitool;

/// <summary>Displays optional Creation Kit diagrams separately from plugin-derived dialogue.</summary>
public sealed class DialogueViewsControl : UserControl
{
    private readonly ComboBox _documents = new() { MinWidth = 180, MaxWidth = 440, DisplayMemberPath = "Source" };
    private readonly Canvas _canvas = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _open = new();
    private readonly Button _folder = new();
    private readonly ScrollViewer _scroll;
    private int _generation;
    private CancellationTokenSource? _loading;

    /// <summary>Creates a sidecar picker and zoomable diagram view.</summary>
    public DialogueViewsControl()
    {
        RuntimeLocalization.Set(_open, ContentControl.ContentProperty, "DialogueViews_Open");
        RuntimeLocalization.Set(_folder, ContentControl.ContentProperty, "DialogueViews_Folder");
        RuntimeLocalization.Set(_status, TextBlock.TextProperty, "DialogueViews_Description");
        AutomationProperties.SetAutomationId(_open, "DialogueViews.Open");
        AutomationProperties.SetAutomationId(_folder, "DialogueViews.Folder");
        AutomationProperties.SetAutomationId(_documents, "DialogueViews.Documents");
        RuntimeLocalization.Set(_documents, AutomationProperties.NameProperty, "DialogueViews_Documents");
        _open.Click += Open_Click;
        _folder.Click += Folder_Click;
        _documents.SelectionChanged += Documents_SelectionChanged;
        _scroll = new ScrollViewer
        {
            Content = _canvas, Height = 340, ZoomMode = ZoomMode.Enabled, MinZoomFactor = 0.1f, MaxZoomFactor = 4,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled,
            Visibility = Visibility.Collapsed
        };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(_open);
        tools.Children.Add(_folder);
        tools.Children.Add(_documents);
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(tools);
        body.Children.Add(_status);
        body.Children.Add(_scroll);
        var expander = new Expander
        {
            Content = body,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        RuntimeLocalization.Set(expander, Expander.HeaderProperty, "DialogueViews_Title");
        Content = expander;
        Unloaded += OnUnloaded;
    }

    /// <summary>Requests navigation using an explicitly selected potential plugin FormID.</summary>
    public event EventHandler<uint>? ReferenceRequested;

    /// <summary>Clears sidecars and invalidates an unfinished load when the owning record source changes.</summary>
    public void Reset()
    {
        _generation++;
        _loading?.Cancel();
        _documents.ItemsSource = null;
        _canvas.Children.Clear();
        _scroll.Visibility = Visibility.Collapsed;
        RuntimeLocalization.Set(_status, TextBlock.TextProperty, "DialogueViews_Description");
    }

    /// <summary>Loads a selected XML or ZIP sidecar without navigating native pickers from automation.</summary>
    private async void Open_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".xml");
            picker.FileTypeFilter.Add(".zip");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));
            if (await picker.PickSingleFileAsync() is { } file) { await LoadPathAsync(file.Path); }
        }
        catch (Exception exception) { _status.Text = exception.Message; }
    }

    /// <summary>Loads a selected sidecar folder while preserving the opened plugin.</summary>
    private async void Folder_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));
            if (await picker.PickSingleFolderAsync() is { } folder) { await LoadPathAsync(folder.Path); }
        }
        catch (Exception exception) { _status.Text = exception.Message; }
    }

    /// <summary>Loads diagrams off the UI thread and discards results from an obsolete source.</summary>
    /// <param name="path">The XML, ZIP, or folder path.</param>
    /// <returns>Completion of loading and presentation.</returns>
    internal async Task LoadPathAsync(string path)
    {
        var generation = ++_generation;
#pragma warning disable S6966 // Cancel synchronously before replacing the load owner; awaiting here would allow a newer load to publish its owner first.
        _loading?.Cancel();
#pragma warning restore S6966
        using var cancellation = new CancellationTokenSource();
        _loading = cancellation;
        _open.IsEnabled = _folder.IsEnabled = false;
        try
        {
            var documents = await Task.Run(() => DialogueViewReader.ReadPath(path, cancellation.Token), cancellation.Token);
            if (generation != _generation) { return; }
            _documents.ItemsSource = documents;
            RuntimeLocalization.Set(_status, TextBlock.TextProperty, "DialogueViews_Loaded", documents.Count);
            if (documents.Count > 0) { _documents.SelectedIndex = 0; }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Superseded/reset loads finish without publishing their obsolete result.
        }
        finally
        {
            if (ReferenceEquals(_loading, cancellation))
            {
                _loading = null;
                _open.IsEnabled = _folder.IsEnabled = true;
            }
        }
    }

    /// <summary>Shows an unresolved reference beside the diagram even without plugin dialogue.</summary>
    public void ShowUnresolvedReference(uint formId)
        => RuntimeLocalization.Set(_status, TextBlock.TextProperty, "DialogueViews_Unresolved", $"{formId:X8}");

    /// <summary>Shows the selected diagram with its authored positions and link routing.</summary>
    private void Documents_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        _canvas.Children.Clear();
        if (_documents.SelectedItem is not DialogueViewDocument document) { return; }
        var bounds = document.Nodes.Select((node, index) => node.Bounds ??
            new DialogueViewBounds(index % 3 * 320, index / 3 * 160, 300, 120)).ToArray();
        var routes = document.Links.Select(link => ParseRoute(link.Points)).ToArray();
        var xs = bounds.SelectMany(rectangle => new[] { rectangle.X, rectangle.X + rectangle.Width })
            .Concat(routes.SelectMany(route => route.Select(point => point.X))).ToArray();
        var ys = bounds.SelectMany(rectangle => new[] { rectangle.Y, rectangle.Y + rectangle.Height })
            .Concat(routes.SelectMany(route => route.Select(point => point.Y))).ToArray();
        var minX = xs.DefaultIfEmpty(0).Min();
        var minY = ys.DefaultIfEmpty(0).Min();
        var extentX = xs.DefaultIfEmpty(300).Max() - minX;
        var extentY = ys.DefaultIfEmpty(120).Max() - minY;
        if (!double.IsFinite(extentX) || !double.IsFinite(extentY))
        {
            RuntimeLocalization.Set(_status, TextBlock.TextProperty, "DialogueViews_CoordinatesTooLarge");
            _scroll.Visibility = Visibility.Collapsed;
            return;
        }
        // A single scale preserves the authored geometry when the canvas needs a finite display extent.
        var scale = Math.Min(1, 100000 / Math.Max(1, Math.Max(extentX, extentY)));
        _canvas.Width = Math.Max(320, extentX * scale + 32);
        _canvas.Height = Math.Max(200, extentY * scale + 32);
        var knownIds = document.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var missingLinks = document.Links.Count(link => link.OriginId is null || link.DestinationId is null ||
            !knownIds.Contains(link.OriginId) || !knownIds.Contains(link.DestinationId));
        RuntimeLocalization.Set(_status, TextBlock.TextProperty, "DialogueViews_DiagramSummary", document.Nodes.Count, document.Links.Count, missingLinks);
        var linkIndex = 0;
        foreach (var link in document.Links)
        {
            var line = new Polyline { Stroke = new SolidColorBrush(Colors.Gray), StrokeThickness = 2 };
            AutomationProperties.SetAutomationId(line, "DialogueViews.Link." + link.Id);
            foreach (var point in routes[linkIndex++])
            {
                line.Points.Add(new Windows.Foundation.Point((point.X - minX) * scale + 16, (point.Y - minY) * scale + 16));
            }
            _canvas.Children.Add(line);
            if (!string.IsNullOrWhiteSpace(link.Text))
            {
                var label = new TextBlock { Text = link.Text, IsTextSelectionEnabled = true };
                AutomationProperties.SetAutomationId(label, "DialogueViews.LinkLabel." + link.Id);
                var point = line.Points.Count > 0 ? line.Points[line.Points.Count / 2] : new Windows.Foundation.Point(16, 16);
                Canvas.SetLeft(label, point.X);
                Canvas.SetTop(label, point.Y);
                _canvas.Children.Add(label);
            }
        }
        var index = 0;
        foreach (var node in document.Nodes)
        {
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = node.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            foreach (var formId in node.FormIds)
            {
                var reference = new HyperlinkButton { Content = $"{formId:X8}", Tag = formId, Padding = new Thickness(0) };
                reference.Click += Reference_Click;
                panel.Children.Add(reference);
            }
            var border = new Border
            {
                Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                Padding = new Thickness(8), BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Colors.Gray), Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                Width = bounds[index].Width * scale, Height = bounds[index].Height * scale
            };
            AutomationProperties.SetAutomationId(border, "DialogueViews.Node." + node.Id);
            ToolTipService.SetToolTip(border, node.ToolTip);
            Canvas.SetLeft(border, (bounds[index].X - minX) * scale + 16);
            Canvas.SetTop(border, (bounds[index].Y - minY) * scale + 16);
            _canvas.Children.Add(border);
            index++;
        }
        _scroll.Visibility = Visibility.Visible;
    }

    /// <summary>Reads finite route points without changing the source document's original route text.</summary>
    private static List<Windows.Foundation.Point> ParseRoute(IReadOnlyList<string> values)
    {
        var result = new List<Windows.Foundation.Point>();
        foreach (var value in values)
        {
            var parts = value.Split(',');
            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                double.IsFinite(x) && double.IsFinite(y))
            {
                result.Add(new Windows.Foundation.Point(x, y));
            }
        }
        return result;
    }

    /// <summary>Raises an explicit record-navigation request; it never changes plugin data.</summary>
    private void Reference_Click(object sender, RoutedEventArgs args) => ReferenceRequested?.Invoke(this, (uint)((HyperlinkButton)sender).Tag);

    /// <summary>Invalidates pending load results when the view is detached.</summary>
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _generation++;
        _loading?.Cancel();
    }
}

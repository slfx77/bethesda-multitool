using Windows.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Xbox360MemoryCarver.Core;

namespace Xbox360MemoryCarver;

/// <summary>
///     Standalone hex minimap control for file visualization.
///     Displays color-coded representation of file regions by type.
/// </summary>
public sealed partial class HexMinimapControl : UserControl
{
    private readonly List<FileRegion> _fileRegions = [];
    private AnalysisResult? _analysisResult;
    private long _fileSize;

    public HexMinimapControl()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;

        // Build the legend from the shared color definitions
        BuildLegend();
    }

    private void BuildLegend()
    {
        LegendPanel.Children.Clear();

        foreach (var category in FileTypeColors.LegendCategories)
        {
            var itemPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4
            };

            var colorBox = new Border
            {
                Width = 12,
                Height = 12,
                Background = new SolidColorBrush(category.Color),
                CornerRadius = new CornerRadius(2)
            };

            var label = new TextBlock
            {
                Text = category.Name,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 204, 204, 204))
            };

            itemPanel.Children.Add(colorBox);
            itemPanel.Children.Add(label);
            LegendPanel.Children.Add(itemPanel);
        }

        // Add the "Unknown" category
        var unknownPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4
        };

        var unknownColorBox = new Border
        {
            Width = 12,
            Height = 12,
            Background = new SolidColorBrush(FileTypeColors.UnknownColor),
            CornerRadius = new CornerRadius(2)
        };

        var unknownLabel = new TextBlock
        {
            Text = "Unknown",
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 204, 204, 204))
        };

        unknownPanel.Children.Add(unknownColorBox);
        unknownPanel.Children.Add(unknownLabel);
        LegendPanel.Children.Add(unknownPanel);
    }

    public void Clear()
    {
        _analysisResult = null;
        _fileSize = 0;
        _fileRegions.Clear();
        MinimapCanvas.Children.Clear();
    }

    public void LoadData(string filePath, AnalysisResult analysisResult)
    {
        _analysisResult = analysisResult;

        var fileInfo = new FileInfo(filePath);
        _fileSize = fileInfo.Length;

        BuildFileRegions();
        Render();
    }

    private void BuildFileRegions()
    {
        _fileRegions.Clear();

        if (_analysisResult == null) return;

        // Sort by size ascending - smaller files processed first get priority
        // This ensures files contained within larger files are visible
        var sortedFiles = _analysisResult.CarvedFiles
            .Where(f => f.Length > 0)
            .OrderBy(f => f.Length)
            .ToList();

        var occupiedRanges = new List<(long Start, long End)>();

        foreach (var file in sortedFiles)
        {
            var start = file.Offset;
            var end = file.Offset + file.Length;

            // Check if this file's range is already fully covered by existing regions
            if (IsRangeFullyCovered(start, end, occupiedRanges)) continue;

            _fileRegions.Add(new FileRegion
            {
                Start = start,
                End = end,
                TypeName = file.FileType,
                Color = FileTypeColors.GetColor(file)
            });
            occupiedRanges.Add((start, end));
        }

        _fileRegions.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    private static bool IsRangeFullyCovered(long start, long end, List<(long Start, long End)> ranges)
    {
        // Check if every byte in [start, end) is covered by existing ranges
        var relevantRanges = ranges
            .Where(r => r.Start < end && r.End > start)
            .OrderBy(r => r.Start)
            .ToList();

        if (relevantRanges.Count == 0) return false;

        var currentPos = start;
        foreach (var range in relevantRanges)
        {
            if (range.Start > currentPos) return false; // Gap found
            currentPos = Math.Max(currentPos, range.End);
            if (currentPos >= end) return true; // Fully covered
        }

        return currentPos >= end;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_analysisResult != null) Render();
    }

    private void Render()
    {
        MinimapCanvas.Children.Clear();

        if (_analysisResult == null || _fileSize == 0) return;

        var canvasWidth = MinimapCanvas.ActualWidth;
        var canvasHeight = MinimapCanvas.ActualHeight;

        if (canvasWidth <= 0 || canvasHeight <= 0) return;

        // Background for unknown/untyped regions
        var bgRect = new Rectangle
        {
            Width = canvasWidth,
            Height = canvasHeight,
            Fill = new SolidColorBrush(FileTypeColors.UnknownColor)
        };
        MinimapCanvas.Children.Add(bgRect);

        // Draw each file region as a colored bar
        foreach (var region in _fileRegions)
        {
            var startY = region.Start / (double)_fileSize * canvasHeight;
            var endY = region.End / (double)_fileSize * canvasHeight;
            var height = Math.Max(1, endY - startY);

            var rect = new Rectangle
            {
                Width = canvasWidth,
                Height = height,
                Fill = new SolidColorBrush(region.Color)
            };
            Canvas.SetTop(rect, startY);
            Canvas.SetLeft(rect, 0);
            ToolTipService.SetToolTip(rect, $"{region.TypeName}\n{region.End - region.Start:N0} bytes");
            MinimapCanvas.Children.Add(rect);
        }
    }

    private sealed class FileRegion
    {
        public long Start { get; init; }
        public long End { get; init; }
        public required string TypeName { get; init; }
        public Color Color { get; init; }
    }
}

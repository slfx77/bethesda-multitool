using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Windows.UI;
using Xbox360MemoryCarver.Core;

namespace Xbox360MemoryCarver.App;

/// <summary>
/// Standalone hex minimap control for file visualization.
/// Displays color-coded representation of file regions by type.
/// </summary>
public sealed partial class HexMinimapControl : UserControl
{
    private string? _filePath;
    private AnalysisResult? _analysisResult;
    private long _fileSize;
    private List<FileRegion> _fileRegions = [];

    public HexMinimapControl()
    {
        this.InitializeComponent();
        this.SizeChanged += OnSizeChanged;
    }

    public void Clear()
    {
        _filePath = null;
        _analysisResult = null;
        _fileSize = 0;
        _fileRegions.Clear();
        MinimapCanvas.Children.Clear();
    }

    public void LoadData(string filePath, AnalysisResult analysisResult)
    {
        _filePath = filePath;
        _analysisResult = analysisResult;

        var fileInfo = new FileInfo(filePath);
        _fileSize = fileInfo.Length;

        BuildFileRegions();
        Render();
    }

    private void BuildFileRegions()
    {
        _fileRegions.Clear();

        if (_analysisResult == null)
            return;

        foreach (var file in _analysisResult.CarvedFiles.OrderBy(f => f.Offset))
        {
            if (file.Length <= 0)
                continue;

            var typeName = FileTypeColors.NormalizeTypeName(file.FileType);
            var color = FileTypeColors.GetColor(typeName);

            _fileRegions.Add(new FileRegion
            {
                Start = file.Offset,
                End = file.Offset + file.Length,
                TypeName = file.FileType,
                Color = color
            });
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_analysisResult != null)
        {
            Render();
        }
    }

    private void Render()
    {
        MinimapCanvas.Children.Clear();

        if (_analysisResult == null || _fileSize == 0)
            return;

        var canvasWidth = MinimapCanvas.ActualWidth;
        var canvasHeight = MinimapCanvas.ActualHeight;

        if (canvasWidth <= 0 || canvasHeight <= 0)
            return;

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
            var startY = (region.Start / (double)_fileSize) * canvasHeight;
            var endY = (region.End / (double)_fileSize) * canvasHeight;
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

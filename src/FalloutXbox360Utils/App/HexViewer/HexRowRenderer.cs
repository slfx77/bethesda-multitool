using System.Globalization;
using System.Text;
using Windows.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace FalloutXbox360Utils;

/// <summary>
///     Handles rendering of hex data rows for the HexViewerControl.
/// </summary>
internal sealed class HexRowRenderer
{
    private const int BytesPerRow = 16;

    private static readonly SolidColorBrush TextBrush = new(Color.FromArgb(255, 212, 212, 212));
    private static readonly SolidColorBrush HighlightBrush = new(Color.FromArgb(255, 255, 140, 0)); // Orange
    private readonly TextBlock _asciiTextBlock;

    // Brush cache to avoid repeated allocations during rendering
    private readonly Dictionary<Color, SolidColorBrush> _brushCache = new();
    private readonly Func<long, FileRegion?> _findRegion;
    private readonly TextBlock _hexTextBlock;
    private readonly TextBlock _offsetTextBlock;

    public HexRowRenderer(
        TextBlock offsetTextBlock,
        TextBlock hexTextBlock,
        TextBlock asciiTextBlock,
        Func<long, FileRegion?> findRegion)
    {
        _offsetTextBlock = offsetTextBlock;
        _hexTextBlock = hexTextBlock;
        _asciiTextBlock = asciiTextBlock;
        _findRegion = findRegion;
    }

    // Highlight range for current search result
    public long HighlightStart { get; set; } = -1;
    public long HighlightEnd { get; set; } = -1;

    public void Clear()
    {
        _offsetTextBlock.Text = "";
        _hexTextBlock.Inlines.Clear();
        _asciiTextBlock.Text = "";
    }

    public void RenderRows(byte[] buffer, long startRow, long endRow, long startOffset, long fileSize)
    {
        Clear();

        var offsetBuilder = new StringBuilder();
        var asciiBuilder = new StringBuilder();

        for (var row = startRow; row < endRow; row++)
        {
            var rowOffset = row * BytesPerRow;
            var bufferOffset = (int)(rowOffset - startOffset);
            var rowBytes = (int)Math.Min(BytesPerRow, fileSize - rowOffset);
            if (rowBytes <= 0) break;

            // Offset column
            offsetBuilder.Append(CultureInfo.InvariantCulture, $"{rowOffset:X8}");
            if (row < endRow - 1) offsetBuilder.Append('\n');

            // Hex bytes with per-byte coloring
            RenderHexRow(buffer, bufferOffset, rowOffset, rowBytes);

            // ASCII column
            RenderAsciiRow(asciiBuilder, buffer, bufferOffset, rowBytes);

            // Newline (except for last row)
            if (row < endRow - 1)
            {
                _hexTextBlock.Inlines.Add(new Run { Text = "\n" });
                asciiBuilder.Append('\n');
            }
        }

        _offsetTextBlock.Text = offsetBuilder.ToString();
        _asciiTextBlock.Text = asciiBuilder.ToString();
    }

    private void RenderHexRow(byte[] buffer, int bufferOffset, long rowOffset, int rowBytes)
    {
        // Find region for start of row (single lookup instead of per-byte)
        var currentRegion = _findRegion(rowOffset);
        var currentBrush = GetRegionBrush(currentRegion);
        var hexBuilder = new StringBuilder();

        for (var i = 0; i < BytesPerRow; i++)
        {
            if (i < rowBytes && bufferOffset + i < buffer.Length)
            {
                var byteOffset = rowOffset + i;
                var b = buffer[bufferOffset + i];

                // Check if this byte is within the highlight range (search result)
                var isHighlighted = HighlightStart >= 0 && byteOffset >= HighlightStart && byteOffset < HighlightEnd;

                // Check if we've crossed into a new region (only check when not highlighted)
                if (!isHighlighted && currentRegion != null && byteOffset >= currentRegion.End)
                {
                    // Flush accumulated hex text with current brush
                    if (hexBuilder.Length > 0)
                    {
                        _hexTextBlock.Inlines.Add(new Run { Text = hexBuilder.ToString(), Foreground = currentBrush });
                        hexBuilder.Clear();
                    }

                    // Find new region
                    currentRegion = _findRegion(byteOffset);
                    currentBrush = GetRegionBrush(currentRegion);
                }

                // If highlighted, flush and use highlight brush
                if (isHighlighted)
                {
                    if (hexBuilder.Length > 0)
                    {
                        _hexTextBlock.Inlines.Add(new Run { Text = hexBuilder.ToString(), Foreground = currentBrush });
                        hexBuilder.Clear();
                    }

                    _hexTextBlock.Inlines.Add(new Run { Text = $"{b:X2} ", Foreground = HighlightBrush });
                }
                else
                {
                    hexBuilder.Append($"{b:X2} ");
                }
            }
            else
            {
                // Flush current run before adding padding
                if (hexBuilder.Length > 0)
                {
                    _hexTextBlock.Inlines.Add(new Run { Text = hexBuilder.ToString(), Foreground = currentBrush });
                    hexBuilder.Clear();
                }

                _hexTextBlock.Inlines.Add(new Run { Text = "   ", Foreground = TextBrush });
            }
        }

        // Flush any remaining hex text
        if (hexBuilder.Length > 0)
        {
            _hexTextBlock.Inlines.Add(new Run { Text = hexBuilder.ToString(), Foreground = currentBrush });
        }
    }

    private SolidColorBrush GetRegionBrush(FileRegion? region)
    {
        if (region == null)
        {
            return TextBrush;
        }

        if (!_brushCache.TryGetValue(region.Color, out var brush))
        {
            brush = new SolidColorBrush(region.Color);
            _brushCache[region.Color] = brush;
        }

        return brush;
    }

    private static void RenderAsciiRow(StringBuilder asciiBuilder, byte[] buffer, int bufferOffset, int rowBytes)
    {
        for (var i = 0; i < rowBytes && bufferOffset + i < buffer.Length; i++)
        {
            var b = buffer[bufferOffset + i];
            asciiBuilder.Append(b is >= 32 and < 127 ? (char)b : '.');
        }
    }
}

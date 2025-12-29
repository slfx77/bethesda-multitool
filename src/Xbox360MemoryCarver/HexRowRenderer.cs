using System.Globalization;
using System.Text;
using Windows.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Xbox360MemoryCarver.App.HexViewer;

/// <summary>
///     Handles rendering of hex data rows for the HexViewerControl.
/// </summary>
internal sealed class HexRowRenderer
{
    private const int BytesPerRow = 16;

    private static readonly SolidColorBrush TextBrush = new(Color.FromArgb(255, 212, 212, 212));
    private readonly TextBlock _asciiTextBlock;
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
            if (rowBytes <= 0)
            {
                break;
            }

            // Offset column
            offsetBuilder.Append(CultureInfo.InvariantCulture, $"{rowOffset:X8}");
            if (row < endRow - 1)
            {
                offsetBuilder.Append('\n');
            }

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
        for (var i = 0; i < BytesPerRow; i++)
        {
            if (i < rowBytes && bufferOffset + i < buffer.Length)
            {
                var byteOffset = rowOffset + i;
                var b = buffer[bufferOffset + i];
                var region = _findRegion(byteOffset);
                _hexTextBlock.Inlines.Add(new Run
                {
                    Text = $"{b:X2} ",
                    Foreground = region != null ? new SolidColorBrush(region.Color) : TextBrush
                });
            }
            else
            {
                _hexTextBlock.Inlines.Add(new Run { Text = "   ", Foreground = TextBrush });
            }
        }
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

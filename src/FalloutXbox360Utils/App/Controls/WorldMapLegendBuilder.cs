using FalloutXbox360Utils.Core.Formats;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace FalloutXbox360Utils;

/// <summary>
///     Builds the legend panel UI for the world map, with toggleable category entries.
/// </summary>
internal static class WorldMapLegendBuilder
{
    internal static void Populate(
        StackPanel panel,
        HashSet<PlacedObjectCategory> hiddenCategories,
        Action invalidateCanvas)
    {
        panel.Children.Clear();
        var grayBorder = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(255, 100, 100, 100));
        var grayFill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
        var graySwatchBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(255, 100, 100, 100));
        var whiteBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.White);
        var dimTextBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(128, 255, 255, 255));

        foreach (var category in Enum.GetValues<PlacedObjectCategory>())
        {
            var color = WorldMapColors.GetCategoryColor(category);
            var colorBorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            var colorFillBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Color.FromArgb(60, color.R, color.G, color.B));
            var colorSwatchBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);

            var swatch = new Border
            {
                Width = 10, Height = 10,
                CornerRadius = new CornerRadius(category == PlacedObjectCategory.MapMarker ? 5 : 2),
                Background = colorSwatchBrush
            };
            var label = new TextBlock
            {
                Text = WorldMapColors.GetCategoryDisplayName(category),
                FontSize = 10, Foreground = whiteBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            content.Children.Add(swatch);
            content.Children.Add(label);

            var item = new Border
            {
                Child = content, BorderBrush = colorBorderBrush, Background = colorFillBrush,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var enabled = true;
            var capturedCategory = category;

            item.PointerPressed += (_, args) => args.Handled = true;
            item.PointerReleased += (_, args) =>
            {
                args.Handled = true;
                enabled = !enabled;
                if (enabled)
                {
                    hiddenCategories.Remove(capturedCategory);
                    item.BorderBrush = colorBorderBrush;
                    item.Background = colorFillBrush;
                    swatch.Background = colorSwatchBrush;
                    label.Foreground = whiteBrush;
                }
                else
                {
                    hiddenCategories.Add(capturedCategory);
                    item.BorderBrush = grayBorder;
                    item.Background = grayFill;
                    swatch.Background = graySwatchBrush;
                    label.Foreground = dimTextBrush;
                }

                invalidateCanvas();
            };

            panel.Children.Add(item);
        }
    }
}

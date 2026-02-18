using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FalloutXbox360Utils;

/// <summary>
///     Helper methods: Format*, Get*, utility methods, templates
/// </summary>
public sealed partial class SingleFileTab
{
    #region Formatting Helpers

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes:N0} B"
        };
    }

    #endregion

    #region Search Helpers

    /// <summary>
    ///     Binary search to find which line a character offset falls on.
    /// </summary>
    private int FindLineForCharOffset(int charOffset)
    {
        var lo = 0;
        var hi = _reportLineOffsets.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_reportLineOffsets[mid] <= charOffset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return Math.Max(0, lo - 1);
    }

    #endregion

    #region Template Helpers

    private static DataTemplate CreateCoverageGapItemTemplate()
    {
        var xaml = """
                   <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                       <Grid Padding="8,2">
                           <Grid.ColumnDefinitions>
                               <ColumnDefinition Width="50" />
                               <ColumnDefinition Width="110" />
                               <ColumnDefinition Width="90" />
                               <ColumnDefinition Width="140" />
                               <ColumnDefinition Width="*" />
                           </Grid.ColumnDefinitions>
                           <TextBlock Grid.Column="0" FontSize="11" Text="{Binding Index}" />
                           <TextBlock Grid.Column="1" FontFamily="Consolas" FontSize="11" Text="{Binding FileOffset}" />
                           <TextBlock Grid.Column="2" FontSize="11" Text="{Binding Size}" />
                           <TextBlock Grid.Column="3" FontSize="11" Text="{Binding Classification}" />
                           <TextBlock Grid.Column="4" FontSize="11" Text="{Binding Context}" TextTrimming="CharacterEllipsis" />
                       </Grid>
                   </DataTemplate>
                   """;
        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }

    #endregion

    #region Property Panel Helpers

    /// <summary>
    ///     Adds a category header row to a property panel grid.
    /// </summary>
    private static void AddCategoryHeader(
        Grid grid, string category, int row, int columnSpan,
        Microsoft.UI.Xaml.Media.SolidColorBrush foregroundBrush)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var categoryBgBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(foregroundBrush.Color) { Opacity = 0.12 };
        var categoryBg = new Border { Background = categoryBgBrush };
        Grid.SetRow(categoryBg, row);
        Grid.SetColumnSpan(categoryBg, columnSpan);
        grid.Children.Add(categoryBg);

        var categoryHeader = new TextBlock
        {
            Text = category,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground =
                (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Margin = new Thickness(8, 5, 0, 7)
        };
        Grid.SetRow(categoryHeader, row);
        Grid.SetColumnSpan(categoryHeader, columnSpan);
        grid.Children.Add(categoryHeader);
    }

    /// <summary>
    ///     Adds an alternating row background if the row index is odd.
    /// </summary>
    private static void AddAlternatingRowBackground(
        Grid grid, int row, int columnSpan, int propertyRowIndex,
        Microsoft.UI.Xaml.Media.SolidColorBrush altRowBrush)
    {
        if (propertyRowIndex % 2 == 1)
        {
            var bgBorder = new Border { Background = altRowBrush };
            Grid.SetRow(bgBorder, row);
            Grid.SetColumnSpan(bgBorder, columnSpan);
            grid.Children.Add(bgBorder);
        }
    }

    /// <summary>
    ///     Toggles an expandable section's visibility and updates the icon arrow.
    /// </summary>
    private static void ToggleExpandSection(TextBlock expandIcon, UIElement subItemsContainer)
    {
        var isCollapsed = subItemsContainer.Visibility == Visibility.Collapsed;
        subItemsContainer.Visibility = isCollapsed ? Visibility.Visible : Visibility.Collapsed;
        expandIcon.Text = isCollapsed ? "\u25BC" : "\u25B6";
    }

    /// <summary>
    ///     Creates theme-aware brushes for property panel alternating rows.
    /// </summary>
    private static Microsoft.UI.Xaml.Media.SolidColorBrush CreateAlternatingRowBrush()
    {
        var foregroundBrush = (Microsoft.UI.Xaml.Media.SolidColorBrush)
            Application.Current.Resources["TextFillColorPrimaryBrush"];
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(foregroundBrush.Color) { Opacity = 0.05 };
    }

    #endregion

    #region Dialog Helpers

    private bool _isDialogOpen;

    private async Task ShowDialogAsync(string title, string message, bool isError = false)
    {
        if (XamlRoot == null)
        {
            System.Diagnostics.Debug.WriteLine($"[UI] Cannot show dialog (no XamlRoot): {title} — {message}");
            return;
        }

        if (_isDialogOpen)
        {
            System.Diagnostics.Debug.WriteLine($"[UI] Dialog already open, skipping: {title} — {message}");
            return;
        }

        _isDialogOpen = true;
        try
        {
            if (isError)
            {
                await ErrorDialogHelper.ShowErrorAsync(title, message, XamlRoot);
            }
            else
            {
                await ErrorDialogHelper.ShowInfoAsync(title, message, XamlRoot);
            }
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    #endregion
}

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

using Windows.System;
using Windows.UI.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Xbox360MemoryCarver;

/// <summary>
///     Helper for showing dependency warning dialogs with clickable download links.
/// </summary>
internal static class DependencyDialogHelper
{
    /// <summary>
    ///     Shows a dialog listing missing dependencies with download links.
    /// </summary>
    /// <param name="result">The dependency check result.</param>
    /// <param name="xamlRoot">XamlRoot for the dialog.</param>
    /// <returns>True if all dependencies are available, false if some are missing.</returns>
    public static async Task<bool> ShowIfMissingAsync(TabDependencyResult result, XamlRoot xamlRoot)
    {
        if (result.AllAvailable) return true;

        var missingDeps = result.Missing.ToList();
        var availableDeps = result.Available.ToList();

        var content = CreateDialogContent(missingDeps, availableDeps);

        var dialog = new ContentDialog
        {
            Title = $"{result.TabName} - Missing Dependencies",
            Content = content,
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };

        await dialog.ShowAsync();
        return false;
    }

    /// <summary>
    ///     Shows an info dialog about available features based on dependencies.
    /// </summary>
    public static async Task ShowStatusAsync(TabDependencyResult result, XamlRoot xamlRoot)
    {
        var content = CreateStatusContent(result);

        var dialog = new ContentDialog
        {
            Title = $"{result.TabName} - Dependency Status",
            Content = content,
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };

        await dialog.ShowAsync();
    }

    private static StackPanel CreateDialogContent(
        List<DependencyStatus> missing,
        List<DependencyStatus> available)
    {
        var panel = new StackPanel { Spacing = 12 };

        // Warning summary
        var warningText = new TextBlock
        {
            Text = missing.Count == 1
                ? "A required dependency is not installed. Some features will be unavailable:"
                : $"{missing.Count} dependencies are not installed. Some features will be unavailable:",
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(warningText);

        // Missing dependencies list
        foreach (var dep in missing) panel.Children.Add(CreateDependencyBlock(dep, true));

        // Available dependencies (collapsed by default if there are many missing)
        if (available.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Available dependencies:",
                Margin = new Thickness(0, 8, 0, 0),
                FontWeight = FontWeights.SemiBold
            });

            foreach (var dep in available) panel.Children.Add(CreateDependencyBlock(dep, false));
        }

        return panel;
    }

    private static StackPanel CreateStatusContent(TabDependencyResult result)
    {
        var panel = new StackPanel { Spacing = 12 };

        if (result.Dependencies.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "This tab has no external dependencies. All features are available.",
                TextWrapping = TextWrapping.Wrap
            });
            return panel;
        }

        if (result.AllAvailable)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "All dependencies are installed. All features are available.",
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            var missingCount = result.Missing.Count();
            panel.Children.Add(new TextBlock
            {
                Text = $"{missingCount} of {result.Dependencies.Count} dependencies are missing.",
                TextWrapping = TextWrapping.Wrap
            });
        }

        foreach (var dep in result.Dependencies) panel.Children.Add(CreateDependencyBlock(dep, !dep.IsAvailable));

        return panel;
    }

    private static Border CreateDependencyBlock(DependencyStatus dep, bool isMissing)
    {
        var innerPanel = new StackPanel { Spacing = 4 };

        // Status icon + name
        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        headerPanel.Children.Add(new FontIcon
        {
            Glyph = isMissing ? "\uE783" : "\uE73E", // Warning or Checkmark
            FontSize = 14,
            Foreground = isMissing
                ? new SolidColorBrush(Colors.Orange)
                : new SolidColorBrush(Colors.Green)
        });
        headerPanel.Children.Add(new TextBlock
        {
            Text = dep.Name + (dep.Version != null ? $" ({dep.Version})" : ""),
            FontWeight = FontWeights.SemiBold
        });
        innerPanel.Children.Add(headerPanel);

        // Description
        innerPanel.Children.Add(new TextBlock
        {
            Text = dep.Description,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(22, 0, 0, 0),
            Opacity = 0.8
        });

        // Path if available
        if (!isMissing && !string.IsNullOrEmpty(dep.Path))
            innerPanel.Children.Add(new TextBlock
            {
                Text = $"Path: {dep.Path}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(22, 0, 0, 0),
                FontSize = 11,
                Opacity = 0.6
            });

        // Download link if missing
        if (isMissing && !string.IsNullOrEmpty(dep.DownloadUrl))
        {
            var linkPanel = new StackPanel
                { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(22, 4, 0, 0) };

            var downloadLink = new HyperlinkButton
            {
                Content = "Download",
                Padding = new Thickness(0)
            };
            downloadLink.Click += async (_, _) => { await Launcher.LaunchUriAsync(new Uri(dep.DownloadUrl)); };
            linkPanel.Children.Add(downloadLink);

            // Show URL as text
            linkPanel.Children.Add(new TextBlock
            {
                Text = $"({dep.DownloadUrl})",
                FontSize = 11,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center
            });

            innerPanel.Children.Add(linkPanel);
        }

        // Installation instructions if missing
        if (isMissing && !string.IsNullOrEmpty(dep.InstallInstructions))
            innerPanel.Children.Add(new TextBlock
            {
                Text = dep.InstallInstructions,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(22, 4, 0, 0),
                FontSize = 12,
                Opacity = 0.7,
                FontStyle = FontStyle.Italic
            });

        return new Border
        {
            Background = isMissing
                ? new SolidColorBrush(Colors.Orange) { Opacity = 0.1 }
                : new SolidColorBrush(Colors.Green) { Opacity = 0.1 },
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Child = innerPanel
        };
    }
}

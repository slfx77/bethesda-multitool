using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Text;

namespace FalloutXbox360Utils;

/// <summary>
///     Builds the property panel Grid for the data browser detail view.
///     All instance-dependent operations (FormID navigation, link creation,
///     cell navigation) are injected via delegates, keeping this class static.
/// </summary>
internal static class PropertyPanelBuilder
{
    /// <summary>
    ///     Builds the main property panel Grid from a list of property entries.
    /// </summary>
    internal static Grid BuildGrid(List<EsmPropertyEntry> properties, Callbacks callbacks)
    {
        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // icon
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // name
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // value col1
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // value col2
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition
            { Width = new GridLength(1, GridUnitType.Star) }); // value col3

        var currentRow = 0;
        var propertyRowIndex = 0;
        string? lastCategory = null;

        var foregroundBrush = (Microsoft.UI.Xaml.Media.SolidColorBrush)
            Application.Current.Resources["TextFillColorPrimaryBrush"];
        var altRowBrush = CreateAlternatingRowBrush();

        foreach (var prop in properties)
        {
            if (prop.Category != null && prop.Category != lastCategory)
            {
                lastCategory = prop.Category;
                propertyRowIndex = 0;
                AddCategoryHeader(mainGrid, prop.Category, currentRow, 5, foregroundBrush);
                currentRow++;
            }

            if (prop.IsExpandable && prop.SubItems?.Count > 0)
            {
                AddExpandableRow(mainGrid, prop, ref currentRow, ref propertyRowIndex, altRowBrush, callbacks);
            }
            else
            {
                AddSimpleRow(mainGrid, prop, ref currentRow, ref propertyRowIndex, altRowBrush, callbacks);
            }
        }

        return mainGrid;
    }

    private static void AddExpandableRow(
        Grid mainGrid, EsmPropertyEntry prop, ref int currentRow, ref int propertyRowIndex,
        Microsoft.UI.Xaml.Media.SolidColorBrush altRowBrush, Callbacks callbacks)
    {
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddAlternatingRowBackground(mainGrid, currentRow, 5, propertyRowIndex, altRowBrush);

        var expandIcon = new TextBlock
        {
            Text = prop.IsExpandedByDefault ? "\u25BC" : "\u25B6",
            FontSize = 10,
            Width = 18,
            Padding = new Thickness(4, 3, 0, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground =
                (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        Grid.SetRow(expandIcon, currentRow);
        Grid.SetColumn(expandIcon, 0);
        mainGrid.Children.Add(expandIcon);

        var nameText = new TextBlock
        {
            Text = prop.Name,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Padding = new Thickness(0, 3, 16, 2),
            IsTextSelectionEnabled = true
        };
        Grid.SetRow(nameText, currentRow);
        Grid.SetColumn(nameText, 1);
        mainGrid.Children.Add(nameText);

        var countText = new TextBlock
        {
            Text = prop.Value,
            FontSize = 12,
            Padding = new Thickness(0, 3, 4, 2),
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Foreground =
                (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        Grid.SetRow(countText, currentRow);
        Grid.SetColumn(countText, 2);
        Grid.SetColumnSpan(countText, 3);
        mainGrid.Children.Add(countText);

        currentRow++;

        // Build sub-items grid
        var subItemsGrid = BuildSubItemsGrid(prop, callbacks);

        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var subItemsContainer = new Border
        {
            Child = subItemsGrid,
            Margin = new Thickness(18, 0, 0, 0)
        };
        Grid.SetRow(subItemsContainer, currentRow);
        Grid.SetColumn(subItemsContainer, 1);
        Grid.SetColumnSpan(subItemsContainer, 4);
        mainGrid.Children.Add(subItemsContainer);
        currentRow++;

        // Click handlers for expand/collapse
        var capturedIcon = expandIcon;
        var capturedSubItems = subItemsGrid;
        expandIcon.PointerPressed += (_, _) => ToggleExpandSection(capturedIcon, capturedSubItems);
        nameText.PointerPressed += (_, _) => ToggleExpandSection(capturedIcon, capturedSubItems);
        countText.PointerPressed += (_, _) => ToggleExpandSection(capturedIcon, capturedSubItems);

        propertyRowIndex++;
    }

    private static void AddSimpleRow(
        Grid mainGrid, EsmPropertyEntry prop, ref int currentRow, ref int propertyRowIndex,
        Microsoft.UI.Xaml.Media.SolidColorBrush altRowBrush, Callbacks callbacks)
    {
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddAlternatingRowBackground(mainGrid, currentRow, 5, propertyRowIndex, altRowBrush);

        var spacer = new TextBlock { Width = 18, Padding = new Thickness(4, 3, 0, 2) };
        Grid.SetRow(spacer, currentRow);
        Grid.SetColumn(spacer, 0);
        mainGrid.Children.Add(spacer);

        var nameText = new TextBlock
        {
            Text = prop.Name,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Padding = new Thickness(0, 3, 16, 2),
            IsTextSelectionEnabled = true
        };
        Grid.SetRow(nameText, currentRow);
        Grid.SetColumn(nameText, 1);
        mainGrid.Children.Add(nameText);

        if (prop.LinkedFormId is > 0 && callbacks.IsFormIdNavigable(prop.LinkedFormId.Value))
        {
            var link = callbacks.CreateFormIdLink(prop.Value, prop.LinkedFormId.Value, 12, true);
            link.Margin = new Thickness(0, 2, 4, 2);
            Grid.SetRow(link, currentRow);
            Grid.SetColumn(link, 2);
            Grid.SetColumnSpan(link, 3);
            mainGrid.Children.Add(link);
        }
        else
        {
            var valueText = new TextBlock
            {
                Text = prop.Value,
                FontSize = 12,
                Padding = new Thickness(0, 3, 4, 2),
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };
            Grid.SetRow(valueText, currentRow);
            Grid.SetColumn(valueText, 2);
            Grid.SetColumnSpan(valueText, 3);
            mainGrid.Children.Add(valueText);
        }

        propertyRowIndex++;
        currentRow++;
    }

    private static Grid BuildSubItemsGrid(EsmPropertyEntry prop, Callbacks callbacks)
    {
        var subItemsGrid = new Grid
        {
            Visibility = prop.IsExpandedByDefault ? Visibility.Visible : Visibility.Collapsed
        };
        subItemsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        subItemsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        subItemsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        subItemsGrid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var subRow = 0;
        foreach (var sub in prop.SubItems!)
        {
            subItemsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            if (sub.Col1 != null || sub.Col2 != null || sub.Col3 != null || sub.Col4 != null)
            {
                AddMultiColumnSubItem(subItemsGrid, sub, subRow, callbacks);
            }
            else if (string.IsNullOrEmpty(sub.Name) && sub.LinkedFormId == null)
            {
                AddValueOnlySubItem(subItemsGrid, sub, subRow, prop);
            }
            else if (sub.LinkedFormId is > 0 && callbacks.IsFormIdNavigable(sub.LinkedFormId.Value))
            {
                var link = callbacks.CreateFormIdLink(sub.Name ?? "", sub.LinkedFormId.Value, 11, true);
                link.Margin = new Thickness(0, 0, 4, 0);
                Grid.SetRow(link, subRow);
                Grid.SetColumnSpan(link, 4);
                subItemsGrid.Children.Add(link);
            }
            else
            {
                AddNameValueSubItem(subItemsGrid, sub, subRow);
            }

            subRow++;
        }

        return subItemsGrid;
    }

    private static void AddMultiColumnSubItem(
        Grid grid, EsmPropertyEntry sub, int row, Callbacks callbacks)
    {
        // Col1: cell navigation link, FormID link, or plain text
        if (sub.CellNavigationFormId is > 0)
        {
            var cellLink = new HyperlinkButton
            {
                Content = new TextBlock
                {
                    Text = sub.Col1 ?? "",
                    FontSize = 11,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    TextDecorations = TextDecorations.Underline,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 350
                },
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                Margin = new Thickness(0, 1, 12, 1)
            };
            StripButtonChrome(cellLink);
            var capturedCellFormId = sub.CellNavigationFormId.Value;
            cellLink.Click += async (_, _) => await callbacks.NavigateToCellInWorldMap(capturedCellFormId);
            Grid.SetRow(cellLink, row);
            Grid.SetColumn(cellLink, 0);
            grid.Children.Add(cellLink);
        }
        else if (sub.LinkedFormId is > 0 && callbacks.IsFormIdNavigable(sub.LinkedFormId.Value))
        {
            var col1Link = callbacks.CreateFormIdLink(sub.Col1 ?? "", sub.LinkedFormId.Value, 11, true);
            col1Link.Margin = new Thickness(0, 0, 12, 0);
            col1Link.MaxWidth = 350;
            Grid.SetRow(col1Link, row);
            Grid.SetColumn(col1Link, 0);
            grid.Children.Add(col1Link);
        }
        else
        {
            var col1Text = new TextBlock
            {
                Text = sub.Col1 ?? "",
                FontSize = 11,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                Padding = new Thickness(0, 1, 12, 1),
                IsTextSelectionEnabled = true,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 350
            };
            Grid.SetRow(col1Text, row);
            Grid.SetColumn(col1Text, 0);
            grid.Children.Add(col1Text);
        }

        var col2Text = new TextBlock
        {
            Text = sub.Col2 ?? "",
            FontSize = 11,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Padding = new Thickness(0, 1, 12, 1),
            IsTextSelectionEnabled = true
        };
        Grid.SetRow(col2Text, row);
        Grid.SetColumn(col2Text, 1);
        grid.Children.Add(col2Text);

        // Col3
        if (sub.Col3FormId is > 0 && callbacks.IsFormIdNavigable(sub.Col3FormId.Value))
        {
            var col3Link = callbacks.CreateFormIdLink(sub.Col3 ?? "", sub.Col3FormId.Value, 11, true);
            col3Link.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetRow(col3Link, row);
            Grid.SetColumn(col3Link, 2);
            grid.Children.Add(col3Link);
        }
        else
        {
            var col3Text = new TextBlock
            {
                Text = sub.Col3 ?? "",
                FontSize = 11,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                Padding = new Thickness(0, 1, 12, 1),
                IsTextSelectionEnabled = true
            };
            Grid.SetRow(col3Text, row);
            Grid.SetColumn(col3Text, 2);
            grid.Children.Add(col3Text);
        }

        // Col4
        if (sub.Col4FormId is > 0 && callbacks.IsFormIdNavigable(sub.Col4FormId.Value))
        {
            var col4Link = callbacks.CreateFormIdLink(sub.Col4 ?? "", sub.Col4FormId.Value, 11, true);
            col4Link.Margin = new Thickness(0, 0, 4, 0);
            Grid.SetRow(col4Link, row);
            Grid.SetColumn(col4Link, 3);
            grid.Children.Add(col4Link);
        }
        else
        {
            var col4Text = new TextBlock
            {
                Text = sub.Col4 ?? "",
                FontSize = 11,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                Padding = new Thickness(0, 1, 4, 1),
                IsTextSelectionEnabled = true
            };
            Grid.SetRow(col4Text, row);
            Grid.SetColumn(col4Text, 3);
            grid.Children.Add(col4Text);
        }
    }

    private static void AddValueOnlySubItem(
        Grid grid, EsmPropertyEntry sub, int row, EsmPropertyEntry parentProp)
    {
        var valText = new TextBlock
        {
            Text = sub.Value,
            FontSize = 11,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Padding = new Thickness(0, 1, 0, 1)
        };

        FrameworkElement element;
        if (sub.Value != null && sub.Value.Contains('\n') && !parentProp.IsExpandedByDefault)
        {
            element = new ScrollViewer
            {
                Content = valText,
                MaxHeight = 400,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }
        else
        {
            element = valText;
        }

        Grid.SetRow(element, row);
        Grid.SetColumnSpan(element, 4);
        grid.Children.Add(element);
    }

    private static void AddNameValueSubItem(Grid grid, EsmPropertyEntry sub, int row)
    {
        var subNameText = new TextBlock
        {
            Text = sub.Name,
            FontSize = 11,
            Padding = new Thickness(0, 1, 16, 1),
            IsTextSelectionEnabled = true
        };
        Grid.SetRow(subNameText, row);
        Grid.SetColumnSpan(subNameText, 2);
        grid.Children.Add(subNameText);

        var valText = new TextBlock
        {
            Text = sub.Value,
            FontSize = 11,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Padding = new Thickness(0, 1, 4, 1),
            IsTextSelectionEnabled = true
        };
        Grid.SetRow(valText, row);
        Grid.SetColumn(valText, 2);
        Grid.SetColumnSpan(valText, 2);
        grid.Children.Add(valText);
    }

    /// <summary>
    ///     Callbacks that connect the property panel to instance-specific navigation features.
    /// </summary>
    internal sealed class Callbacks
    {
        /// <summary>Returns true if the given FormID can be navigated to in the browser.</summary>
        public required Func<uint, bool> IsFormIdNavigable { get; init; }

        /// <summary>Creates a HyperlinkButton styled as an underlined FormID link.</summary>
        public required Func<string, uint, int, bool, HyperlinkButton> CreateFormIdLink { get; init; }

        /// <summary>Navigates to a cell in the world map (populates map, navigates, switches tab).</summary>
        public required Func<uint, Task> NavigateToCellInWorldMap { get; init; }
    }

    #region Record Breakdown Cards

    /// <summary>
    ///     Builds a themed category card (title + record label/count grid) for the record breakdown panel.
    /// </summary>
    internal static Border BuildCategoryCard(string title, (string Label, int Count)[] records)
    {
        var panel = new StackPanel { Spacing = 2 };

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        for (var i = 0; i < records.Length; i++)
        {
            var (label, count) = records[i];
            if (count == 0) continue;

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = grid.RowDefinitions.Count - 1;

            var labelText = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Padding = new Thickness(0, 1, 8, 1)
            };
            Grid.SetRow(labelText, row);
            Grid.SetColumn(labelText, 0);
            grid.Children.Add(labelText);

            var countText = new TextBlock
            {
                Text = count.ToString("N0"),
                FontSize = 12,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(0, 1, 0, 1)
            };
            Grid.SetRow(countText, row);
            Grid.SetColumn(countText, 1);
            grid.Children.Add(countText);
        }

        panel.Children.Add(grid);

        return new Border
        {
            Child = panel,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(4),
            Background =
                (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]
        };
    }

    /// <summary>
    ///     Builds a 3-column flowing grid with category cards distributed round-robin.
    /// </summary>
    internal static Grid BuildThreeColumnCardLayout((string Name, (string Label, int Count)[] Records)[] categories)
    {
        var columnsGrid = new Grid();
        columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16, GridUnitType.Pixel) });
        columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16, GridUnitType.Pixel) });
        columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var columnPanels = new StackPanel[3];
        for (var c = 0; c < 3; c++)
        {
            columnPanels[c] = new StackPanel { Spacing = 12 };
            Grid.SetColumn(columnPanels[c], c * 2);
            columnsGrid.Children.Add(columnPanels[c]);
        }

        for (var i = 0; i < categories.Length; i++)
        {
            var (name, records) = categories[i];
            var card = BuildCategoryCard(name, records);
            columnPanels[i % 3].Children.Add(card);
        }

        return columnsGrid;
    }

    #endregion

    #region UI Helpers (shared with SingleFileTab.Helpers.cs)

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

    private static void ToggleExpandSection(TextBlock expandIcon, UIElement subItemsContainer)
    {
        var isCollapsed = subItemsContainer.Visibility == Visibility.Collapsed;
        subItemsContainer.Visibility = isCollapsed ? Visibility.Visible : Visibility.Collapsed;
        expandIcon.Text = isCollapsed ? "\u25BC" : "\u25B6";
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush CreateAlternatingRowBrush()
    {
        var foregroundBrush = (Microsoft.UI.Xaml.Media.SolidColorBrush)
            Application.Current.Resources["TextFillColorPrimaryBrush"];
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(foregroundBrush.Color) { Opacity = 0.05 };
    }

    /// <summary>
    ///     Strips button chrome from a HyperlinkButton so it renders as an inline text link.
    /// </summary>
    private static void StripButtonChrome(HyperlinkButton link)
    {
        link.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        link.BorderThickness = new Thickness(0);
        link.MinWidth = 0;
        link.MinHeight = 0;
    }

    #endregion
}

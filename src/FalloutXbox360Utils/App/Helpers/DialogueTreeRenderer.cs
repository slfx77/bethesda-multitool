using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FalloutXbox360Utils;

/// <summary>
///     Static UI element builders for the dialogue conversation display.
///     All methods are static — they create WinUI elements using Application.Current.Resources
///     but never access instance state or code-behind fields.
/// </summary>
internal static class DialogueTreeRenderer
{
    /// <summary>
    ///     Creates the styled player prompt block (blue "Player" header + quoted text).
    /// </summary>
    public static Border CreatePlayerPromptBlock(string promptText, Border? recordDetailPanel = null)
    {
        var content = new StackPanel { Spacing = 4 };

        // Player label
        var labelPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        labelPanel.Children.Add(new FontIcon
        {
            Glyph = "\uE77B",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
        });
        labelPanel.Children.Add(new TextBlock
        {
            Text = "Player",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
        });
        content.Children.Add(labelPanel);

        // Prompt text
        content.Children.Add(new TextBlock
        {
            Text = promptText,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(22, 2, 0, 2)
        });

        if (recordDetailPanel != null)
        {
            content.Children.Add(recordDetailPanel);
        }

        return new Border
        {
            Child = content,
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 8)
        };
    }

    /// <summary>
    ///     Creates a speech challenge banner with shield icon and difficulty name.
    /// </summary>
    public static Border CreateSpeechChallengeBanner(string difficultyName)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        panel.Children.Add(new FontIcon
        {
            Glyph = "\uE8D4", // Shield icon
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"]
        });

        panel.Children.Add(new TextBlock
        {
            Text = $"Speech Challenge \u2014 {difficultyName}",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"]
        });

        return new Border
        {
            Child = panel,
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 0, 4)
        };
    }

    /// <summary>
    ///     Creates a small metadata tag badge (emotion, goodbye, say once, etc.).
    /// </summary>
    public static Border CreateMetadataTag(string text)
    {
        return new Border
        {
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            },
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(6, 2, 6, 2)
        };
    }

    /// <summary>
    ///     Wraps a collapsed detail Grid in a toggle panel with a clickable "Record Details" header.
    /// </summary>
    public static Border WrapInTogglePanel(Grid detailGrid, Thickness toggleMargin, Thickness borderMargin)
    {
        var toggleIcon = new TextBlock
        {
            Text = "\u25B6",
            FontSize = 9,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        var toggleText = new TextBlock
        {
            Text = "Record Details",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        var togglePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = toggleMargin
        };
        togglePanel.Children.Add(toggleIcon);
        togglePanel.Children.Add(toggleText);
        togglePanel.PointerPressed += (_, _) =>
        {
            var isCollapsed = detailGrid.Visibility == Visibility.Collapsed;
            detailGrid.Visibility = isCollapsed ? Visibility.Visible : Visibility.Collapsed;
            toggleIcon.Text = isCollapsed ? "\u25BC" : "\u25B6";
        };

        var container = new StackPanel();
        container.Children.Add(togglePanel);
        container.Children.Add(detailGrid);

        return new Border
        {
            Child = container,
            Margin = borderMargin
        };
    }

    /// <summary>Populates a two-column detail Grid from detail rows, with optional FormID link creation.</summary>
    public static Border BuildDetailPanel(
        List<DialogueRecordDetailBuilder.DetailRow> rows,
        Thickness gridMargin,
        Thickness toggleMargin,
        Thickness borderMargin,
        Func<string, uint, int, bool, FrameworkElement>? createLink = null)
    {
        var detailGrid = new Grid { Visibility = Visibility.Collapsed, Margin = gridMargin };
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var row = 0;
        foreach (var entry in rows)
        {
            if (string.IsNullOrEmpty(entry.Value) && entry.LinkFormId is null or 0)
            {
                continue;
            }

            detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var nameBlock = new TextBlock
            {
                Text = entry.Label,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Padding = new Thickness(0, 1, 12, 1)
            };
            Grid.SetRow(nameBlock, row);
            Grid.SetColumn(nameBlock, 0);
            detailGrid.Children.Add(nameBlock);

            if (entry.LinkFormId is > 0 && createLink != null)
            {
                var link = createLink(
                    entry.Value ?? $"0x{entry.LinkFormId.Value:X8}",
                    entry.LinkFormId.Value, 11, true);
                Grid.SetRow(link, row);
                Grid.SetColumn(link, 1);
                detailGrid.Children.Add(link);
            }
            else
            {
                var valueBlock = new TextBlock
                {
                    Text = entry.Value ?? "",
                    FontSize = 11,
                    FontFamily = new FontFamily("Consolas"),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Padding = new Thickness(0, 1, 0, 1)
                };
                Grid.SetRow(valueBlock, row);
                Grid.SetColumn(valueBlock, 1);
                detailGrid.Children.Add(valueBlock);
            }

            row++;
        }

        return WrapInTogglePanel(detailGrid, toggleMargin, borderMargin);
    }

    /// <summary>
    ///     Builds the NPC speaker header panel (icon + name + optional FormID).
    /// </summary>
    public static StackPanel BuildSpeakerHeader(string speakerName, uint? speakerFormId)
    {
        var speakerPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        speakerPanel.Children.Add(new FontIcon
        {
            Glyph = "\uE77B",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        speakerPanel.Children.Add(new TextBlock
        {
            Text = speakerName,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });

        if (speakerFormId is > 0)
        {
            speakerPanel.Children.Add(new TextBlock
            {
                Text = $"0x{speakerFormId.Value:X8}",
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        }

        return speakerPanel;
    }

    /// <summary>
    ///     Creates a quoted response TextBlock for dialogue text.
    /// </summary>
    public static TextBlock CreateResponseText(string text, bool isSubtitleFallback = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(22, 2, 0, 2)
        };
        if (isSubtitleFallback)
        {
            block.FontStyle = Windows.UI.Text.FontStyle.Italic;
        }

        return block;
    }

    /// <summary>
    ///     Creates the small "(from subtitles CSV)" attribution label.
    /// </summary>
    public static TextBlock CreateSubtitleSourceLabel()
    {
        return new TextBlock
        {
            Text = "(from subtitles CSV)",
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Margin = new Thickness(22, 0, 0, 2)
        };
    }

    /// <summary>
    ///     Creates a horizontal divider for separating alternative dialogue responses.
    /// </summary>
    public static Border CreateResponseSeparator()
    {
        return new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Margin = new Thickness(0, 4, 0, 4),
            Opacity = 0.5
        };
    }

    /// <summary>
    ///     Creates a secondary italic text label (used for status, notes, etc.).
    /// </summary>
    public static TextBlock CreateSecondaryLabel(string text, Thickness? margin = null)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Margin = margin ?? new Thickness(0)
        };
    }

    /// <summary>
    ///     Creates a larger secondary italic text label (used for end-of-dialogue indicators).
    /// </summary>
    public static TextBlock CreateEndOfDialogueLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Margin = new Thickness(0, 4, 0, 0)
        };
    }

    /// <summary>
    ///     Creates the "No dialogue responses found" placeholder.
    /// </summary>
    public static TextBlock CreateEmptyTopicPlaceholder()
    {
        return new TextBlock
        {
            Text = "No dialogue responses found for this topic.",
            FontSize = 12,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
    }

    /// <summary>
    ///     Builds a metadata tag strip (horizontal StackPanel) from tag strings.
    ///     Returns null if there are no tags.
    /// </summary>
    public static StackPanel? BuildMetadataTagStrip(List<string> tagStrings)
    {
        if (tagStrings.Count == 0)
        {
            return null;
        }

        var tags = new StackPanel
            { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(22, 2, 0, 0) };
        foreach (var tagText in tagStrings)
        {
            tags.Children.Add(CreateMetadataTag(tagText));
        }

        return tags;
    }

    /// <summary>
    ///     Wraps NPC response content in a card-style border.
    /// </summary>
    public static Border WrapInResponseCard(StackPanel content, bool isSelected = false, bool isClickable = false)
    {
        var border = new Border
        {
            Child = content,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8)
        };

        if (isSelected)
        {
            border.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            border.BorderThickness = new Thickness(2);
        }
        else if (isClickable)
        {
            border.Opacity = 0.7;
        }

        return border;
    }

    /// <summary>
    ///     Builds the content panel for a player dialogue choice button, including arrow prefix,
    ///     optional challenge/visited tags, display text, and optional goodbye suffix.
    /// </summary>
    public static StackPanel BuildChoiceContent(
        string displayText, bool isVisited,
        string? challengeOutcome = null, string? speechChallengeDifficulty = null,
        bool isGoodbyeTopic = false)
    {
        var contentPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        // Arrow prefix
        contentPanel.Children.Add(new TextBlock
        {
            Text = "\u203A",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
        });

        // Challenge outcome tag (SUCCEEDED/FAILED)
        if (challengeOutcome != null)
        {
            var isSuccess = challengeOutcome == "SUCCEEDED";
            contentPanel.Children.Add(new Border
            {
                Child = new TextBlock
                {
                    Text = challengeOutcome,
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = isSuccess
                        ? (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"]
                        : (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
                },
                Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 1, 4, 1),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        // Speech challenge tag
        if (speechChallengeDifficulty != null)
        {
            contentPanel.Children.Add(new Border
            {
                Child = new TextBlock
                {
                    Text = $"Speech {speechChallengeDifficulty}",
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"]
                },
                Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 1, 4, 1),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        // Visited tag
        if (isVisited)
        {
            contentPanel.Children.Add(new Border
            {
                Child = new TextBlock
                {
                    Text = "Visited",
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                },
                Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 1, 4, 1),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        // Display text
        var textBlock = new TextBlock
        {
            Text = displayText,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (isVisited)
        {
            textBlock.Opacity = 0.7;
        }

        contentPanel.Children.Add(textBlock);

        // Goodbye suffix
        if (isGoodbyeTopic)
        {
            contentPanel.Children.Add(new TextBlock
            {
                Text = "(ends conversation)",
                FontSize = 11,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        }

        return contentPanel;
    }
}

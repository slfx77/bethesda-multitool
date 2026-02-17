using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FalloutXbox360Utils;

/// <summary>
///     Dialogue Viewer tab: interactive conversation navigation with quest/NPC picker.
/// </summary>
public sealed partial class SingleFileTab
{
    #region Dialogue Viewer Reset

    private void ResetDialogueViewer()
    {
        DialogueViewerPlaceholder.Visibility = Visibility.Visible;
        DialogueViewerContent.Visibility = Visibility.Collapsed;
        DialogueViewerProgressBar.Visibility = Visibility.Collapsed;
        DialogueViewerStatusText.Text = Strings.Empty_RunAnalysisForDialogues;
        DialoguePickerTree.RootNodes.Clear();
        DialogueConversationPanel.Children.Clear();
        DialogueChoicesPanel.Children.Clear();
        DialogueChoicesHeader.Visibility = Visibility.Collapsed;
        DialogueHeaderText.Text = Strings.Empty_SelectDialogueTopic;
        DialogueBrowseMode.SelectedIndex = 0;
        DialogueSearchBox.Text = "";
        _dialoguePickerByQuest = true;
        _visitedTopicFormIds.Clear();
        _currentDialogueTopic = null;
        _dialogueSpeakerFilter = null;
        _dialogueQuestFilter = null;
        _dialogueSearchDebounceToken?.Dispose();
        _dialogueSearchDebounceToken = null;
    }

    #endregion

    #region Cross-Tab Dialogue Navigation

    /// <summary>
    ///     Attempts to navigate to a dialogue record (INFO or DIAL) in the Dialogue Viewer tab.
    ///     Returns true if the FormID was found and navigation was initiated.
    /// </summary>
    private bool TryNavigateToDialogueRecord(uint formId)
    {
        if (_session.DialogueFormIdIndex == null || !_session.DialogueFormIdIndex.TryGetValue(formId, out var topic))
        {
            return false;
        }

        // Push current location before switching tabs (NavigateToFormId already pushed once,
        // but that was before we knew we'd end up in Dialogue instead of Data Browser)

        // Switch to Dialogue Viewer tab
        SubTabView.SelectedItem = DialogueViewerTab;

        // Ensure it's populated
        if (!_session.DialogueViewerPopulated && _session.SemanticResult != null)
        {
            _ = PopulateDialogueViewerAsync();
        }

        // Cross-tab navigation: clear filters
        _dialogueSpeakerFilter = null;
        _dialogueQuestFilter = null;

        // Navigate to the topic
        NavigateToDialogueTopic(topic, pushToStack: _currentDialogueTopic != null);
        return true;
    }

    #endregion

    #region Dialogue Viewer Fields

    private readonly HashSet<uint> _visitedTopicFormIds = new();

    private bool _dialoguePickerByQuest = true;
    private TopicDialogueNode? _currentDialogueTopic;
    private uint? _dialogueSpeakerFilter;
    private uint? _dialogueQuestFilter;
    private CancellationTokenSource? _dialogueSearchDebounceToken;

    // Typed wrappers for TreeView DataObject to disambiguate quest vs NPC picker context
    private sealed record QuestPickerData(uint QuestFormId, List<TopicDialogueNode> Topics);
    private sealed record SpeakerPickerData(uint SpeakerFormId, List<TopicDialogueNode> Topics);
    private sealed record QuestTopicPickerData(uint QuestFormId, TopicDialogueNode Topic);
    private sealed record SpeakerTopicPickerData(uint SpeakerFormId, TopicDialogueNode Topic);

    #endregion

    #region Dialogue Viewer Population

    private async Task PopulateDialogueViewerAsync()
    {
        if (_session.DialogueViewerPopulated)
        {
            return;
        }

        // Show progress
        DialogueViewerProgressBar.Visibility = Visibility.Visible;
        DialogueViewerStatusText.Text = _session.IsEsmFile
            ? Strings.Status_LoadingDialogueData
            : Strings.Status_ReconstructingDialogueData;

        try
        {
            // Ensure semantic reconstruction is complete
            if (_session.SemanticResult == null)
            {
                DialogueViewerProgressBar.IsIndeterminate = false;
                await EnsureSemanticReconstructionAsync();
            }

            var result = _session.SemanticResult;
            if (result?.DialogueTree == null)
            {
                DialogueViewerStatusText.Text = Strings.Status_NoDialogueData;
                return;
            }

            DialogueViewerStatusText.Text = Strings.Status_BuildingDialogueViewer;

            _session.DialogueTree = result.DialogueTree;

            // Resolver is already built in EnsureSemanticReconstructionAsync

            // Build speaker index for NPC browse mode
            // Build set of NPCs with FullName to distinguish real NPCs from marker/template NPCs
            var npcsWithFullName = new HashSet<uint>(
                result.Npcs.Where(n => n.FullName != null).Select(n => n.FormId));
            npcsWithFullName.UnionWith(
                result.Creatures.Where(c => c.FullName != null).Select(c => c.FormId));
            _session.TopicsBySpeaker = BuildTopicsBySpeaker(_session.DialogueTree, npcsWithFullName);

            // Build FormID → topic index for cross-tab navigation
            _session.DialogueFormIdIndex = BuildDialogueFormIdIndex(_session.DialogueTree);

            // Build the picker tree (default: quest mode)
            BuildDialoguePickerTree(_session.DialogueTree, byQuest: true);

            DialogueViewerPlaceholder.Visibility = Visibility.Collapsed;
            DialogueViewerContent.Visibility = Visibility.Visible;
            _session.DialogueViewerPopulated = true;
        }
        finally
        {
            DialogueViewerProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private string ResolveFormName(uint formId)
    {
        return _session.Resolver?.GetBestNameWithRefChain(formId) ?? $"0x{formId:X8}";
    }

    private static Dictionary<uint, List<TopicDialogueNode>> BuildTopicsBySpeaker(
        DialogueTreeResult tree, HashSet<uint> npcsWithFullName)
    {
        var index = new Dictionary<uint, List<TopicDialogueNode>>();

        void IndexTopics(IEnumerable<TopicDialogueNode> topics)
        {
            foreach (var topic in topics)
            {
                var speakerId = ResolveTopicSpeaker(topic, npcsWithFullName);

                if (speakerId is > 0)
                {
                    if (!index.TryGetValue(speakerId.Value, out var list))
                    {
                        list = [];
                        index[speakerId.Value] = list;
                    }

                    list.Add(topic);
                }
            }
        }

        foreach (var quest in tree.QuestTrees.Values)
        {
            IndexTopics(quest.Topics);
        }

        IndexTopics(tree.OrphanTopics);

        return index;
    }

    /// <summary>
    ///     Resolves the speaker for a topic using consensus across all INFOs,
    ///     preferring NPCs with FullName (real named NPCs) over marker/template NPCs.
    /// </summary>
    private static uint? ResolveTopicSpeaker(TopicDialogueNode topic, HashSet<uint> npcsWithFullName)
    {
        // 1. Topic-level TNAM is highest authority
        if (topic.Topic?.SpeakerFormId is > 0)
        {
            return topic.Topic.SpeakerFormId;
        }

        // 2. Consensus across all INFOs, preferring NPCs with FullName
        var speakerCounts = new Dictionary<uint, int>();
        foreach (var speakerId in topic.InfoChain
                     .Select(i => i.Info.SpeakerFormId)
                     .Where(id => id is > 0))
        {
            speakerCounts.TryGetValue(speakerId!.Value, out var c);
            speakerCounts[speakerId.Value] = c + 1;
        }

        if (speakerCounts.Count == 0)
        {
            return null;
        }

        // Prefer speakers with FullName (real named NPCs) over marker/template NPCs
        var withName = speakerCounts
            .Where(kv => npcsWithFullName.Contains(kv.Key))
            .ToList();

        if (withName.Count > 0)
        {
            return withName.MaxBy(kv => kv.Value).Key;
        }

        // Fallback: most common overall
        return speakerCounts.MaxBy(kv => kv.Value).Key;
    }

    private static Dictionary<uint, TopicDialogueNode> BuildDialogueFormIdIndex(DialogueTreeResult tree)
    {
        var index = new Dictionary<uint, TopicDialogueNode>();

        void IndexTopics(IEnumerable<TopicDialogueNode> topics)
        {
            foreach (var topic in topics)
            {
                // Index the topic itself
                index.TryAdd(topic.TopicFormId, topic);

                // Index each INFO by its FormID
                foreach (var info in topic.InfoChain)
                {
                    index.TryAdd(info.Info.FormId, topic);
                }
            }
        }

        foreach (var quest in tree.QuestTrees.Values)
        {
            IndexTopics(quest.Topics);
        }

        IndexTopics(tree.OrphanTopics);

        return index;
    }

    #endregion

    #region Dialogue Picker Tree

    private void BuildDialoguePickerTree(DialogueTreeResult tree, bool byQuest, string? searchQuery = null)
    {
        _dialoguePickerByQuest = byQuest;
        DialoguePickerTree.RootNodes.Clear();

        if (byQuest)
        {
            BuildQuestPickerTree(tree, searchQuery);
        }
        else
        {
            BuildNpcPickerTree(searchQuery);
        }
    }

    private void BuildQuestPickerTree(DialogueTreeResult tree, string? searchQuery)
    {
        var quests = tree.QuestTrees.Values
            .Where(q => q.Topics.Count > 0)
            .OrderBy(q => q.QuestName ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var quest in quests)
        {
            var matchingTopics = FilterTopics(quest.Topics, searchQuery);
            if (matchingTopics.Count == 0)
            {
                continue;
            }

            var questName = quest.QuestName ?? $"0x{quest.QuestFormId:X8}";
            var questNode = new EsmBrowserNode
            {
                DisplayName = questName,
                Detail = $"({matchingTopics.Count})",
                NodeType = "Category",
                IconGlyph = "\uE8BD",
                HasUnrealizedChildren = true,
                DataObject = new QuestPickerData(quest.QuestFormId, matchingTopics)
            };

            var treeNode = new TreeViewNode { Content = questNode, HasUnrealizedChildren = true };
            DialoguePickerTree.RootNodes.Add(treeNode);
        }

        // Orphan topics (no quest context)
        var orphanTopics = FilterTopics(tree.OrphanTopics, searchQuery);
        if (orphanTopics.Count > 0)
        {
            var orphanNode = new EsmBrowserNode
            {
                DisplayName = "Unassigned Topics",
                Detail = $"({orphanTopics.Count})",
                NodeType = "Category",
                IconGlyph = "\uE8BD",
                HasUnrealizedChildren = true,
                DataObject = orphanTopics // No quest context for orphans
            };

            var treeNode = new TreeViewNode { Content = orphanNode, HasUnrealizedChildren = true };
            DialoguePickerTree.RootNodes.Add(treeNode);
        }
    }

    private void BuildNpcPickerTree(string? searchQuery)
    {
        if (_session.TopicsBySpeaker == null)
        {
            return;
        }

        var speakers = _session.TopicsBySpeaker
            .Select(kv => (
                FormId: kv.Key,
                Name: ResolveFormName(kv.Key),
                Topics: FilterTopics(kv.Value, searchQuery)))
            .Where(s => s.Topics.Count > 0)
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var speaker in speakers)
        {
            var speakerNode = new EsmBrowserNode
            {
                DisplayName = speaker.Name,
                Detail = $"({speaker.Topics.Count})",
                NodeType = "Category",
                IconGlyph = "\uE77B",
                HasUnrealizedChildren = true,
                DataObject = new SpeakerPickerData(speaker.FormId, speaker.Topics)
            };

            var treeNode = new TreeViewNode { Content = speakerNode, HasUnrealizedChildren = true };
            DialoguePickerTree.RootNodes.Add(treeNode);
        }
    }

    private static List<TopicDialogueNode> FilterTopics(List<TopicDialogueNode> topics, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return topics;
        }

        return topics.Where(t => TopicMatchesQuery(t, query)).ToList();
    }

    private static bool TopicMatchesQuery(TopicDialogueNode topic, string query)
    {
        // Match topic name / EditorId
        if (topic.TopicName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (topic.Topic?.EditorId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (topic.Topic?.FullName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        // Match response text or prompt text
        return topic.InfoChain.Any(entry =>
            entry.Info.PromptText?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
            entry.Info.Responses.Any(r => r.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true));
    }

#pragma warning disable CA1822 // XAML event handlers cannot be static
    private void DialoguePickerTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
#pragma warning restore CA1822
    {
        if (!args.Node.HasUnrealizedChildren || args.Node.Content is not EsmBrowserNode node)
        {
            return;
        }

        // Extract topics and optional context from DataObject
        List<TopicDialogueNode> topics;
        uint? speakerFormId = null;
        uint? questFormId = null;
        if (node.DataObject is SpeakerPickerData speakerData)
        {
            topics = speakerData.Topics;
            speakerFormId = speakerData.SpeakerFormId;
        }
        else if (node.DataObject is QuestPickerData questData)
        {
            topics = questData.Topics;
            questFormId = questData.QuestFormId;
        }
        else if (node.DataObject is List<TopicDialogueNode> orphanTopics)
        {
            topics = orphanTopics;
        }
        else
        {
            return;
        }

        foreach (var topic in topics.OrderBy(t => t.TopicName ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var topicName = topic.TopicName ?? topic.Topic?.EditorId ?? $"0x{topic.TopicFormId:X8}";
            var infoCount = topic.InfoChain.Count;
            var topicType = topic.Topic?.TopicTypeName;

            // NPC view: show first response text; Quest view: show topic name
            string displayName;
            string detail;
            if (!_dialoguePickerByQuest)
            {
                var firstText = topic.InfoChain
                    .SelectMany(info => info.Info.Responses)
                    .Select(r => r.Text)
                    .FirstOrDefault(t => !string.IsNullOrEmpty(t));

                // Use response text only if it differs substantially from the topic name
                if (firstText != null && !IsSimilarText(firstText, topicName))
                {
                    displayName = firstText.Length > 80 ? firstText[..77] + "..." : firstText;
                }
                else
                {
                    displayName = topicName;
                }

                // Don't repeat topic name in detail — just show type and count
                detail = topicType != null ? $"{topicType} ({infoCount})" : $"({infoCount})";
            }
            else
            {
                displayName = topicName;
                detail = topicType != null ? $"{topicType} ({infoCount})" : $"({infoCount})";
            }

            // Propagate context to topic leaf nodes
            object topicDataObject;
            if (speakerFormId.HasValue)
            {
                topicDataObject = new SpeakerTopicPickerData(speakerFormId.Value, topic);
            }
            else if (questFormId.HasValue)
            {
                topicDataObject = new QuestTopicPickerData(questFormId.Value, topic);
            }
            else
            {
                topicDataObject = topic;
            }

            var topicNode = new EsmBrowserNode
            {
                DisplayName = displayName,
                Detail = detail,
                NodeType = "Record",
                IconGlyph = GetTopicTypeIcon(topic.Topic?.TopicType ?? 0),
                DataObject = topicDataObject
            };

            args.Node.Children.Add(new TreeViewNode { Content = topicNode });
        }

        args.Node.HasUnrealizedChildren = false;
    }

    private void DialoguePickerTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode { Content: EsmBrowserNode browserNode })
        {
            return;
        }

        if (browserNode.DataObject is SpeakerTopicPickerData speakerData)
        {
            // NPC mode: filter InfoChain to this speaker
            _dialogueSpeakerFilter = speakerData.SpeakerFormId;
            _dialogueQuestFilter = null;
            NavigateToDialogueTopic(speakerData.Topic, pushToStack: false);
        }
        else if (browserNode.DataObject is QuestTopicPickerData questData)
        {
            // Quest mode: filter InfoChain to this quest
            _dialogueSpeakerFilter = null;
            _dialogueQuestFilter = questData.QuestFormId;
            NavigateToDialogueTopic(questData.Topic, pushToStack: false);
        }
        else if (browserNode.DataObject is TopicDialogueNode topic)
        {
            // Orphan topic: no filters
            _dialogueSpeakerFilter = null;
            _dialogueQuestFilter = null;
            NavigateToDialogueTopic(topic, pushToStack: false);
        }
    }

    /// <summary>
    ///     Checks whether two strings are similar enough that showing both would look duplicated.
    ///     Returns true if one starts with the other (case-insensitive).
    /// </summary>
    private static bool IsSimilarText(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) ||
               b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetTopicTypeIcon(byte topicType)
    {
        return topicType switch
        {
            0 => "\uE8BD", // Topic - chat
            1 => "\uE8BD", // Conversation - chat
            2 => "\uEC05", // Combat - swords
            3 => "\uE8BD", // Persuasion - chat
            4 => "\uE7B3", // Detection - eye
            5 => "\uE8F1", // Service - settings
            6 => "\uE8BD", // Miscellaneous - chat
            7 => "\uE767", // Radio - radio
            _ => "\uE8BD"
        };
    }

    #endregion

    #region Dialogue Conversation Navigation

    private void NavigateToDialogueTopic(TopicDialogueNode topic, bool pushToStack)
    {
        // Push current state onto unified back stack
        if (pushToStack && _currentDialogueTopic != null && !_isNavigating)
        {
            PushUnifiedNav();
        }

        _currentDialogueTopic = topic;
        _visitedTopicFormIds.Add(topic.TopicFormId);

        // Update header
        UpdateDialogueHeader(topic);

        // Build conversation display
        BuildConversationDisplay(topic);

        // Build player choices
        BuildPlayerChoices(topic);

        // Scroll to top
        DialogueConversationScroller.ChangeView(null, 0, null, disableAnimation: true);

        // Sync the left panel tree selection to the current topic
        SyncDialogueTreeSelection(topic);
    }

    private void SyncDialogueTreeSelection(TopicDialogueNode topic)
    {
        foreach (var rootNode in DialoguePickerTree.RootNodes)
        {
            if (rootNode.Content is not EsmBrowserNode categoryNode)
            {
                continue;
            }

            // Check if this category contains the topic (without expanding)
            var containsTopic = categoryNode.DataObject switch
            {
                QuestPickerData qd => qd.Topics.Any(t => t.TopicFormId == topic.TopicFormId),
                SpeakerPickerData sd => sd.Topics.Any(t => t.TopicFormId == topic.TopicFormId),
                List<TopicDialogueNode> list => list.Any(t => t.TopicFormId == topic.TopicFormId),
                _ => false
            };

            if (!containsTopic)
            {
                continue;
            }

            // Expand the category if needed so child nodes are realized
            if (rootNode.HasUnrealizedChildren)
            {
                rootNode.IsExpanded = true;
            }

            // Search children for the matching topic
            foreach (var child in rootNode.Children)
            {
                if (child.Content is not EsmBrowserNode topicNode)
                {
                    continue;
                }

                var childTopicFormId = topicNode.DataObject switch
                {
                    QuestTopicPickerData qt => qt.Topic.TopicFormId,
                    SpeakerTopicPickerData st => st.Topic.TopicFormId,
                    TopicDialogueNode t => t.TopicFormId,
                    _ => (uint?)null
                };

                if (childTopicFormId == topic.TopicFormId)
                {
                    DialoguePickerTree.SelectedNode = child;
                    var container = DialoguePickerTree.ContainerFromNode(child) as UIElement;
                    container?.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
                    return;
                }
            }

            // Topic found in data but not in tree children (shouldn't happen after expansion)
            return;
        }
    }

    private void UpdateDialogueHeader(TopicDialogueNode topic)
    {
        var parts = new List<string>();

        // Determine the quest to display in the header
        if (_session.DialogueTree != null)
        {
            var hasFilter = _dialogueSpeakerFilter != null || _dialogueQuestFilter != null;
            var filteredInfos = hasFilter ? GetFilteredInfoChain(topic) : topic.InfoChain;
            var questFormId = DialogueViewerHelper.ResolveHeaderQuest(
                topic, filteredInfos, _session.DialogueTree, hasFilter);

            if (questFormId is > 0)
            {
                parts.Add(ResolveFormName(questFormId.Value));
            }
        }

        // Topic name
        var topicName = topic.TopicName ?? topic.Topic?.EditorId ?? $"0x{topic.TopicFormId:X8}";
        parts.Add(topicName);

        DialogueHeaderText.Text = string.Join(" \u203A ", parts);
    }

    /// <summary>
    ///     Returns the topic's InfoChain filtered by the current speaker filter (NPC mode),
    ///     or the full chain if no filter is active.
    /// </summary>
    private List<InfoDialogueNode> GetFilteredInfoChain(TopicDialogueNode topic)
    {
        return DialogueViewerHelper.FilterInfoChain(topic.InfoChain, _dialogueQuestFilter, _dialogueSpeakerFilter);
    }

    private void BuildConversationDisplay(TopicDialogueNode topic)
    {
        DialogueConversationPanel.Children.Clear();

        var infoChain = GetFilteredInfoChain(topic);
        if (infoChain.Count == 0)
        {
            var emptyText = new TextBlock
            {
                Text = "No dialogue responses found for this topic.",
                FontSize = 12,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            DialogueConversationPanel.Children.Add(emptyText);
            return;
        }

        // Show alternative indicator if multiple INFOs
        if (infoChain.Count > 1)
        {
            var altNote = new TextBlock
            {
                Text = $"{infoChain.Count} possible responses (game selects based on conditions):",
                FontSize = 11,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Margin = new Thickness(0, 0, 0, 4)
            };
            DialogueConversationPanel.Children.Add(altNote);
        }

        for (var i = 0; i < infoChain.Count; i++)
        {
            var infoNode = infoChain[i];

            // Add separator for alternative responses
            if (i > 0)
            {
                var separator = new Border
                {
                    Height = 1,
                    Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                    Margin = new Thickness(0, 4, 0, 4),
                    Opacity = 0.5
                };
                DialogueConversationPanel.Children.Add(separator);

                var altLabel = new TextBlock
                {
                    Text = $"Alternative response {i + 1}:",
                    FontSize = 11,
                    FontStyle = Windows.UI.Text.FontStyle.Italic,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    Margin = new Thickness(0, 0, 0, 4)
                };
                DialogueConversationPanel.Children.Add(altLabel);
            }

            DialogueConversationPanel.Children.Add(CreateNpcResponseBlock(infoNode));
        }
    }

    private Border CreateNpcResponseBlock(InfoDialogueNode infoNode)
    {
        var info = infoNode.Info;
        var content = new StackPanel { Spacing = 4 };

        // Speaker header
        var speakerName = ResolveSpeakerName(info.SpeakerFormId);
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

        if (info.SpeakerFormId is > 0)
        {
            speakerPanel.Children.Add(new TextBlock
            {
                Text = $"0x{info.SpeakerFormId.Value:X8}",
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        }

        content.Children.Add(speakerPanel);

        // Response texts
        foreach (var response in info.Responses.Where(r => !string.IsNullOrEmpty(r.Text)))
        {
            content.Children.Add(new TextBlock
            {
                Text = $"\u201C{response.Text}\u201D",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Margin = new Thickness(22, 2, 0, 2)
            });
        }

        // Metadata tags
        var tags = new StackPanel
            { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(22, 2, 0, 0) };
        var hasTag = false;

        // Emotion tags
        foreach (var response in info.Responses)
        {
            if (response.EmotionType != 0) // Not Neutral
            {
                var sign = response.EmotionValue >= 0 ? "+" : "";
                tags.Children.Add(CreateMetadataTag($"Emotion: {response.EmotionName} {sign}{response.EmotionValue}"));
                hasTag = true;
            }
        }

        if (info.IsGoodbye)
        {
            tags.Children.Add(CreateMetadataTag("Goodbye"));
            hasTag = true;
        }

        if (info.IsSayOnce)
        {
            tags.Children.Add(CreateMetadataTag("Say Once"));
            hasTag = true;
        }

        if (info.IsSpeechChallenge)
        {
            tags.Children.Add(CreateMetadataTag($"Speech {info.DifficultyName}"));
            hasTag = true;
        }

        if (hasTag)
        {
            content.Children.Add(tags);
        }

        // Collapsible record details
        content.Children.Add(BuildRecordDetailPanel(info));

        return new Border
        {
            Child = content,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8)
        };
    }

    private Border BuildRecordDetailPanel(DialogueRecord info)
    {
        var detailGrid = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(22, 4, 0, 0) };
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var row = 0;

        void AddRow(string name, string? value, uint? linkFormId = null)
        {
            if (string.IsNullOrEmpty(value) && linkFormId is null or 0)
            {
                return;
            }

            detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var nameBlock = new TextBlock
            {
                Text = name,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Padding = new Thickness(0, 1, 12, 1)
            };
            Grid.SetRow(nameBlock, row);
            Grid.SetColumn(nameBlock, 0);
            detailGrid.Children.Add(nameBlock);

            if (linkFormId is > 0)
            {
                var link = CreateFormIdLink(value ?? $"0x{linkFormId.Value:X8}", linkFormId.Value, 11, monospace: true);
                Grid.SetRow(link, row);
                Grid.SetColumn(link, 1);
                detailGrid.Children.Add(link);
            }
            else
            {
                var valueBlock = new TextBlock
                {
                    Text = value ?? "",
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

        // Identity
        AddRow("FormID", $"0x{info.FormId:X8}");
        AddRow("EditorID", info.EditorId);

        // Relationships (with navigable links)
        if (info.TopicFormId is > 0)
        {
            var topicDisplay = ResolveFormName(info.TopicFormId.Value);
            AddRow("Topic", topicDisplay, info.TopicFormId.Value);
        }

        if (info.QuestFormId is > 0)
        {
            var questName = ResolveFormName(info.QuestFormId.Value);
            AddRow("Quest", questName, info.QuestFormId.Value);
        }

        if (info.SpeakerFormId is > 0)
        {
            var speakerDisplay = ResolveSpeakerName(info.SpeakerFormId);
            AddRow("Speaker", $"{speakerDisplay} (0x{info.SpeakerFormId.Value:X8})", info.SpeakerFormId.Value);
        }

        // Flags
        AddRow("Info Index", info.InfoIndex.ToString());
        if (info.InfoFlags != 0)
        {
            var flags = new List<string>();
            if (info.IsGoodbye) flags.Add("Goodbye");
            if ((info.InfoFlags & 0x02) != 0) flags.Add("Random");
            if ((info.InfoFlags & 0x04) != 0) flags.Add("RandomEnd");
            if (info.IsSayOnce) flags.Add("SayOnce");
            if (info.IsSpeechChallenge) flags.Add("SpeechChallenge");
            AddRow("Flags", $"0x{info.InfoFlags:X2} ({string.Join(", ", flags)})");
        }

        if (info.InfoFlagsExt != 0)
        {
            AddRow("Extended Flags", $"0x{info.InfoFlagsExt:X2}");
        }

        if (info.Difficulty > 0)
        {
            AddRow("Difficulty", info.DifficultyName);
        }

        // Linking
        if (info.PreviousInfo is > 0)
        {
            AddRow("Previous INFO", $"0x{info.PreviousInfo.Value:X8}", info.PreviousInfo.Value);
        }

        if (info.LinkToTopics.Count > 0)
        {
            AddRow("Link To Topics", string.Join(", ", info.LinkToTopics.Select(id => $"0x{id:X8}")));
        }

        if (info.LinkFromTopics.Count > 0)
        {
            AddRow("Link From Topics", string.Join(", ", info.LinkFromTopics.Select(id => $"0x{id:X8}")));
        }

        if (info.AddTopics.Count > 0)
        {
            AddRow("Add Topics", string.Join(", ", info.AddTopics.Select(id => $"0x{id:X8}")));
        }

        // Response detail
        for (var i = 0; i < info.Responses.Count; i++)
        {
            var r = info.Responses[i];
            var prefix = info.Responses.Count > 1 ? $"Response {i + 1}" : "Response";
            AddRow($"{prefix} Emotion", $"{r.EmotionName} ({r.EmotionValue:+#;-#;0})");
            AddRow($"{prefix} Number", r.ResponseNumber.ToString());
        }

        // Location
        AddRow("Offset", $"0x{info.Offset:X8}");
        AddRow("Endianness", info.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)");

        // Toggle header
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
            Margin = new Thickness(22, 4, 0, 0)
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
            Margin = new Thickness(0, 2, 0, 0)
        };
    }

    private static Border CreateMetadataTag(string text)
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

    private void BuildPlayerChoices(TopicDialogueNode topic)
    {
        DialogueChoicesPanel.Children.Clear();

        // Collect TCLT-based player choices from filtered INFOs, excluding self-references
        var filteredInfoChain = GetFilteredInfoChain(topic);
        var choiceTopics = DialogueViewerHelper.CollectLinkedTopics(filteredInfoChain, topic.TopicFormId);

        if (choiceTopics.Count > 0)
        {
            // Normal case: show TCLT-linked player choices
            DialogueChoicesHeader.Visibility = Visibility.Visible;
            foreach (var (_, (linked, sourceInfo)) in choiceTopics)
            {
                DialogueChoicesPanel.Children.Add(CreatePlayerChoiceWithDetails(linked, sourceInfo));
            }
        }
        else
        {
            // No TCLT choices — determine the end-of-dialogue reason
            var allGoodbye = filteredInfoChain.Count > 0 && filteredInfoChain.All(i => i.Info.IsGoodbye);
            var hasResultScript = filteredInfoChain.Any(i => i.Info.HasResultScript);

            if (allGoodbye)
            {
                AddEndOfDialogueLabel("End of conversation (Goodbye)");
                AddReturnToPickerLink();
            }
            else if (hasResultScript)
            {
                AddEndOfDialogueLabel("End of dialogue (scripted)");
                AddReturnToPickerLink();
            }
            else
            {
                // No TCLT, no scripts, not goodbye: in-game the player returns to the NPC's topic menu.
                // Show parent topic's TCLT choices if found, otherwise return link.
                var parentTopic = FindParentTopic(topic);
                if (parentTopic != null)
                {
                    var parentChoices = DialogueViewerHelper.CollectLinkedTopics(
                        parentTopic.InfoChain, topic.TopicFormId);
                    if (parentChoices.Count > 0)
                    {
                        DialogueChoicesHeader.Visibility = Visibility.Visible;
                        foreach (var (_, (linked, sourceInfo)) in parentChoices)
                        {
                            DialogueChoicesPanel.Children.Add(
                                CreatePlayerChoiceWithDetails(linked, sourceInfo));
                        }
                    }
                    else
                    {
                        AddEndOfDialogueLabel("Conversation returns to topic list");
                        AddReturnToPickerLink();
                    }
                }
                else
                {
                    AddEndOfDialogueLabel("Conversation returns to topic list");
                    AddReturnToPickerLink();
                }
            }
        }

        // Show AddedTopics (NAME) as informational section below choices
        ShowAddedTopicsInfo(filteredInfoChain);
    }

    private void AddEndOfDialogueLabel(string text)
    {
        DialogueChoicesPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Margin = new Thickness(0, 4, 0, 0)
        });
    }

    private void ShowAddedTopicsInfo(List<InfoDialogueNode> filteredInfoChain)
    {
        var addedTopics = new Dictionary<uint, TopicDialogueNode>();
        foreach (var infoNode in filteredInfoChain)
        {
            foreach (var added in infoNode.AddedTopics)
            {
                addedTopics.TryAdd(added.TopicFormId, added);
            }
        }

        if (addedTopics.Count == 0)
        {
            return;
        }

        var header = new TextBlock
        {
            Text = "Topics unlocked for future conversations:",
            FontSize = 11,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Margin = new Thickness(0, 8, 0, 2)
        };
        DialogueChoicesPanel.Children.Add(header);

        foreach (var (_, addedTopic) in addedTopics)
        {
            var topicName = addedTopic.Topic?.FullName
                            ?? addedTopic.TopicName
                            ?? addedTopic.Topic?.EditorId
                            ?? $"0x{addedTopic.TopicFormId:X8}";

            var link = new HyperlinkButton
            {
                Content = new TextBlock
                {
                    Text = $"  \u2022 {topicName}",
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                },
                Padding = new Thickness(0, 1, 0, 1)
            };

            var capturedTopic = addedTopic;
            link.Click += (_, _) => NavigateToDialogueTopic(capturedTopic, pushToStack: true);

            DialogueChoicesPanel.Children.Add(link);
        }
    }

    private Button CreatePlayerChoiceButton(TopicDialogueNode linkedTopic, InfoDialogueNode sourceInfo)
    {
        var promptText = DialogueViewerHelper.ResolvePromptText(sourceInfo, linkedTopic);
        var isVisited = _visitedTopicFormIds.Contains(linkedTopic.TopicFormId);

        // Check if any INFO in the linked topic is a speech challenge
        var speechInfo = linkedTopic.InfoChain.FirstOrDefault(i => i.Info.IsSpeechChallenge);
        var isGoodbyeTopic = linkedTopic.InfoChain.Count > 0 && linkedTopic.InfoChain.All(i => i.Info.IsGoodbye);

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

        // Speech challenge tag
        if (speechInfo != null)
        {
            contentPanel.Children.Add(new Border
            {
                Child = new TextBlock
                {
                    Text = $"Speech {speechInfo.Info.DifficultyName}",
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

        // Prompt text
        var promptBlock = new TextBlock
        {
            Text = promptText,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (isVisited)
        {
            promptBlock.Opacity = 0.7;
        }

        contentPanel.Children.Add(promptBlock);

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

        var button = new Button
        {
            Content = contentPanel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0)
        };

        // Capture the topic for the lambda
        var target = linkedTopic;
        button.Click += (_, _) => NavigateToDialogueTopic(target, pushToStack: true);

        return button;
    }

    private StackPanel CreatePlayerChoiceWithDetails(TopicDialogueNode linkedTopic, InfoDialogueNode sourceInfo)
    {
        var container = new StackPanel();
        container.Children.Add(CreatePlayerChoiceButton(linkedTopic, sourceInfo));
        container.Children.Add(BuildTopicDetailPanel(linkedTopic, sourceInfo));
        return container;
    }

    private Border BuildTopicDetailPanel(TopicDialogueNode linkedTopic, InfoDialogueNode sourceInfo)
    {
        var detailGrid = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(30, 0, 0, 0) };
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var row = 0;

        void AddRow(string name, string? value, uint? linkFormId = null)
        {
            if (string.IsNullOrEmpty(value) && linkFormId is null or 0)
            {
                return;
            }

            detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var nameBlock = new TextBlock
            {
                Text = name,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Padding = new Thickness(0, 1, 12, 1)
            };
            Grid.SetRow(nameBlock, row);
            Grid.SetColumn(nameBlock, 0);
            detailGrid.Children.Add(nameBlock);

            if (linkFormId is > 0)
            {
                var link = CreateFormIdLink(value ?? $"0x{linkFormId.Value:X8}", linkFormId.Value, 11, monospace: true);
                Grid.SetRow(link, row);
                Grid.SetColumn(link, 1);
                detailGrid.Children.Add(link);
            }
            else
            {
                var valueBlock = new TextBlock
                {
                    Text = value ?? "",
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

        // Topic metadata
        AddRow("Topic FormID", $"0x{linkedTopic.TopicFormId:X8}");
        AddRow("Topic EditorID", linkedTopic.Topic?.EditorId);
        AddRow("Topic Name", linkedTopic.Topic?.FullName);
        AddRow("Topic Type", linkedTopic.Topic?.TopicTypeName);
        AddRow("INFO Count", linkedTopic.InfoChain.Count.ToString());

        // Quest link
        if (linkedTopic.Topic?.QuestFormId is > 0)
        {
            var questName = ResolveFormName(linkedTopic.Topic.QuestFormId.Value);
            AddRow("Quest", questName, linkedTopic.Topic.QuestFormId.Value);
        }

        // Source INFO that leads here
        AddRow("Source INFO", $"0x{sourceInfo.Info.FormId:X8}");
        if (!string.IsNullOrEmpty(sourceInfo.Info.EditorId))
        {
            AddRow("Source EditorID", sourceInfo.Info.EditorId);
        }

        // Toggle header
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
            Margin = new Thickness(30, 0, 0, 0)
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
            Margin = new Thickness(0, 0, 0, 2)
        };
    }

    private string ResolveSpeakerName(uint? formId)
    {
        if (formId is null or 0)
        {
            return "Unknown Speaker";
        }

        return _session.Resolver?.GetBestNameWithRefChain(formId.Value) ?? $"0x{formId.Value:X8}";
    }

    /// <summary>
    ///     Finds the topic that links TO the given topic (the "parent" in the dialogue menu hierarchy).
    ///     When a response ends without linked topics or goodbye, the game returns the player to the
    ///     parent topic's choices.
    /// </summary>
    private TopicDialogueNode? FindParentTopic(TopicDialogueNode currentTopic)
    {
        if (_session.DialogueTree == null)
        {
            return null;
        }

        // When quest filter is active, search only within that quest first
        if (_dialogueQuestFilter.HasValue &&
            _session.DialogueTree.QuestTrees.TryGetValue(_dialogueQuestFilter.Value, out var filteredQuest))
        {
            var result = DialogueViewerHelper.FindParentTopicIn(currentTopic, filteredQuest.Topics);
            if (result != null)
            {
                return result;
            }
        }
        else
        {
            // Search all quest topics
            foreach (var quest in _session.DialogueTree.QuestTrees.Values)
            {
                var result = DialogueViewerHelper.FindParentTopicIn(currentTopic, quest.Topics);
                if (result != null)
                {
                    return result;
                }
            }
        }

        // Also check orphan topics
        return DialogueViewerHelper.FindParentTopicIn(currentTopic, _session.DialogueTree.OrphanTopics);
    }

    /// <summary>
    ///     Adds a "Return to topic list" hyperlink that clears the view back to the picker.
    ///     Used as a fallback when no parent topic can be found, or after Goodbye.
    /// </summary>
    private void AddReturnToPickerLink()
    {
        var returnButton = new HyperlinkButton
        {
            Content = new TextBlock { Text = "\u2190 Return to topic list", FontSize = 12 },
            Padding = new Thickness(0, 4, 0, 0)
        };
        returnButton.Click += (_, _) =>
        {
            _currentDialogueTopic = null;
            DialogueConversationPanel.Children.Clear();
            DialogueChoicesPanel.Children.Clear();
            DialogueChoicesHeader.Visibility = Visibility.Collapsed;
            DialogueHeaderText.Text = Strings.Empty_SelectDialogueTopic;
        };
        DialogueChoicesPanel.Children.Add(returnButton);
    }

    #endregion

    #region Dialogue Viewer Events

    private void DialogueBrowseMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_session.DialogueTree == null)
        {
            return;
        }

        var byQuest = DialogueBrowseMode.SelectedIndex == 0;
        var searchQuery = string.IsNullOrWhiteSpace(DialogueSearchBox.Text) ? null : DialogueSearchBox.Text.Trim();
        BuildDialoguePickerTree(_session.DialogueTree, byQuest, searchQuery);
    }

    private async void DialogueSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Debounce 250ms
        if (_dialogueSearchDebounceToken != null)
        {
            await _dialogueSearchDebounceToken.CancelAsync();
            _dialogueSearchDebounceToken.Dispose();
        }
        _dialogueSearchDebounceToken = new CancellationTokenSource();
        var token = _dialogueSearchDebounceToken.Token;

        try
        {
            await Task.Delay(250, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (_session.DialogueTree == null)
        {
            return;
        }

        var searchQuery = string.IsNullOrWhiteSpace(DialogueSearchBox.Text) ? null : DialogueSearchBox.Text.Trim();
        var byQuest = DialogueBrowseMode.SelectedIndex == 0;
        BuildDialoguePickerTree(_session.DialogueTree, byQuest, searchQuery);
    }

    #endregion
}

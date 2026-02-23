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

        DialogueViewerProgressBar.Visibility = Visibility.Visible;
        DialogueViewerStatusText.Text = _session.IsEsmFile
            ? Strings.Status_LoadingDialogueData
            : Strings.Status_ReconstructingDialogueData;

        try
        {
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

            var npcsWithFullName = new HashSet<uint>(
                result.Npcs.Where(n => n.FullName != null || result.FormIdToDisplayName.ContainsKey(n.FormId))
                    .Select(n => n.FormId));
            npcsWithFullName.UnionWith(
                result.Creatures.Where(c => c.FullName != null || result.FormIdToDisplayName.ContainsKey(c.FormId))
                    .Select(c => c.FormId));
            _session.TopicsBySpeaker =
                DialogueMetadataBuilder.BuildTopicsBySpeaker(_session.DialogueTree, npcsWithFullName);

            _session.DialogueFormIdIndex =
                DialogueMetadataBuilder.BuildDialogueFormIdIndex(_session.DialogueTree);

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
        return _session.EffectiveResolver?.GetBestNameWithRefChain(formId) ?? $"0x{formId:X8}";
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
            var matchingTopics = DialogueMetadataBuilder.FilterTopics(quest.Topics, searchQuery);
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

        var orphanTopics = DialogueMetadataBuilder.FilterTopics(tree.OrphanTopics, searchQuery);
        if (orphanTopics.Count > 0)
        {
            var orphanNode = new EsmBrowserNode
            {
                DisplayName = "Unassigned Topics",
                Detail = $"({orphanTopics.Count})",
                NodeType = "Category",
                IconGlyph = "\uE8BD",
                HasUnrealizedChildren = true,
                DataObject = orphanTopics
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
                Topics: DialogueMetadataBuilder.FilterTopics(kv.Value, searchQuery)))
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

#pragma warning disable CA1822 // XAML event handlers cannot be static
    private void DialoguePickerTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
#pragma warning restore CA1822
    {
        if (!args.Node.HasUnrealizedChildren || args.Node.Content is not EsmBrowserNode node)
        {
            return;
        }

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

            var detail = topicType != null ? $"{topicType} ({infoCount})" : $"({infoCount})";

            // NPC view: show first response text; Quest view: show topic name
            string displayName;
            if (!_dialoguePickerByQuest)
            {
                var firstText = topic.InfoChain
                    .SelectMany(info => info.Info.Responses)
                    .Select(r => r.Text)
                    .FirstOrDefault(t => !string.IsNullOrEmpty(t));

                // Subtitle fallback for picker display
                if (firstText == null)
                {
                    var subtitles = _session.EffectiveSubtitles;
                    if (subtitles != null)
                    {
                        firstText = topic.InfoChain
                            .Select(info => subtitles.Lookup(info.Info.FormId)?.Text)
                            .FirstOrDefault(t => !string.IsNullOrEmpty(t));
                    }
                }

                if (firstText != null && !DialogueMetadataBuilder.IsSimilarText(firstText, topicName))
                {
                    displayName = firstText.Length > 80 ? firstText[..77] + "..." : firstText;
                }
                else
                {
                    displayName = topicName;
                }
            }
            else
            {
                displayName = topicName;
            }

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
                IconGlyph = DialogueMetadataBuilder.GetTopicTypeIcon(topic.Topic?.TopicType ?? 0),
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
            _dialogueSpeakerFilter = speakerData.SpeakerFormId;
            _dialogueQuestFilter = null;
            NavigateToDialogueTopic(speakerData.Topic, pushToStack: false);
        }
        else if (browserNode.DataObject is QuestTopicPickerData questData)
        {
            _dialogueSpeakerFilter = null;
            _dialogueQuestFilter = questData.QuestFormId;
            NavigateToDialogueTopic(questData.Topic, pushToStack: false);
        }
        else if (browserNode.DataObject is TopicDialogueNode topic)
        {
            _dialogueSpeakerFilter = null;
            _dialogueQuestFilter = null;
            NavigateToDialogueTopic(topic, pushToStack: false);
        }
    }

    #endregion

    #region Dialogue Conversation Navigation

    private void NavigateToDialogueTopic(TopicDialogueNode topic, bool pushToStack, string? promptText = null)
    {
        if (pushToStack && _currentDialogueTopic != null && !_isNavigating)
        {
            PushUnifiedNav();
        }

        _currentDialogueTopic = topic;
        _visitedTopicFormIds.Add(topic.TopicFormId);

        var filteredInfoChain = GetFilteredInfoChain(topic);

        UpdateDialogueHeader(topic, filteredInfoChain);
        BuildConversationDisplay(filteredInfoChain, promptText);
        BuildPlayerChoices(topic, filteredInfoChain);

        DialogueConversationScroller.ChangeView(null, 0, null, disableAnimation: true);
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

            if (rootNode.HasUnrealizedChildren)
            {
                rootNode.IsExpanded = true;
            }

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

            return;
        }
    }

    private void UpdateDialogueHeader(TopicDialogueNode topic, List<InfoDialogueNode> filteredInfoChain)
    {
        var parts = new List<string>();

        if (_session.DialogueTree != null)
        {
            var hasFilter = _dialogueSpeakerFilter != null || _dialogueQuestFilter != null;
            var questFormId = DialogueViewerHelper.ResolveHeaderQuest(
                topic, filteredInfoChain, _session.DialogueTree, hasFilter);

            if (questFormId is > 0)
            {
                parts.Add(ResolveFormName(questFormId.Value));
            }
        }

        var topicName = topic.TopicName ?? topic.Topic?.EditorId ?? $"0x{topic.TopicFormId:X8}";
        parts.Add(topicName);

        DialogueHeaderText.Text = string.Join(" \u203A ", parts);
    }

    private List<InfoDialogueNode> GetFilteredInfoChain(TopicDialogueNode topic)
    {
        return DialogueViewerHelper.FilterInfoChain(topic.InfoChain, _dialogueQuestFilter, _dialogueSpeakerFilter);
    }

    private void BuildConversationDisplay(
        List<InfoDialogueNode> filteredInfoChain, string? promptText = null)
    {
        DialogueConversationPanel.Children.Clear();

        if (filteredInfoChain.Count == 0)
        {
            DialogueConversationPanel.Children.Add(DialogueTreeRenderer.CreateEmptyTopicPlaceholder());
            return;
        }

        if (!string.IsNullOrEmpty(promptText))
        {
            DialogueConversationPanel.Children.Add(DialogueTreeRenderer.CreatePlayerPromptBlock(promptText));
        }

        var challengeInfo = filteredInfoChain.FirstOrDefault(i => i.Info.IsSpeechChallenge);
        if (challengeInfo != null)
        {
            DialogueConversationPanel.Children.Add(
                DialogueTreeRenderer.CreateSpeechChallengeBanner(challengeInfo.Info.DifficultyName));
        }

        if (filteredInfoChain.Count > 1)
        {
            DialogueConversationPanel.Children.Add(DialogueTreeRenderer.CreateSecondaryLabel(
                $"{filteredInfoChain.Count} possible responses (game selects based on conditions):",
                new Thickness(0, 0, 0, 4)));
        }

        for (var i = 0; i < filteredInfoChain.Count; i++)
        {
            if (i > 0)
            {
                DialogueConversationPanel.Children.Add(DialogueTreeRenderer.CreateResponseSeparator());
                DialogueConversationPanel.Children.Add(DialogueTreeRenderer.CreateSecondaryLabel(
                    $"Alternative response {i + 1}:", new Thickness(0, 0, 0, 4)));
            }

            DialogueConversationPanel.Children.Add(CreateNpcResponseBlock(filteredInfoChain[i]));
        }
    }

    private Border CreateNpcResponseBlock(InfoDialogueNode infoNode)
    {
        var info = infoNode.Info;
        var content = new StackPanel { Spacing = 4 };

        content.Children.Add(
            DialogueTreeRenderer.BuildSpeakerHeader(ResolveSpeakerName(info.SpeakerFormId), info.SpeakerFormId));

        var hasResponseText = false;
        foreach (var response in info.Responses.Where(r => !string.IsNullOrEmpty(r.Text)))
        {
            hasResponseText = true;
            content.Children.Add(DialogueTreeRenderer.CreateResponseText(response.Text!));
        }

        if (!hasResponseText)
        {
            var subtitle = _session.EffectiveSubtitles?.Lookup(info.FormId);
            if (subtitle?.Text != null)
            {
                content.Children.Add(DialogueTreeRenderer.CreateResponseText(subtitle.Text, isSubtitleFallback: true));
                content.Children.Add(DialogueTreeRenderer.CreateSubtitleSourceLabel());
            }
        }

        var tagStrip = DialogueTreeRenderer.BuildMetadataTagStrip(
            DialogueRecordDetailBuilder.CollectMetadataTags(info));
        if (tagStrip != null)
        {
            content.Children.Add(tagStrip);
        }

        content.Children.Add(BuildRecordDetailPanel(info));
        return DialogueTreeRenderer.WrapInResponseCard(content);
    }

    private Border BuildRecordDetailPanel(DialogueRecord info)
    {
        var csvSubtitle = _session.EffectiveSubtitles?.Lookup(info.FormId);
        var rows = DialogueRecordDetailBuilder.BuildRecordDetailRows(
            info, csvSubtitle, ResolveFormName, ResolveSpeakerName);

        return DialogueTreeRenderer.BuildDetailPanel(
            rows,
            gridMargin: new Thickness(22, 4, 0, 0),
            toggleMargin: new Thickness(22, 4, 0, 0),
            borderMargin: new Thickness(0, 2, 0, 0),
            createLink: (text, formId, fontSize, monospace) => CreateFormIdLink(text, formId, fontSize, monospace));
    }

    private void BuildPlayerChoices(TopicDialogueNode topic, List<InfoDialogueNode> filteredInfoChain)
    {
        DialogueChoicesPanel.Children.Clear();

        var choiceTopics = DialogueViewerHelper.CollectLinkedTopics(filteredInfoChain, topic.TopicFormId);

        if (choiceTopics.Count > 0)
        {
            DialogueChoicesHeader.Visibility = Visibility.Visible;
            foreach (var (_, (linked, sourceInfo)) in choiceTopics)
            {
                DialogueChoicesPanel.Children.Add(CreatePlayerChoiceWithDetails(linked, sourceInfo));
            }
        }
        else
        {
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
                var parentTopic = FindParentTopic(topic);
                var parentChoices = parentTopic != null
                    ? DialogueViewerHelper.CollectLinkedTopics(parentTopic.InfoChain, topic.TopicFormId)
                    : [];

                if (parentChoices.Count > 0)
                {
                    DialogueChoicesHeader.Visibility = Visibility.Visible;
                    foreach (var (_, (linked, sourceInfo)) in parentChoices)
                    {
                        DialogueChoicesPanel.Children.Add(CreatePlayerChoiceWithDetails(linked, sourceInfo));
                    }
                }
                else
                {
                    AddEndOfDialogueLabel("Conversation returns to topic list");
                    AddReturnToPickerLink();
                }
            }
        }

        ShowAddedTopicsInfo(filteredInfoChain);
    }

    private void AddEndOfDialogueLabel(string text)
    {
        DialogueChoicesPanel.Children.Add(DialogueTreeRenderer.CreateEndOfDialogueLabel(text));
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

        DialogueChoicesPanel.Children.Add(DialogueTreeRenderer.CreateSecondaryLabel(
            "Topics unlocked for future conversations:", new Thickness(0, 8, 0, 2)));

        foreach (var (_, addedTopic) in addedTopics)
        {
            var displayText = DialogueViewerHelper.ResolveTopicDisplayText(addedTopic);
            var button = CreateDialogueChoiceButton(displayText, addedTopic);
            var container = new StackPanel();
            container.Children.Add(button);
            DialogueChoicesPanel.Children.Add(container);
        }
    }

    private StackPanel CreatePlayerChoiceWithDetails(TopicDialogueNode linkedTopic, InfoDialogueNode sourceInfo)
    {
        var container = new StackPanel();
        container.Children.Add(CreatePlayerChoiceButton(linkedTopic, sourceInfo));
        container.Children.Add(BuildTopicDetailPanel(linkedTopic, sourceInfo));
        return container;
    }

    private Button CreatePlayerChoiceButton(TopicDialogueNode linkedTopic, InfoDialogueNode sourceInfo)
    {
        var promptText = DialogueViewerHelper.ResolvePromptText(sourceInfo, linkedTopic);
        var challengeOutcome = DialogueMetadataBuilder.DetectChallengeOutcome(ref promptText);
        var speechInfo = linkedTopic.InfoChain.FirstOrDefault(i => i.Info.IsSpeechChallenge);
        var isGoodbyeTopic = linkedTopic.InfoChain.Count > 0 && linkedTopic.InfoChain.All(i => i.Info.IsGoodbye);

        return CreateDialogueChoiceButton(
            promptText, linkedTopic,
            challengeOutcome: challengeOutcome,
            speechChallengeDifficulty: speechInfo?.Info.DifficultyName,
            isGoodbyeTopic: isGoodbyeTopic);
    }

    private Button CreateDialogueChoiceButton(
        string displayText, TopicDialogueNode targetTopic,
        string? challengeOutcome = null, string? speechChallengeDifficulty = null,
        bool isGoodbyeTopic = false)
    {
        var isVisited = _visitedTopicFormIds.Contains(targetTopic.TopicFormId);
        var contentPanel = DialogueTreeRenderer.BuildChoiceContent(
            displayText, isVisited, challengeOutcome, speechChallengeDifficulty, isGoodbyeTopic);

        var button = new Button
        {
            Content = contentPanel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0)
        };

        button.Click += (_, _) => NavigateToDialogueTopic(targetTopic, pushToStack: true, promptText: displayText);

        return button;
    }

    private Border BuildTopicDetailPanel(TopicDialogueNode linkedTopic, InfoDialogueNode sourceInfo)
    {
        var rows = DialogueRecordDetailBuilder.BuildTopicDetailRows(
            linkedTopic, sourceInfo, ResolveFormName);

        return DialogueTreeRenderer.BuildDetailPanel(
            rows,
            gridMargin: new Thickness(30, 0, 0, 0),
            toggleMargin: new Thickness(30, 0, 0, 0),
            borderMargin: new Thickness(0, 0, 0, 2),
            createLink: (text, formId, fontSize, monospace) => CreateFormIdLink(text, formId, fontSize, monospace));
    }

    private string ResolveSpeakerName(uint? formId)
    {
        if (formId is null or 0)
        {
            return "Unknown Speaker";
        }

        return _session.EffectiveResolver?.GetBestNameWithRefChain(formId.Value) ?? $"0x{formId.Value:X8}";
    }

    private TopicDialogueNode? FindParentTopic(TopicDialogueNode currentTopic)
    {
        if (_session.DialogueTree == null)
        {
            return null;
        }

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
            foreach (var quest in _session.DialogueTree.QuestTrees.Values)
            {
                var result = DialogueViewerHelper.FindParentTopicIn(currentTopic, quest.Topics);
                if (result != null)
                {
                    return result;
                }
            }
        }

        return DialogueViewerHelper.FindParentTopicIn(currentTopic, _session.DialogueTree.OrphanTopics);
    }

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

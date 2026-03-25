using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        _dialogueShowEditorIds = false;
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

        SubTabView.SelectedItem = DialogueViewerTab;

        if (!_session.DialogueViewerPopulated && _session.SemanticResult != null)
        {
            _ = PopulateDialogueViewerAsync();
        }

        _dialogueSpeakerFilter = null;
        _dialogueQuestFilter = null;
        var displayText = DialogueViewerHelper.ResolveTopicDisplayText(topic);
        var firstInfo = topic.InfoChain.FirstOrDefault()?.Info;
        NavigateToDialogueTopic(topic, pushToStack: _currentDialogueTopic != null,
            promptText: displayText, promptSourceInfo: firstInfo);
        return true;
    }

    #endregion

    #region Dialogue Viewer Fields

    private readonly HashSet<uint> _visitedTopicFormIds = new();

    private bool _dialoguePickerByQuest = true;
    private bool _dialogueShowEditorIds;
    private TopicDialogueNode? _currentDialogueTopic;
    private InfoDialogueNode? _selectedResponseNode;
    private uint? _dialogueSpeakerFilter;
    private uint? _dialogueQuestFilter;
    private CancellationTokenSource? _dialogueSearchDebounceToken;

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
            : Strings.Status_ParsingDialogueData;

        try
        {
            if (_session.SemanticResult == null)
            {
                DialogueViewerProgressBar.IsIndeterminate = false;
                await EnsureSemanticParseAsync();
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

    private string ResolveEditorId(uint formId)
    {
        return _session.EffectiveResolver?.ResolveEditorId(formId) ?? $"0x{formId:X8}";
    }

    private string? ResolveQuestVariable(uint questFormId, uint varIndex)
    {
        var quests = _session.SemanticResult?.Quests;
        if (quests == null)
        {
            return null;
        }

        var quest = quests.FirstOrDefault(q => q.FormId == questFormId);
        return quest?.Variables.FirstOrDefault(v => v.Index == varIndex)?.Name;
    }

    #endregion

    #region Dialogue Picker Tree

    private void BuildDialoguePickerTree(DialogueTreeResult tree, bool byQuest, string? searchQuery = null)
    {
        _dialoguePickerByQuest = byQuest;
        DialoguePickerTree.RootNodes.Clear();

        List<TreeViewNode> nodes;
        if (byQuest)
        {
            nodes = DialoguePickerTreeBuilder.BuildQuestPickerNodes(
                tree, searchQuery, _dialogueShowEditorIds ? ResolveEditorId : ResolveFormName);
        }
        else if (_session.TopicsBySpeaker != null)
        {
            nodes = DialoguePickerTreeBuilder.BuildNpcPickerNodes(
                _session.TopicsBySpeaker,
                _dialogueShowEditorIds ? ResolveEditorId : ResolveFormName,
                searchQuery);
        }
        else
        {
            return;
        }

        foreach (var node in nodes)
        {
            DialoguePickerTree.RootNodes.Add(node);
        }
    }

#pragma warning disable CA1822 // XAML event handlers cannot be static
    private void DialoguePickerTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
#pragma warning restore CA1822
    {
        DialoguePickerTreeBuilder.ExpandCategoryNode(
            args, _dialoguePickerByQuest,
            ResolveFormName, _dialogueShowEditorIds);
    }

    private void DialoguePickerTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode { Content: EsmBrowserNode browserNode })
        {
            return;
        }

        var (topic, speakerFilter, questFilter) = DialoguePickerTreeBuilder.ExtractTopicFromInvocation(browserNode);
        if (topic == null)
        {
            // Auto-select GREETING topic when an NPC speaker node is clicked
            if (browserNode.DataObject is DialoguePickerTreeBuilder.SpeakerPickerData speakerPicker
                && _session.DialogueTree != null)
            {
                var greeting = DialogueViewerHelper.FindGreetingTopicForSpeaker(
                    _session.DialogueTree, speakerPicker.SpeakerFormId);
                if (greeting != null)
                {
                    _dialogueSpeakerFilter = speakerPicker.SpeakerFormId;
                    _dialogueQuestFilter = null;
                    var filteredChain = DialogueViewerHelper.FilterInfoChain(
                        greeting.InfoChain, null, speakerPicker.SpeakerFormId);
                    var greetingInfo = filteredChain.FirstOrDefault()?.Info;
                    var greetingText = greetingInfo?.PromptText
                                       ?? DialogueViewerHelper.ResolveTopicDisplayText(greeting);
                    NavigateToDialogueTopic(greeting, pushToStack: false,
                        promptText: greetingText, promptSourceInfo: greetingInfo);
                }
            }

            return;
        }

        _dialogueSpeakerFilter = speakerFilter;
        _dialogueQuestFilter = questFilter;
        var displayText = DialogueViewerHelper.ResolveTopicDisplayText(topic);
        var firstInfo = topic.InfoChain.FirstOrDefault()?.Info;
        NavigateToDialogueTopic(topic, pushToStack: false,
            promptText: displayText, promptSourceInfo: firstInfo);
    }

    #endregion

    #region Dialogue Conversation Navigation

    private void NavigateToDialogueTopic(TopicDialogueNode topic, bool pushToStack,
        string? promptText = null, DialogueRecord? promptSourceInfo = null)
    {
        if (pushToStack && _currentDialogueTopic != null && !_isNavigating)
        {
            PushUnifiedNav();
        }

        _currentDialogueTopic = topic;
        _selectedResponseNode = null;
        _visitedTopicFormIds.Add(topic.TopicFormId);

        var filteredInfoChain = DialogueViewerHelper.FilterInfoChain(
            topic.InfoChain, _dialogueQuestFilter, _dialogueSpeakerFilter,
            strictQuestFilter: _dialogueQuestFilter != null);

        UpdateDialogueHeader(topic, filteredInfoChain);
        PopulateConversationDisplay(topic, filteredInfoChain, promptText, promptSourceInfo);
        PopulatePlayerChoices(topic, filteredInfoChain);

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

            if (!DialoguePickerTreeBuilder.CategoryContainsTopic(categoryNode, topic.TopicFormId))
            {
                continue;
            }

            if (rootNode.HasUnrealizedChildren)
            {
                rootNode.IsExpanded = true;
            }

            if (TrySelectTopicInChildren(rootNode, topic.TopicFormId))
            {
                return;
            }

            // Search one level deeper (NPC → Category → Topic)
            foreach (var child in rootNode.Children)
            {
                if (child.Content is not EsmBrowserNode childNode ||
                    !DialoguePickerTreeBuilder.CategoryContainsTopic(childNode, topic.TopicFormId))
                {
                    continue;
                }

                if (child.HasUnrealizedChildren)
                {
                    child.IsExpanded = true;
                }

                if (TrySelectTopicInChildren(child, topic.TopicFormId))
                {
                    return;
                }
            }

            return;
        }
    }

    private bool TrySelectTopicInChildren(TreeViewNode parentNode, uint topicFormId)
    {
        foreach (var child in parentNode.Children)
        {
            if (child.Content is not EsmBrowserNode topicNode)
            {
                continue;
            }

            if (DialoguePickerTreeBuilder.GetChildTopicFormId(topicNode) == topicFormId)
            {
                DialoguePickerTree.SelectedNode = child;
                var container = DialoguePickerTree.ContainerFromNode(child) as UIElement;
                container?.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
                return true;
            }
        }

        return false;
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
                parts.Add(_dialogueShowEditorIds
                    ? ResolveEditorId(questFormId.Value)
                    : ResolveFormName(questFormId.Value));
            }
        }

        var topicName = topic.TopicName ?? topic.Topic?.EditorId ?? $"0x{topic.TopicFormId:X8}";
        parts.Add(topicName);

        DialogueHeaderText.Text = string.Join(" \u203A ", parts);
    }

    private void PopulateConversationDisplay(TopicDialogueNode topic,
        List<InfoDialogueNode> filteredInfoChain,
        string? promptText = null, DialogueRecord? promptSourceInfo = null)
    {
        DialogueConversationPanel.Children.Clear();

        // When no INFO record is available for the prompt detail panel but we have a DIAL record,
        // build a topic-level detail panel instead
        Border? topicDetailPanel = null;
        if (promptSourceInfo == null && topic.Topic != null)
        {
            var topicRows = DialogueRecordDetailBuilder.BuildDialTopicDetailRows(
                topic.Topic, ResolveFormName);
            topicDetailPanel = DialogueTreeRenderer.BuildDetailPanel(
                topicRows,
                gridMargin: new Thickness(22, 4, 0, 0),
                toggleMargin: new Thickness(22, 4, 0, 0),
                borderMargin: new Thickness(0, 2, 0, 0),
                createLink: (text, formId, fontSize, monospace) =>
                    CreateFormIdLink(text, formId, fontSize, monospace));
        }

        // When multiple responses exist, allow clicking to select one for follow-up choices
        Action<InfoDialogueNode>? onResponseSelected = filteredInfoChain.Count > 1
            ? OnResponseSelected
            : null;

        var elements = DialogueConversationBuilder.BuildConversationElements(
            filteredInfoChain, promptText,
            ResolveSpeakerName,
            formId => _session.EffectiveSubtitles?.Lookup(formId),
            BuildRecordDetailPanel,
            promptSourceInfo,
            onResponseSelected,
            _selectedResponseNode,
            topicDetailPanel);

        foreach (var element in elements)
        {
            DialogueConversationPanel.Children.Add(element);
        }
    }

    private void OnResponseSelected(InfoDialogueNode selectedNode)
    {
        // Toggle selection — clicking same response again deselects it
        _selectedResponseNode = _selectedResponseNode == selectedNode ? null : selectedNode;

        if (_currentDialogueTopic == null)
        {
            return;
        }

        var filteredInfoChain = DialogueViewerHelper.FilterInfoChain(
            _currentDialogueTopic.InfoChain, _dialogueQuestFilter, _dialogueSpeakerFilter,
            strictQuestFilter: _dialogueQuestFilter != null);

        // Re-render conversation to update selection styling
        PopulateConversationDisplay(_currentDialogueTopic, filteredInfoChain);
        PopulatePlayerChoices(_currentDialogueTopic, filteredInfoChain);
    }

    private Border BuildRecordDetailPanel(DialogueRecord info)
    {
        var csvSubtitle = _session.EffectiveSubtitles?.Lookup(info.FormId);
        var topicEditorId = info.TopicFormId is > 0
            ? _session.EffectiveResolver?.EditorIds.GetValueOrDefault(info.TopicFormId.Value)
            : null;
        var rows = DialogueRecordDetailBuilder.BuildRecordDetailRows(
            info, csvSubtitle, ResolveFormName, ResolveSpeakerName, topicEditorId,
            ResolveEditorId, ResolveQuestVariable);

        return DialogueTreeRenderer.BuildDetailPanel(
            rows,
            gridMargin: new Thickness(22, 4, 0, 0),
            toggleMargin: new Thickness(22, 4, 0, 0),
            borderMargin: new Thickness(0, 2, 0, 0),
            createLink: (text, formId, fontSize, monospace) => CreateFormIdLink(text, formId, fontSize, monospace));
    }

    private void PopulatePlayerChoices(TopicDialogueNode topic, List<InfoDialogueNode> filteredInfoChain)
    {
        DialogueChoicesPanel.Children.Clear();

        // Topics with 0 INFOs have no dialogue data — don't show misleading parent choices
        if (filteredInfoChain.Count == 0)
        {
            AddEndOfDialogueLabel("No dialogue data available for this topic");
            AddReturnToPickerLink();
            return;
        }

        // Check Goodbye FIRST — if all filtered INFOs end the conversation,
        // don't show TCLT-linked choices (which would pull in unrelated topics).
        var allGoodbye = filteredInfoChain.Count > 0 && filteredInfoChain.All(i => i.Info.IsGoodbye);

        // When a specific response is selected and there are multiple responses,
        // only show that response's follow-up choices
        var choiceChain = filteredInfoChain;
        if (_selectedResponseNode != null && filteredInfoChain.Count > 1 &&
            filteredInfoChain.Contains(_selectedResponseNode))
        {
            choiceChain = [_selectedResponseNode];
        }

        var choiceTopics = allGoodbye
            ? new Dictionary<uint, (TopicDialogueNode Topic, InfoDialogueNode SourceInfo)>()
            : DialogueViewerHelper.CollectLinkedTopics(choiceChain, topic.TopicFormId);

        // When multiple responses exist and none is selected, show a hint instead of merging all choices
        if (filteredInfoChain.Count > 1 && _selectedResponseNode == null && !allGoodbye)
        {
            // Check if any responses have choice topics at all
            var anyHasChoices = DialogueViewerHelper.CollectLinkedTopics(filteredInfoChain, topic.TopicFormId).Count >
                                0;
            if (anyHasChoices)
            {
                DialogueChoicesHeader.Visibility = Visibility.Visible;
                DialogueChoicesPanel.Children.Add(
                    DialogueTreeRenderer.CreateSecondaryLabel(
                        "Select a response above to see its follow-up choices",
                        new Thickness(0, 4, 0, 4)));
                ShowAddedTopicsInfo(filteredInfoChain);
                return;
            }
        }

        if (choiceTopics.Count > 0)
        {
            DialogueChoicesHeader.Visibility = Visibility.Visible;
            foreach (var (_, (linked, sourceInfo)) in choiceTopics
                         .OrderByDescending(kv => kv.Value.Topic.Topic?.Priority ?? 0f))
            {
                DialogueChoicesPanel.Children.Add(CreatePlayerChoiceWithDetails(linked, sourceInfo));
            }
        }
        else
        {
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
                // In the game, when a dialogue branch ends without goodbye/script,
                // it returns to the parent topic's choices (the previous choice list).
                var parentTopic = _session.DialogueTree != null
                    ? DialogueViewerHelper.FindParentTopic(topic, _session.DialogueTree, _dialogueQuestFilter)
                    : null;

                if (parentTopic != null)
                {
                    var parentFilteredChain = DialogueViewerHelper.FilterInfoChain(
                        parentTopic.InfoChain, _dialogueQuestFilter, _dialogueSpeakerFilter);
                    var parentChoices = DialogueViewerHelper.CollectLinkedTopics(
                        parentFilteredChain, topic.TopicFormId);

                    if (parentChoices.Count > 0)
                    {
                        DialogueChoicesHeader.Visibility = Visibility.Visible;
                        foreach (var (_, (linked, sourceInfo)) in parentChoices
                                     .OrderByDescending(kv => kv.Value.Topic.Topic?.Priority ?? 0f))
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

        ShowAddedTopicsInfo(filteredInfoChain);
    }

    private void AddEndOfDialogueLabel(string text)
    {
        DialogueChoicesPanel.Children.Add(DialogueTreeRenderer.CreateEndOfDialogueLabel(text));
    }

    private void ShowAddedTopicsInfo(List<InfoDialogueNode> filteredInfoChain)
    {
        var addedTopics = DialogueConversationBuilder.CollectUnlockedTopics(filteredInfoChain);
        if (addedTopics.Count == 0)
        {
            return;
        }

        DialogueChoicesPanel.Children.Add(DialogueTreeRenderer.CreateSecondaryLabel(
            "Topics unlocked for future conversations:", new Thickness(0, 8, 0, 2)));

        foreach (var (_, addedTopic) in addedTopics)
        {
            var sourceInfo = filteredInfoChain.FirstOrDefault(i =>
                i.AddedTopics.Any(a => a.TopicFormId == addedTopic.TopicFormId));
            if (sourceInfo != null)
            {
                DialogueChoicesPanel.Children.Add(CreatePlayerChoiceWithDetails(addedTopic, sourceInfo));
            }
            else
            {
                var displayText = _dialogueShowEditorIds
                    ? DialogueViewerHelper.ResolveEditorIdDisplay(addedTopic)
                    : DialogueViewerHelper.ResolveTopicDisplayText(addedTopic);
                var targetFirstInfo = addedTopic.InfoChain.FirstOrDefault()?.Info;
                var button = DialogueConversationBuilder.CreateStyledChoiceButton(
                    displayText, addedTopic, _visitedTopicFormIds,
                    (t, prompt) => NavigateToDialogueTopic(t, pushToStack: true, promptText: prompt,
                        promptSourceInfo: targetFirstInfo));
                var container = new StackPanel();
                container.Children.Add(button);
                DialogueChoicesPanel.Children.Add(container);
            }
        }
    }

    private StackPanel CreatePlayerChoiceWithDetails(TopicDialogueNode linkedTopic, InfoDialogueNode sourceInfo)
    {
        var container = new StackPanel();
        var button = DialogueConversationBuilder.CreatePlayerChoiceButton(
            linkedTopic, sourceInfo, _visitedTopicFormIds,
            (t, prompt) => NavigateToDialogueTopic(t, pushToStack: true, promptText: prompt,
                promptSourceInfo: sourceInfo.Info),
            _dialogueShowEditorIds);
        container.Children.Add(button);
        container.Children.Add(BuildTopicDetailPanel(linkedTopic, sourceInfo));
        return container;
    }

    private Border BuildTopicDetailPanel(TopicDialogueNode linkedTopic, InfoDialogueNode sourceInfo)
    {
        var rows = DialogueRecordDetailBuilder.BuildTopicDetailRows(
            linkedTopic, sourceInfo, ResolveFormName, ResolveEditorId);

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

    private void DialogueShowEditorIdCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _dialogueShowEditorIds = DialogueShowEditorIdCheckBox.IsChecked == true;

        if (_session.DialogueTree == null)
        {
            return;
        }

        // Rebuild the picker tree with new display mode
        var byQuest = DialogueBrowseMode.SelectedIndex == 0;
        var searchQuery = string.IsNullOrWhiteSpace(DialogueSearchBox.Text) ? null : DialogueSearchBox.Text.Trim();
        BuildDialoguePickerTree(_session.DialogueTree, byQuest, searchQuery);

        // Re-render current conversation with new display mode and restore selection
        if (_currentDialogueTopic != null)
        {
            var filteredInfoChain = DialogueViewerHelper.FilterInfoChain(
                _currentDialogueTopic.InfoChain, _dialogueQuestFilter, _dialogueSpeakerFilter,
                strictQuestFilter: _dialogueQuestFilter != null);
            UpdateDialogueHeader(_currentDialogueTopic, filteredInfoChain);
            PopulatePlayerChoices(_currentDialogueTopic, filteredInfoChain);
            SyncDialogueTreeSelection(_currentDialogueTopic);
        }
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

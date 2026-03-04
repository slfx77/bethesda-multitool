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

    #endregion

    #region Dialogue Picker Tree

    private void BuildDialoguePickerTree(DialogueTreeResult tree, bool byQuest, string? searchQuery = null)
    {
        _dialoguePickerByQuest = byQuest;
        DialoguePickerTree.RootNodes.Clear();

        List<TreeViewNode> nodes;
        if (byQuest)
        {
            nodes = DialoguePickerTreeBuilder.BuildQuestPickerNodes(tree, searchQuery);
        }
        else if (_session.TopicsBySpeaker != null)
        {
            nodes = DialoguePickerTreeBuilder.BuildNpcPickerNodes(
                _session.TopicsBySpeaker, ResolveFormName, searchQuery);
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
            formId => _session.EffectiveSubtitles?.Lookup(formId),
            ResolveFormName);
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
            return;
        }

        _dialogueSpeakerFilter = speakerFilter;
        _dialogueQuestFilter = questFilter;
        NavigateToDialogueTopic(topic, pushToStack: false);
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

        var filteredInfoChain = DialogueViewerHelper.FilterInfoChain(
            topic.InfoChain, _dialogueQuestFilter, _dialogueSpeakerFilter);

        UpdateDialogueHeader(topic, filteredInfoChain);
        PopulateConversationDisplay(filteredInfoChain, promptText);
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

            foreach (var child in rootNode.Children)
            {
                if (child.Content is not EsmBrowserNode topicNode)
                {
                    continue;
                }

                if (DialoguePickerTreeBuilder.GetChildTopicFormId(topicNode) == topic.TopicFormId)
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

    private void PopulateConversationDisplay(List<InfoDialogueNode> filteredInfoChain, string? promptText = null)
    {
        DialogueConversationPanel.Children.Clear();

        var elements = DialogueConversationBuilder.BuildConversationElements(
            filteredInfoChain, promptText,
            ResolveSpeakerName,
            formId => _session.EffectiveSubtitles?.Lookup(formId),
            BuildRecordDetailPanel);

        foreach (var element in elements)
        {
            DialogueConversationPanel.Children.Add(element);
        }
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

    private void PopulatePlayerChoices(TopicDialogueNode topic, List<InfoDialogueNode> filteredInfoChain)
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
                var parentTopic = _session.DialogueTree != null
                    ? DialogueViewerHelper.FindParentTopic(topic, _session.DialogueTree, _dialogueQuestFilter)
                    : null;
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
        var addedTopics = DialogueConversationBuilder.CollectUnlockedTopics(filteredInfoChain);
        if (addedTopics.Count == 0)
        {
            return;
        }

        DialogueChoicesPanel.Children.Add(DialogueTreeRenderer.CreateSecondaryLabel(
            "Topics unlocked for future conversations:", new Thickness(0, 8, 0, 2)));

        foreach (var (_, addedTopic) in addedTopics)
        {
            var displayText = DialogueViewerHelper.ResolveTopicDisplayText(addedTopic);
            var button = DialogueConversationBuilder.CreateStyledChoiceButton(
                displayText, addedTopic, _visitedTopicFormIds,
                (t, prompt) => NavigateToDialogueTopic(t, pushToStack: true, promptText: prompt));
            var container = new StackPanel();
            container.Children.Add(button);
            DialogueChoicesPanel.Children.Add(container);
        }
    }

    private StackPanel CreatePlayerChoiceWithDetails(TopicDialogueNode linkedTopic, InfoDialogueNode sourceInfo)
    {
        var container = new StackPanel();
        var button = DialogueConversationBuilder.CreatePlayerChoiceButton(
            linkedTopic, sourceInfo, _visitedTopicFormIds,
            (t, prompt) => NavigateToDialogueTopic(t, pushToStack: true, promptText: prompt));
        container.Children.Add(button);
        container.Children.Add(BuildTopicDetailPanel(linkedTopic, sourceInfo));
        return container;
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

using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Subtitles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FalloutXbox360Utils;

/// <summary>
///     Builds the conversation display UI elements for the dialogue viewer.
///     Creates NPC response blocks and player choice buttons.
/// </summary>
internal static class DialogueConversationBuilder
{
    private const int MaxDisplayedFollowUps = 12;
    private const int MaxFollowUpDisplayDepth = 8;

    /// <summary>
    ///     Builds the full conversation display elements from a filtered info chain.
    /// </summary>
    public static List<UIElement> BuildConversationElements(
        List<InfoDialogueNode> filteredInfoChain,
        string? promptText,
        Func<uint?, string> resolveSpeakerName,
        Func<uint, SubtitleEntry?> subtitleLookup,
        Func<DialogueRecord, Border> buildRecordDetailPanel,
        DialogueRecord? promptSourceInfo = null,
        Action<InfoDialogueNode>? onResponseSelected = null,
        InfoDialogueNode? selectedResponse = null,
        Border? topicDetailPanel = null)
    {
        var elements = new List<UIElement>();

        // Show player prompt before the empty check so topics with no responses
        // still display the prompt text and record details
        if (!string.IsNullOrEmpty(promptText))
        {
            var detailPanel = promptSourceInfo != null
                ? buildRecordDetailPanel(promptSourceInfo)
                : topicDetailPanel;
            elements.Add(DialogueTreeRenderer.CreatePlayerPromptBlock(promptText, detailPanel));
        }

        if (filteredInfoChain.Count == 0)
        {
            elements.Add(DialogueTreeRenderer.CreateEmptyTopicPlaceholder());
            return elements;
        }

        var challengeInfo = filteredInfoChain.FirstOrDefault(i => i.Info.IsSpeechChallenge);
        if (challengeInfo != null)
        {
            elements.Add(
                DialogueTreeRenderer.CreateSpeechChallengeBanner(challengeInfo.Info.DifficultyName));
        }

        // Cap display to prevent crash on shared topics (GREETING, Hello) with thousands of INFOs
        const int maxDisplayedResponses = 25;
        var displayChain = filteredInfoChain;
        var wasTruncated = false;
        if (filteredInfoChain.Count > maxDisplayedResponses)
        {
            displayChain = filteredInfoChain.Take(maxDisplayedResponses).ToList();
            wasTruncated = true;
        }

        if (displayChain.Count > 1)
        {
            elements.Add(DialogueTreeRenderer.CreateSecondaryLabel(
                wasTruncated
                    ? $"{filteredInfoChain.Count} possible responses (showing first {maxDisplayedResponses}):"
                    : $"{filteredInfoChain.Count} possible responses (game selects based on conditions):",
                new Thickness(0, 0, 0, 4)));
        }

        for (var i = 0; i < displayChain.Count; i++)
        {
            if (i > 0)
            {
                elements.Add(DialogueTreeRenderer.CreateResponseSeparator());
                elements.Add(DialogueTreeRenderer.CreateSecondaryLabel(
                    $"Alternative response {i + 1}:", new Thickness(0, 0, 0, 4)));
            }

            var isSelected = selectedResponse == displayChain[i];
            var responseElement = BuildNpcResponseBlock(
                displayChain[i], resolveSpeakerName, subtitleLookup, buildRecordDetailPanel,
                onResponseSelected, isSelected);
            elements.Add(responseElement);

            if (displayChain.Count == 1 || isSelected)
            {
                AddFollowUpResponseElements(
                    elements, displayChain[i], resolveSpeakerName, subtitleLookup,
                    buildRecordDetailPanel);
            }
        }

        if (wasTruncated)
        {
            elements.Add(DialogueTreeRenderer.CreateSecondaryLabel(
                $"Showing {maxDisplayedResponses} of {filteredInfoChain.Count} responses",
                new Thickness(0, 4, 0, 0)));
        }

        return elements;
    }

    private static void AddFollowUpResponseElements(
        List<UIElement> elements,
        InfoDialogueNode sourceNode,
        Func<uint?, string> resolveSpeakerName,
        Func<uint, SubtitleEntry?> subtitleLookup,
        Func<DialogueRecord, Border> buildRecordDetailPanel)
    {
        AddFollowUpResponseElements(
            elements, sourceNode, resolveSpeakerName, subtitleLookup, buildRecordDetailPanel,
            [sourceNode.Info.FormId], 0);
    }

    private static void AddFollowUpResponseElements(
        List<UIElement> elements,
        InfoDialogueNode sourceNode,
        Func<uint?, string> resolveSpeakerName,
        Func<uint, SubtitleEntry?> subtitleLookup,
        Func<DialogueRecord, Border> buildRecordDetailPanel,
        HashSet<uint> visitedInfoFormIds,
        int depth)
    {
        if (depth >= MaxFollowUpDisplayDepth || sourceNode.FollowUpInfos.Count == 0)
        {
            return;
        }

        var followUps = sourceNode.FollowUpInfos
            .Where(f => !visitedInfoFormIds.Contains(f.Info.FormId))
            .Take(MaxDisplayedFollowUps + 1)
            .ToList();

        if (followUps.Count == 0)
        {
            return;
        }

        var wasTruncated = followUps.Count > MaxDisplayedFollowUps;
        var displayFollowUps = wasTruncated
            ? followUps.Take(MaxDisplayedFollowUps).ToList()
            : followUps;

        elements.Add(DialogueTreeRenderer.CreateSecondaryLabel(
            displayFollowUps.Count == 1
                ? "Possible follow-up response:"
                : "Possible follow-up responses:",
            new Thickness(22, 6, 0, 2)));

        foreach (var followUp in displayFollowUps)
        {
            visitedInfoFormIds.Add(followUp.Info.FormId);
            elements.Add(BuildNpcResponseBlock(
                followUp, resolveSpeakerName, subtitleLookup, buildRecordDetailPanel));
            AddFollowUpResponseElements(
                elements, followUp, resolveSpeakerName, subtitleLookup,
                buildRecordDetailPanel, visitedInfoFormIds, depth + 1);
        }

        if (wasTruncated)
        {
            elements.Add(DialogueTreeRenderer.CreateSecondaryLabel(
                $"Showing {MaxDisplayedFollowUps} of {sourceNode.FollowUpInfos.Count} follow-up responses",
                new Thickness(22, 2, 0, 0)));
        }
    }

    /// <summary>
    ///     Creates the NPC response card for a single INFO node.
    /// </summary>
    public static UIElement BuildNpcResponseBlock(
        InfoDialogueNode infoNode,
        Func<uint?, string> resolveSpeakerName,
        Func<uint, SubtitleEntry?> subtitleLookup,
        Func<DialogueRecord, Border> buildRecordDetailPanel,
        Action<InfoDialogueNode>? onResponseSelected = null,
        bool isSelected = false)
    {
        var info = infoNode.Info;
        var content = new StackPanel { Spacing = 4 };

        content.Children.Add(
            DialogueTreeRenderer.BuildSpeakerHeader(resolveSpeakerName(info.SpeakerFormId), info.SpeakerFormId));

        var responseTexts = info.Responses
            .Select(r => r.Text)
            .Where(text => !string.IsNullOrEmpty(text))
            .Select(text => text!)
            .ToList();

        if (responseTexts.Count > 0)
        {
            if (onResponseSelected != null)
            {
                var button = DialogueTreeRenderer.CreateClickableResponseBlock(responseTexts);
                button.Click += (_, _) => onResponseSelected(infoNode);
                content.Children.Add(button);
            }
            else
            {
                foreach (var text in responseTexts)
                {
                    content.Children.Add(DialogueTreeRenderer.CreateResponseText(text));
                }
            }
        }

        if (responseTexts.Count == 0)
        {
            var subtitle = subtitleLookup(info.FormId);
            if (subtitle?.Text != null)
            {
                if (onResponseSelected != null)
                {
                    var button = DialogueTreeRenderer.CreateClickableResponseText(
                        subtitle.Text, isSubtitleFallback: true);
                    button.Click += (_, _) => onResponseSelected(infoNode);
                    content.Children.Add(button);
                }
                else
                {
                    content.Children.Add(
                        DialogueTreeRenderer.CreateResponseText(subtitle.Text, isSubtitleFallback: true));
                }

                content.Children.Add(DialogueTreeRenderer.CreateSubtitleSourceLabel());
            }
            else if (onResponseSelected != null)
            {
                // DMP files often lack response text — show placeholder
                var button = DialogueTreeRenderer.CreateClickableResponseText(
                    "[Response not present in file]", isSubtitleFallback: true);
                button.Click += (_, _) => onResponseSelected(infoNode);
                content.Children.Add(button);
            }
        }

        var tagStrip = DialogueTreeRenderer.BuildMetadataTagStrip(
            DialogueRecordDetailBuilder.CollectMetadataTags(info));
        if (tagStrip != null)
        {
            content.Children.Add(tagStrip);
        }

        content.Children.Add(buildRecordDetailPanel(info));
        return DialogueTreeRenderer.WrapInResponseCard(content, isSelected, onResponseSelected != null);
    }

    /// <summary>
    ///     Resolves the prompt text for a player choice button and creates a styled button.
    /// </summary>
    public static Button CreatePlayerChoiceButton(
        TopicDialogueNode linkedTopic,
        InfoDialogueNode sourceInfo,
        HashSet<uint> visitedTopicFormIds,
        Action<TopicDialogueNode, string> onNavigate,
        bool showEditorId = false)
    {
        var promptText = showEditorId
            ? DialogueViewerHelper.ResolveEditorIdDisplay(linkedTopic)
            : DialogueViewerHelper.ResolvePromptText(sourceInfo, linkedTopic);
        var challengeOutcome = DialogueMetadataBuilder.DetectChallengeOutcome(ref promptText);
        var speechInfo = linkedTopic.InfoChain.FirstOrDefault(i => i.Info.IsSpeechChallenge);
        var isGoodbyeTopic = linkedTopic.InfoChain.Count > 0 && linkedTopic.InfoChain.All(i => i.Info.IsGoodbye);

        return CreateStyledChoiceButton(
            promptText, linkedTopic, visitedTopicFormIds, onNavigate,
            challengeOutcome, speechInfo?.Info.DifficultyName, isGoodbyeTopic);
    }

    /// <summary>
    ///     Creates a styled dialogue choice button (used for both player choices and unlocked topics).
    /// </summary>
    public static Button CreateStyledChoiceButton(
        string displayText, TopicDialogueNode targetTopic,
        HashSet<uint> visitedTopicFormIds,
        Action<TopicDialogueNode, string> onNavigate,
        string? challengeOutcome = null, string? speechChallengeDifficulty = null,
        bool isGoodbyeTopic = false)
    {
        var isVisited = visitedTopicFormIds.Contains(targetTopic.TopicFormId);
        var hasNoInfo = targetTopic.InfoChain.Count == 0;
        var contentPanel = DialogueTreeRenderer.BuildChoiceContent(
            displayText, isVisited, challengeOutcome, speechChallengeDifficulty, isGoodbyeTopic, hasNoInfo);

        var button = new Button
        {
            Content = contentPanel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0)
        };

        button.Click += (_, _) => onNavigate(targetTopic, displayText);
        return button;
    }

    /// <summary>
    ///     Collects unlocked topics (AddedTopics) from a filtered info chain, deduplicating by FormID.
    /// </summary>
    public static Dictionary<uint, TopicDialogueNode> CollectUnlockedTopics(List<InfoDialogueNode> filteredInfoChain)
    {
        return DialogueViewerHelper.CollectUnlockedTopics(filteredInfoChain);
    }
}

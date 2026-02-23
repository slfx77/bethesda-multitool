using FalloutXbox360Utils.Core.Formats.Esm.Models;
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
    /// <summary>
    ///     Builds the full conversation display elements from a filtered info chain.
    /// </summary>
    public static List<UIElement> BuildConversationElements(
        List<InfoDialogueNode> filteredInfoChain,
        string? promptText,
        Func<uint?, string> resolveSpeakerName,
        Func<uint, SubtitleEntry?> subtitleLookup,
        Func<DialogueRecord, Border> buildRecordDetailPanel)
    {
        var elements = new List<UIElement>();

        if (filteredInfoChain.Count == 0)
        {
            elements.Add(DialogueTreeRenderer.CreateEmptyTopicPlaceholder());
            return elements;
        }

        if (!string.IsNullOrEmpty(promptText))
        {
            elements.Add(DialogueTreeRenderer.CreatePlayerPromptBlock(promptText));
        }

        var challengeInfo = filteredInfoChain.FirstOrDefault(i => i.Info.IsSpeechChallenge);
        if (challengeInfo != null)
        {
            elements.Add(
                DialogueTreeRenderer.CreateSpeechChallengeBanner(challengeInfo.Info.DifficultyName));
        }

        if (filteredInfoChain.Count > 1)
        {
            elements.Add(DialogueTreeRenderer.CreateSecondaryLabel(
                $"{filteredInfoChain.Count} possible responses (game selects based on conditions):",
                new Thickness(0, 0, 0, 4)));
        }

        for (var i = 0; i < filteredInfoChain.Count; i++)
        {
            if (i > 0)
            {
                elements.Add(DialogueTreeRenderer.CreateResponseSeparator());
                elements.Add(DialogueTreeRenderer.CreateSecondaryLabel(
                    $"Alternative response {i + 1}:", new Thickness(0, 0, 0, 4)));
            }

            elements.Add(BuildNpcResponseBlock(
                filteredInfoChain[i], resolveSpeakerName, subtitleLookup, buildRecordDetailPanel));
        }

        return elements;
    }

    /// <summary>
    ///     Creates the NPC response card for a single INFO node.
    /// </summary>
    public static Border BuildNpcResponseBlock(
        InfoDialogueNode infoNode,
        Func<uint?, string> resolveSpeakerName,
        Func<uint, SubtitleEntry?> subtitleLookup,
        Func<DialogueRecord, Border> buildRecordDetailPanel)
    {
        var info = infoNode.Info;
        var content = new StackPanel { Spacing = 4 };

        content.Children.Add(
            DialogueTreeRenderer.BuildSpeakerHeader(resolveSpeakerName(info.SpeakerFormId), info.SpeakerFormId));

        var hasResponseText = false;
        foreach (var response in info.Responses.Where(r => !string.IsNullOrEmpty(r.Text)))
        {
            hasResponseText = true;
            content.Children.Add(DialogueTreeRenderer.CreateResponseText(response.Text!));
        }

        if (!hasResponseText)
        {
            var subtitle = subtitleLookup(info.FormId);
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

        content.Children.Add(buildRecordDetailPanel(info));
        return DialogueTreeRenderer.WrapInResponseCard(content);
    }

    /// <summary>
    ///     Resolves the prompt text for a player choice button and creates a styled button.
    /// </summary>
    public static Button CreatePlayerChoiceButton(
        TopicDialogueNode linkedTopic,
        InfoDialogueNode sourceInfo,
        HashSet<uint> visitedTopicFormIds,
        Action<TopicDialogueNode, string> onNavigate)
    {
        var promptText = DialogueViewerHelper.ResolvePromptText(sourceInfo, linkedTopic);
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

        button.Click += (_, _) => onNavigate(targetTopic, displayText);
        return button;
    }

    /// <summary>
    ///     Collects unlocked topics (AddedTopics) from a filtered info chain, deduplicating by FormID.
    /// </summary>
    public static Dictionary<uint, TopicDialogueNode> CollectUnlockedTopics(List<InfoDialogueNode> filteredInfoChain)
    {
        var addedTopics = new Dictionary<uint, TopicDialogueNode>();
        foreach (var infoNode in filteredInfoChain)
        {
            foreach (var added in infoNode.AddedTopics)
            {
                addedTopics.TryAdd(added.TopicFormId, added);
            }
        }

        return addedTopics;
    }
}

using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Semantic.LoadOrder;
using BethesdaMultitool.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Slfx77.Multitool.Core.Browsing;

namespace BethesdaMultitool;

/// <summary>Connects original dialogue-response identities to the Explore source's audio controls.</summary>
public sealed partial class SingleFileTab
{
    /// <summary>Shares the window source without transferring its ownership to the record pane.</summary>
    internal void AttachDialogueAudioSource(BrowserSnapshot snapshot) => DialogueAudioPanel.SetSource(snapshot);

    /// <summary>Adds response-specific audio controls for the currently supported Fallout games.</summary>
    private UIElement? BuildDialogueAudioControls(DialogueRecord info)
    {
        if (_session.EffectiveRecords?.Game is not (BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas) ||
            info.Responses.Count == 0) return null;
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        for (var index = 0; index < info.Responses.Count; index++)
        {
            var number = index < info.AudioSourceResponseNumbers.Count
                ? info.AudioSourceResponseNumbers[index] : info.Responses[index].ResponseNumber;
            var button = new Button
            {
                Tag = (info, index)
            };
            RuntimeLocalization.Set(button, ContentControl.ContentProperty, "DialogueAudio_Response", number);
            AutomationProperties.SetAutomationId(button, $"DialogueAudio.Response.{info.FormId:X8}.{number}");
            ToolTipService.SetToolTip(button, info.Responses[index].Text);
            button.Click += DialogueAudioResponse_Click;
            controls.Children.Add(button);
        }
        return controls;
    }

    /// <summary>Retains original response numbering and uses parsed voice identity without inventing a default voice.</summary>
    private async void DialogueAudioResponse_Click(object sender, RoutedEventArgs args)
    {
        var (info, index) = ((DialogueRecord, int))((Button)sender).Tag;
        var number = index < info.AudioSourceResponseNumbers.Count
            ? info.AudioSourceResponseNumbers[index] : info.Responses[index].ResponseNumber;
        var records = _session.EffectiveRecords;
        var voiceId = info.SpeakerVoiceTypeFormId ?? records?.Npcs
            .FirstOrDefault(npc => npc.FormId == info.SpeakerFormId)?.VoiceType;
        var voice = voiceId is { } id
            ? records?.VoiceTypes.FirstOrDefault(item => item.FormId == id)?.EditorId : null;
        var origin = LoadOrderAudioOrigin.Resolve(info, _session.LoadOrder.SelectedView?.Index,
            _session.FilePath, _session.IsEsmFile);
        await DialogueAudioPanel.ShowResponseAsync(origin.InfoFormId, number,
            origin.RecordPath, origin.UsePluginHeader, voice);
    }
}

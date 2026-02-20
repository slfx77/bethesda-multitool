using FalloutAudioTranscriber.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FalloutAudioTranscriber.Controls;

public sealed partial class VoiceFileDetailPanel : UserControl
{
    public VoiceFileDetailPanel()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user clicks Approve.</summary>
    public event EventHandler? ApproveRequested;

    /// <summary>Raised when the user clicks Transcribe (run Whisper).</summary>
    public event EventHandler? TranscribeRequested;

    /// <summary>Get or set the transcription text box content.</summary>
    public string TranscriptionText
    {
        get => TranscriptionTextBox.Text;
        set => TranscriptionTextBox.Text = value;
    }

    /// <summary>
    ///     Display details for a voice file entry.
    /// </summary>
    public void ShowEntry(VoiceFileEntry? entry)
    {
        if (entry == null)
        {
            NoSelectionText.Visibility = Visibility.Visible;
            DetailsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        NoSelectionText.Visibility = Visibility.Collapsed;
        DetailsPanel.Visibility = Visibility.Visible;

        FormIdText.Text = $"{entry.FormId:X8}";
        TopicText.Text = entry.TopicEditorId;
        VoiceTypeText.Text = entry.VoiceType;

        if (entry.SpeakerName != null)
        {
            SpeakerPanel.Visibility = Visibility.Visible;
            SpeakerText.Text = entry.SpeakerName;
        }
        else
        {
            SpeakerPanel.Visibility = Visibility.Collapsed;
        }

        if (entry.QuestName != null)
        {
            QuestPanel.Visibility = Visibility.Visible;
            QuestText.Text = entry.QuestName;
        }
        else
        {
            QuestPanel.Visibility = Visibility.Collapsed;
        }

        SubtitleText.Text = entry.HasSubtitle ? entry.SubtitleText! : "(no subtitle in ESM)";
        SubtitleText.FontStyle = entry.HasSubtitle
            ? Windows.UI.Text.FontStyle.Normal
            : Windows.UI.Text.FontStyle.Italic;

        BsaPathText.Text = entry.BsaPath;

        // Transcription controls for non-ESM entries
        if (entry.Status != TranscriptionStatus.EsmSubtitle)
        {
            TranscriptionPanel.Visibility = Visibility.Visible;
            TranscriptionStatusText.Text = entry.Status switch
            {
                TranscriptionStatus.Automatic => "Auto (pending review)",
                TranscriptionStatus.Accepted => "Accepted",
                _ => "Untranscribed"
            };
            TranscriptionTextBox.Text = entry.SubtitleText ?? "";
            TranscriptionTextBox.IsEnabled = true;
            TranscribeButton.IsEnabled = true;
            ApproveButton.IsEnabled = true;
            HideWhisperProgress();
        }
        else
        {
            TranscriptionPanel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Show the Whisper progress indicator.</summary>
    public void ShowWhisperProgress(string message)
    {
        WhisperProgressPanel.Visibility = Visibility.Visible;
        WhisperStatusText.Text = message;
        WhisperProgressBar.IsIndeterminate = true;
        TranscribeButton.IsEnabled = false;
    }

    /// <summary>Hide the Whisper progress indicator.</summary>
    public void HideWhisperProgress()
    {
        WhisperProgressPanel.Visibility = Visibility.Collapsed;
        TranscribeButton.IsEnabled = true;
    }

    /// <summary>Enable or disable the Transcribe button (e.g., when Whisper isn't ready).</summary>
    public void SetWhisperAvailable(bool available)
    {
        TranscribeButton.IsEnabled = available;
    }

    private void Approve_Click(object sender, RoutedEventArgs e) => ApproveRequested?.Invoke(this, EventArgs.Empty);
    private void Transcribe_Click(object sender, RoutedEventArgs e) => TranscribeRequested?.Invoke(this, EventArgs.Empty);
}

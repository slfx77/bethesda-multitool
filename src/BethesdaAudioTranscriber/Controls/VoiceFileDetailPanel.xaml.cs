using Windows.UI.Text;
using BethesdaAudioTranscriber.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaAudioTranscriber.Controls;

public sealed partial class VoiceFileDetailPanel : UserControl
{
    private bool _transcribeEsmMode;

    public VoiceFileDetailPanel()
    {
        InitializeComponent();
    }

    /// <summary>Get or set the transcription text box content.</summary>
    public string TranscriptionText
    {
        get => TranscriptionTextBox.Text;
        set => TranscriptionTextBox.Text = value;
    }

    /// <summary>Raised when the user clicks Approve.</summary>
    public event EventHandler? ApproveRequested;

    /// <summary>Raised when the user clicks Transcribe (run Whisper).</summary>
    public event EventHandler? TranscribeRequested;

    /// <summary>Raised when the user clicks Reject (revert to ESM text).</summary>
    public event EventHandler? RejectRequested;

    /// <summary>Raised when the user dismisses a suspected-typo flag without changes.</summary>
    public event EventHandler? DismissReviewRequested;

    /// <summary>Set whether ESM lines should show transcription controls.</summary>
    public void SetTranscribeEsmMode(bool enabled)
    {
        _transcribeEsmMode = enabled;
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

        // ESM Reference panel — visible when entry has original ESM text
        if (entry.EsmSubtitleText != null)
        {
            EsmReferencePanel.Visibility = Visibility.Visible;
            EsmReferenceText.Text = entry.EsmSubtitleText;
        }
        else
        {
            EsmReferencePanel.Visibility = Visibility.Collapsed;
        }

        ShowReviewCard(entry);

        SubtitleText.Text = entry.HasSubtitle ? entry.SubtitleText! : "(no subtitle in ESM)";
        SubtitleText.FontStyle = entry.HasSubtitle
            ? FontStyle.Normal
            : FontStyle.Italic;

        BsaPathText.Text = entry.BsaPath;

        // Transcription controls: shown for non-ESM entries, or ESM entries in transcribe mode
        if (entry.Status != TranscriptionStatus.EsmSubtitle || _transcribeEsmMode)
        {
            TranscriptionPanel.Visibility = Visibility.Visible;
            TranscriptionStatusText.Text = entry.Status switch
            {
                TranscriptionStatus.EsmSubtitle => "ESM (transcription mode)",
                TranscriptionStatus.Automatic => "Auto (pending review)",
                TranscriptionStatus.Accepted => "Accepted",
                _ => "Untranscribed"
            };
            TranscriptionTextBox.Text = entry.SubtitleText ?? "";
            TranscriptionTextBox.IsEnabled = true;
            TranscribeButton.IsEnabled = true;
            ApproveButton.IsEnabled = true;

            // Show Reject button only when entry has ESM text and has been overridden
            RejectButton.Visibility = entry.EsmSubtitleText != null
                                      && entry.TranscriptionSource != "esm"
                ? Visibility.Visible
                : Visibility.Collapsed;

            HideWhisperProgress();
        }
        else
        {
            TranscriptionPanel.Visibility = Visibility.Collapsed;
            RejectButton.Visibility = Visibility.Collapsed;
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

    /// <summary>
    ///     Show or hide the suspected-typo card for the given entry.
    /// </summary>
    private void ShowReviewCard(VoiceFileEntry entry)
    {
        var review = entry.Review;
        if (review == null || review.Resolved)
        {
            ReviewPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ReviewPanel.Visibility = Visibility.Visible;
        ReviewHeaderText.Text = $"Suspected Typo ({review.Confidence}) — {review.Checks}";
        ReviewDetailText.Text = review.Detail;

        // Warn when the transcript changed since the flag was generated
        var stale = review.FlaggedText != null
                    && entry.SubtitleText != null
                    && !string.Equals(review.FlaggedText.Trim(), entry.SubtitleText.Trim(), StringComparison.Ordinal);
        ReviewFlaggedTextPanel.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
        ReviewFlaggedText.Text = stale ? review.FlaggedText : "";

        if (!string.IsNullOrEmpty(review.SuggestedText))
        {
            ReviewSuggestionPanel.Visibility = Visibility.Visible;
            ReviewSuggestionText.Text = review.SuggestedText;
            UseSuggestionButton.Visibility = Visibility.Visible;
        }
        else
        {
            ReviewSuggestionPanel.Visibility = Visibility.Collapsed;
            UseSuggestionButton.Visibility = Visibility.Collapsed;
        }
    }

    private void UseSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ReviewSuggestionText.Text))
        {
            TranscriptionTextBox.Text = ReviewSuggestionText.Text;
        }
    }

    private void DismissReview_Click(object sender, RoutedEventArgs e)
    {
        DismissReviewRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Approve_Click(object sender, RoutedEventArgs e)
    {
        ApproveRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Transcribe_Click(object sender, RoutedEventArgs e)
    {
        TranscribeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Reject_Click(object sender, RoutedEventArgs e)
    {
        RejectRequested?.Invoke(this, EventArgs.Empty);
    }
}

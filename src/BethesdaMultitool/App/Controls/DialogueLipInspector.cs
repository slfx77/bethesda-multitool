using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Media.Audio.Dialogue;
using BethesdaMultitool.Core.Media.Audio.Lip;
using BethesdaMultitool.Core.Vfs;
using BethesdaMultitool.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Slfx77.Multitool.Core.Browsing;

namespace BethesdaMultitool;

/// <summary>Shows actual LIP source samples under explicit browser-source ownership.</summary>
internal static class DialogueLipInspector
{
    /// <summary>Inspects a unique companion or explains missing/ambiguous sources without choosing one implicitly.</summary>
    internal static async Task ShowAsync(XamlRoot root, BrowserSnapshot snapshot,
        DialogueAudioCandidate candidate, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(root);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, snapshot.CancellationToken);
        var cancellationToken = cancellation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        await using var lease = snapshot.AcquireLease();
        var body = new StackPanel { Spacing = 10, MinWidth = 380, MaxWidth = 680 };
        body.Children.Add(Label("DialogueLip_Source", snapshot.Source.DisplayName, candidate.Audio.Path));
        if (snapshot.Source is not BethesdaBrowseSource source)
        {
            body.Children.Add(Label("DialogueLip_SourceUnavailable"));
        }
        else if (candidate.LipCompanions.Count == 0)
        {
            body.Children.Add(Label("DialogueLip_Missing"));
        }
        else if (candidate.LipCompanions.Count != 1)
        {
            body.Children.Add(Label("DialogueLip_Ambiguous", candidate.LipCompanions.Count));
            foreach (var companion in candidate.LipCompanions.Take(16))
                body.Children.Add(Label("DialogueLip_Companion", companion.Path, companion.Source));
        }
        else
        {
            var companion = candidate.LipCompanions[0];
            body.Children.Add(Label("DialogueLip_Companion", companion.Path, companion.Source));
            try
            {
                var timeline = await Task.Run(() => ReadTimeline(source, companion, cancellationToken), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                AddTimeline(body, timeline);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
            {
                body.Children.Add(Label("DialogueLip_ReadFailed", exception.Message));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Content = new ScrollViewer { Content = body, MaxHeight = 620, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
        };
        RuntimeLocalization.Set(dialog, ContentDialog.TitleProperty, "DialogueLip_Title");
        RuntimeLocalization.Set(dialog, ContentDialog.CloseButtonTextProperty, "DialogueLip_Close");
        AutomationProperties.SetAutomationId(dialog, "DialogueLip.Dialog");
        // Retirement may occur off the UI thread. Queue closure so a replaced source cannot leave stale inspection open.
        // A managed lambda avoids C#/WinRT unwrapping a projected method-group target as a native delegate.
        using var registration = cancellationToken.Register(() => dialog.DispatcherQueue.TryEnqueue(() => dialog.Hide()));
        cancellationToken.ThrowIfCancellationRequested();
        await dialog.ShowAsync();
    }

    /// <summary>Checks the catalog identity and bounded bytes before decoding; the caller retains the source lease.</summary>
    private static LipTimeline ReadTimeline(BethesdaBrowseSource source, GameFileEntry companion, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (companion.Size < 0 || companion.Size > LipDecoder.MaximumEncodedBytes)
            throw new InvalidDataException(Strings.Get("DialogueLip_SizeLimit"));
        var read = source.Session.FileSystem.TryReadAllBytesBounded(companion.Path, LipDecoder.MaximumEncodedBytes)
            ?? throw new InvalidDataException(Strings.Get("DialogueLip_Unreadable"));
        token.ThrowIfCancellationRequested();
        if (!string.Equals(read.Entry.Source, companion.Source, StringComparison.Ordinal) ||
            !string.Equals(read.Entry.Path.Replace('\\', '/'), companion.Path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
            read.Entry.Size != companion.Size || read.Data.LongLength != companion.Size)
            throw new InvalidDataException(Strings.Get("DialogueLip_ProvenanceChanged"));
        return LipDecoder.Decode(read.Data, token);
    }

    /// <summary>Adds bounded metadata and one selectable sample row, avoiding a control per frame.</summary>
    private static void AddTimeline(StackPanel body, LipTimeline timeline)
    {
        var metadata = Label("DialogueLip_Metadata", timeline.FrameCount,
            LipTimeline.Tracks.Count, timeline.FramesPerSecond, timeline.StartingFrame,
            timeline.EncodedSize, timeline.DeclaredSize);
        AutomationProperties.SetAutomationId(metadata, "DialogueLip.Metadata");
        body.Children.Add(metadata);
        body.Children.Add(Label("DialogueLip_RawWeights"));
        if (timeline.FrameCount == 0)
        {
            body.Children.Add(Label("DialogueLip_Empty"));
            return;
        }
        var frame = new NumberBox
        {
            Minimum = 0, Maximum = timeline.FrameCount - 1,
            Value = 0, SmallChange = 1, LargeChange = 30, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var time = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var values = new ListView { MaxHeight = 260, SelectionMode = ListViewSelectionMode.None };
        AutomationProperties.SetAutomationId(frame, "DialogueLip.Frame");
        RuntimeLocalization.Set(frame, NumberBox.HeaderProperty, "DialogueLip_Frame");
        RuntimeLocalization.Set(frame, AutomationProperties.NameProperty, "DialogueLip_Frame");
        AutomationProperties.SetAutomationId(time, "DialogueLip.Time");
        AutomationProperties.SetAutomationId(values, "DialogueLip.Values");
        RuntimeLocalization.Set(values, AutomationProperties.NameProperty, "DialogueLip_Values");
        frame.ValueChanged += (_, args) =>
        {
            if (!double.IsFinite(args.NewValue)) return;
            var normalized = Math.Clamp(Math.Round(args.NewValue), 0, timeline.FrameCount - 1);
            if (!normalized.Equals(args.NewValue)) { frame.Value = normalized; return; }
            UpdateFrame(timeline, normalized, time, values);
        };
        UpdateFrame(timeline, 0, time, values);
        body.Children.Add(frame);
        body.Children.Add(time);
        body.Children.Add(values);
    }

    /// <summary>Updates exactly thirty-three source weights and their relative sample time.</summary>
    private static void UpdateFrame(LipTimeline timeline, double requestedFrame, TextBlock time, ListView values)
    {
        if (!double.IsFinite(requestedFrame)) return;
        var frame = (int)Math.Clamp(Math.Round(requestedFrame), 0, timeline.FrameCount - 1);
        RuntimeLocalization.Set(time, TextBlock.TextProperty, "DialogueLip_Time", timeline.GetRelativeTimeSeconds(frame));
        RuntimeLocalization.Set(time, AutomationProperties.NameProperty, "DialogueLip_Time", timeline.GetRelativeTimeSeconds(frame));
        values.Items.Clear();
        foreach (var track in LipTimeline.Tracks)
        {
            var value = timeline.GetValue(frame, track.Index);
            var row = new TextBlock();
            RuntimeLocalization.Bind(row, TextBlock.TextProperty, catalog =>
                catalog.Format("DialogueLip_TrackValue", track.Index, track.Group, track.Name,
                    value.ToString("G9", catalog.Culture)));
            values.Items.Add(row);
        }
    }

    /// <summary>Creates a wrapping, selectable text label using the current application theme.</summary>
    /// <param name="key">The resource key retained for later display-language changes.</param>
    /// <param name="arguments">Captured values inserted without translating source identities.</param>
    /// <returns>A label that updates in place while its dialog remains open.</returns>
    private static TextBlock Label(string key, params object?[] arguments)
    {
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        RuntimeLocalization.SetText(label, key, arguments);
        return label;
    }
}

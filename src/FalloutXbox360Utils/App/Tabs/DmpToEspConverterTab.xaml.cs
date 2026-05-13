using System.Threading.Channels;
using Windows.Storage.Pickers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace FalloutXbox360Utils;

/// <summary>
///     GUI tab that runs the DMP→ESP conversion pipeline. Layout: file inputs on the left,
///     virtualized progress event log on the right. Settings (plugin metadata, validation
///     options, compression) live in a slide-out drawer.
/// </summary>
public sealed partial class DmpToEspConverterTab : UserControl, IDisposable, IHasSettingsDrawer
{
    private const int LogBatchTickMs = 50;
    private const int LogMaxBatchSize = 250;

    private readonly List<ConversionEventEntry> _allEvents = [];
    private readonly DispatcherTimer _logDrainTimer;
    private CancellationTokenSource? _cts;
    private Channel<ConversionEventEntry>? _channel;
    private SeverityFilter _filter = SeverityFilter.All;
    private bool _isPcDataValid;
    private PluginBuildResult? _lastResult;
    private AssetPackingResult? _lastAssetPackingResult;

    public DmpToEspConverterTab()
    {
        InitializeComponent();
        _logDrainTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(LogBatchTickMs)
        };
        _logDrainTimer.Tick += LogDrainTimer_Tick;
    }

    private enum SeverityFilter
    {
        All,
        DecisionsAndAbove,
        WarningsAndAbove,
        ErrorsOnly
    }

    public void ToggleSettingsDrawer() => SettingsDrawerHelper.Toggle(SettingsDrawer);
    public void CloseSettingsDrawer() => SettingsDrawerHelper.Close(SettingsDrawer);

    public void Dispose()
    {
        _cts?.Dispose();
        _logDrainTimer.Stop();
    }

    private async void BrowseDmpButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".dmp");
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            DmpPathTextBox.Text = file.Path;
        }
    }

    private async void BrowsePcDataButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            PcDataDirTextBox.Text = folder.Path;
        }
    }

    private async void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add("Plugin file", new List<string> { ".esp" });

        var defaultName = "DmpToEspOutput";
        if (!string.IsNullOrEmpty(DmpPathTextBox.Text))
        {
            defaultName = Path.GetFileNameWithoutExtension(DmpPathTextBox.Text);
        }

        picker.SuggestedFileName = defaultName;
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));

        var file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            OutputEspTextBox.Text = file.Path;
        }
    }

    private void PackAssetsCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        if (PackAssetsPanel is null)
        {
            return; // Fires once during InitializeComponent before the panel exists.
        }

        PackAssetsPanel.Visibility = PackAssetsCheckBox.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Suggest a sensible default BSA path the first time the user enables packing.
        if (PackAssetsCheckBox.IsChecked == true &&
            string.IsNullOrEmpty(OutputBsaTextBox.Text) &&
            !string.IsNullOrEmpty(OutputEspTextBox.Text))
        {
            OutputBsaTextBox.Text = Path.ChangeExtension(OutputEspTextBox.Text, ".bsa");
        }
    }

    private async void BrowseOutputBsaButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add("BSA archive", new List<string> { ".bsa" });

        var defaultName = "PrototypeAssets";
        if (!string.IsNullOrEmpty(OutputEspTextBox.Text))
        {
            defaultName = Path.GetFileNameWithoutExtension(OutputEspTextBox.Text);
        }

        picker.SuggestedFileName = defaultName;
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));

        var file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            OutputBsaTextBox.Text = file.Path;
        }
    }

    /// <summary>
    ///     Parse the secondary-folders TextBox: each non-blank line is one folder. Lines
    ///     beginning with "360:" are flagged as Xbox 360 format (the converter will run
    ///     per-asset 360→PC conversion when reading from them).
    /// </summary>
    private static List<SecondaryDataFolder> ParseSecondaryFolders(string text)
    {
        var result = new List<SecondaryDataFolder>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var is360 = false;
            if (line.StartsWith("360:", StringComparison.OrdinalIgnoreCase))
            {
                is360 = true;
                line = line[4..].Trim();
            }

            if (line.Length == 0)
            {
                continue;
            }

            result.Add(new SecondaryDataFolder { Path = line, IsXbox360Format = is360 });
        }

        return result;
    }

    private void InputPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePcDataValidation();
        UpdateButtonStates();
    }

    private void UpdatePcDataValidation()
    {
        var dir = PcDataDirTextBox.Text;
        if (string.IsNullOrEmpty(dir))
        {
            _isPcDataValid = false;
            PcDataValidationText.Text = "";
            return;
        }

        var esmPath = Path.Combine(dir, "FalloutNV.esm");
        if (File.Exists(esmPath))
        {
            _isPcDataValid = true;
            PcDataValidationText.Text = $"FalloutNV.esm found ({new FileInfo(esmPath).Length / (1024.0 * 1024.0):F1} MB).";
        }
        else
        {
            _isPcDataValid = false;
            PcDataValidationText.Text = "FalloutNV.esm not found in this directory.";
        }
    }

    private void UpdateButtonStates()
    {
        var hasDmp = !string.IsNullOrEmpty(DmpPathTextBox.Text) && File.Exists(DmpPathTextBox.Text);
        var hasOutput = !string.IsNullOrEmpty(OutputEspTextBox.Text);
        var running = _cts != null && !_cts.IsCancellationRequested;

        ConvertButton.IsEnabled = hasDmp && hasOutput && _isPcDataValid && !running;
        CancelButton.IsEnabled = running;
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        var dmpPath = DmpPathTextBox.Text;
        var pcEsmPath = Path.Combine(PcDataDirTextBox.Text, "FalloutNV.esm");
        var outputPath = OutputEspTextBox.Text;

        var pcEsmFileSize = new FileInfo(pcEsmPath).Length;

        // v22 asset-rename: when the user has configured secondary data folders for asset
        // packing, reuse them for the pre-encode rename pass so the output ESP carries
        // unified paths for assets that survived under different names. Baseline = the
        // PC Data folder containing FalloutNV.esm.
        var renameFolders = PackAssetsCheckBox.IsChecked == true
            ? ParseSecondaryFolders(SecondaryFoldersTextBox.Text)
            : new List<SecondaryDataFolder>();

        var options = new PluginBuildOptions
        {
            MasterFileName = "FalloutNV.esm",
            MasterFileSize = pcEsmFileSize,
            Author = string.IsNullOrEmpty(PluginAuthorTextBox.Text) ? null : PluginAuthorTextBox.Text,
            Description = string.IsNullOrEmpty(PluginDescriptionTextBox.Text) ? null : PluginDescriptionTextBox.Text,
            CompressRecords = CompressRecordsCheckBox.IsChecked == true,
            ValidateOutput = ValidateOutputCheckBox.IsChecked == true,
            VerboseDecisions = VerboseDecisionsCheckBox.IsChecked == true,
            AssetRenameBaselineFolder = renameFolders.Count > 0 ? PcDataDirTextBox.Text : null,
            AssetRenameSecondaryFolders = renameFolders
        };

        // Set up a buffered channel for progress events.
        _channel = Channel.CreateUnbounded<ConversionEventEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var sink = new ChannelProgressSink(_channel.Writer, DispatcherQueue,
            phaseUpdate: phase => PhaseStatusTextBlock.Text = phase);

        _allEvents.Clear();
        EventsListView.ItemsSource = null;
        UpdateLogFooter();
        ResultSummaryBorder.Visibility = Visibility.Collapsed;
        _lastAssetPackingResult = null;

        _cts = new CancellationTokenSource();
        UpdateButtonStates();
        ConversionProgressBar.Visibility = Visibility.Visible;
        _logDrainTimer.Start();

        var inputs = new DmpToEspInputs
        {
            DmpPath = dmpPath,
            PcEsmPath = pcEsmPath,
            OutputEspPath = outputPath,
            Options = options
        };

        try
        {
            var registry = RecordEncoderRegistry.CreateV18bDefault();
            var builder = new PluginBuilder(registry, sink);

            // Run the engine on a worker thread so the UI thread stays free.
            var result = await Task.Run(() => builder.BuildAsync(inputs, _cts.Token), _cts.Token);
            _lastResult = result;

            // After a successful conversion, optionally run the asset packer. Progress
            // events flow into the same channel-backed log under phase = "AssetPacking".
            if (result.Success && PackAssetsCheckBox.IsChecked == true)
            {
                _lastAssetPackingResult = await RunAssetPackingAsync(result, sink, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            PhaseStatusTextBlock.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            PhaseStatusTextBlock.Text = $"Failed: {ex.Message}";
        }
        finally
        {
            _channel.Writer.Complete();
            // Drain any remaining events synchronously after the engine finishes.
            DrainChannel();
            _logDrainTimer.Stop();
            ConversionProgressBar.Visibility = Visibility.Collapsed;
            _cts?.Dispose();
            _cts = null;
            UpdateButtonStates();
            UpdateResultSummary();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        PhaseStatusTextBlock.Text = "Cancelling...";
    }

    private void ConvertAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ConvertButton.IsEnabled)
        {
            ConvertButton_Click(this, new RoutedEventArgs());
            args.Handled = true;
        }
    }

    private void CancelAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (CancelButton.IsEnabled)
        {
            CancelButton_Click(this, new RoutedEventArgs());
            args.Handled = true;
        }
    }

    private void FilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _filter = FilterComboBox.SelectedIndex switch
        {
            1 => SeverityFilter.DecisionsAndAbove,
            2 => SeverityFilter.WarningsAndAbove,
            3 => SeverityFilter.ErrorsOnly,
            _ => SeverityFilter.All
        };
        RefreshFilteredView();
    }

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        var filtered = ApplyFilter(_allEvents);
        var text = string.Join(Environment.NewLine,
            filtered.Select(en =>
                $"{en.TimeDisplay} {en.SeverityLabel,-4} [{en.Phase,-20}] {en.FormTypeDisplay,-6} {en.FormIdDisplay,-10} {en.Message}"));
        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        _allEvents.Clear();
        EventsListView.ItemsSource = null;
        UpdateLogFooter();
    }

    private void LogDrainTimer_Tick(object? sender, object e)
    {
        DrainChannel();
    }

    private void DrainChannel()
    {
        if (_channel is null)
        {
            return;
        }

        var changed = false;
        var maxThisTick = LogMaxBatchSize;
        while (maxThisTick-- > 0 && _channel.Reader.TryRead(out var entry))
        {
            _allEvents.Add(entry);
            changed = true;
        }

        if (changed)
        {
            RefreshFilteredView();
            UpdateLogFooter();
        }
    }

    private void RefreshFilteredView()
    {
        // SelectionChanged can fire while XAML is still constructing later controls.
        if (EventsListView is null)
        {
            return;
        }

        var filtered = ApplyFilter(_allEvents);
        EventsListView.ItemsSource = filtered;
    }

    private List<ConversionEventEntry> ApplyFilter(IReadOnlyList<ConversionEventEntry> events)
    {
        return _filter switch
        {
            SeverityFilter.DecisionsAndAbove =>
                events.Where(en => en.Severity >= ConversionEventSeverity.Decision).ToList(),
            SeverityFilter.WarningsAndAbove =>
                events.Where(en => en.Severity >= ConversionEventSeverity.Warning).ToList(),
            SeverityFilter.ErrorsOnly =>
                events.Where(en => en.Severity == ConversionEventSeverity.Error).ToList(),
            _ => events.ToList()
        };
    }

    private void UpdateLogFooter()
    {
        var total = _allEvents.Count;
        var errors = _allEvents.Count(en => en.Severity == ConversionEventSeverity.Error);
        var warnings = _allEvents.Count(en => en.Severity == ConversionEventSeverity.Warning);
        LogFooterTextBlock.Text =
            $"{total:N0} event(s) — {errors} error(s), {warnings} warning(s)";
    }

    private void UpdateResultSummary()
    {
        if (_lastResult == null)
        {
            return;
        }

        var s = _lastResult.Stats;
        var summary =
            $"{(_lastResult.Success ? "Conversion succeeded." : $"Conversion failed: {_lastResult.ErrorMessage}")} " +
            $"\nRecords considered: {s.RecordsConsidered:N0}; emitted: {s.RecordsEmitted:N0}; skipped: {s.RecordsSkipped:N0}; failed: {s.RecordsFailed:N0}." +
            $"\nElapsed: {s.Elapsed.TotalSeconds:F2}s. Output bytes: {s.OutputBytes:N0}.";

        if (!string.IsNullOrEmpty(_lastResult.ValidationReport))
        {
            summary += "\n\nValidation:\n" + _lastResult.ValidationReport;
        }

        if (_lastAssetPackingResult is not null)
        {
            var ps = _lastAssetPackingResult.Stats;
            if (_lastAssetPackingResult.Success)
            {
                summary +=
                    $"\n\nAsset packing: " +
                    $"already-in-baseline={ps.AlreadyInBaseline:N0}, " +
                    $"resolved-exact={ps.ResolvedExact:N0}, " +
                    $"resolved-fuzzy={ps.ResolvedFuzzy:N0}, " +
                    $"converted-360={ps.Converted360:N0}, " +
                    $"missing={ps.Missing:N0}." +
                    $"\nPacked {ps.PackedAssetCount:N0} asset(s) into " +
                    $"{(string.IsNullOrEmpty(_lastAssetPackingResult.OutputPath) ? "(no BSA written — nothing to pack)" : _lastAssetPackingResult.OutputPath)}";
            }
            else
            {
                summary += $"\n\nAsset packing failed: {_lastAssetPackingResult.ErrorMessage}";
            }
        }

        ResultSummaryTextBlock.Text = summary;
        ResultSummaryBorder.Visibility = Visibility.Visible;
    }

    /// <summary>
    ///     Invoke the asset packer after a successful conversion. Called from the UI thread
    ///     so XAML field reads are safe; the heavy work is moved off-thread internally.
    ///     Progress events flow into the same channel-backed log under phase = "AssetPacking".
    /// </summary>
    private async Task<AssetPackingResult?> RunAssetPackingAsync(
        PluginBuildResult conversionResult,
        IConversionProgressSink sink,
        CancellationToken cancellationToken)
    {
        if (conversionResult.OutputPath is null)
        {
            sink.Warn("AssetPacking", "Skipping asset packing — no ESP output path");
            return null;
        }

        // We're on the UI thread here (the builder Task.Run already awaited back). Safe
        // to read XAML fields directly.
        var secondaries = ParseSecondaryFolders(SecondaryFoldersTextBox.Text);
        var outputBsa = OutputBsaTextBox.Text;
        var baselineDataFolder = PcDataDirTextBox.Text;
        var dmpPath = DmpPathTextBox.Text;
        var verbose = VerboseDecisionsCheckBox.IsChecked == true;

        if (secondaries.Count == 0)
        {
            sink.Warn("AssetPacking",
                "Asset packing was enabled but no secondary data folders were provided");
            return null;
        }

        if (string.IsNullOrWhiteSpace(outputBsa))
        {
            sink.Warn("AssetPacking", "Asset packing was enabled but no output BSA path was provided");
            return null;
        }

        var options = new AssetPackingOptions
        {
            ConvertedEspPath = conversionResult.OutputPath,
            DmpPath = string.IsNullOrEmpty(dmpPath) ? null : dmpPath,
            BaselineDataFolder = baselineDataFolder,
            SecondaryDataFolders = secondaries,
            OutputBsaPath = outputBsa,
            VerbosePerAsset = verbose
        };

        var service = new AssetPackingService();
        // Run on a worker thread to keep the UI responsive during the BSA scan/write.
        return await Task.Run(
            () => service.PackAsync(options, sink, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Bridge between the engine's <see cref="IConversionProgressSink" /> contract and the
    ///     UI's buffered channel. Events flow from the engine thread into the channel without
    ///     touching the UI dispatcher; the UI drains them on a timer tick.
    /// </summary>
    private sealed class ChannelProgressSink : IConversionProgressSink
    {
        private readonly ChannelWriter<ConversionEventEntry> _writer;
        private readonly DispatcherQueue _dispatcher;
        private readonly Action<string> _phaseUpdate;

        public ChannelProgressSink(ChannelWriter<ConversionEventEntry> writer,
            DispatcherQueue dispatcher,
            Action<string> phaseUpdate)
        {
            _writer = writer;
            _dispatcher = dispatcher;
            _phaseUpdate = phaseUpdate;
        }

        public void OnPhaseStart(string phase, int? totalItems)
        {
            _dispatcher.TryEnqueue(() => _phaseUpdate(phase));
        }

        public void OnEvent(ConversionProgressEvent evt)
        {
            _writer.TryWrite(ConversionEventEntry.FromDomain(evt));
        }

        public void OnPhaseEnd(string phase, ConversionPipelineStats partialStats)
        {
            // Phase-end events are reflected in the running stats; no need for UI state.
        }

        public void OnComplete(ConversionPipelineStats stats)
        {
            _dispatcher.TryEnqueue(() => _phaseUpdate("Complete."));
        }
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace BethesdaMultitool;

/// <summary>
///     A minimal modal progress dialog for the (potentially expensive, tiled) map export. Built in
///     code so it adds no XAML/accessibility-ratchet surface; Cancel uses the ContentDialog's own
///     close button. <see cref="Report" /> must be called on the UI thread — feed it via an
///     <see cref="IProgress{T}" /> created on the UI thread so background callers marshal automatically.
/// </summary>
internal sealed class ExportProgressController
{
    private readonly ContentDialog _dialog;
    private readonly ProgressBar _bar;
    private readonly TextBlock _status;

    internal ExportProgressController(XamlRoot xamlRoot)
    {
        _status = new TextBlock { Text = "Preparing…" };
        _bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Width = 340, IsIndeterminate = true };

        _dialog = new ContentDialog
        {
            Title = "Exporting map",
            Content = new StackPanel { Spacing = 12, Children = { _status, _bar } },
            CloseButtonText = "Cancel",
            XamlRoot = xamlRoot
        };
        _dialog.CloseButtonClick += (_, _) => Cts.Cancel();
    }

    internal CancellationTokenSource Cts { get; } = new();

    internal IAsyncOperation<ContentDialogResult> ShowAsync() => _dialog.ShowAsync();

    internal void Complete() => _dialog.Hide();

    /// <summary>UI-thread progress update. <paramref name="total" /> ≤ 0 shows an indeterminate bar.</summary>
    internal void Report(string phase, int current, int total)
    {
        _status.Text = total > 0 ? $"{phase} ({current}/{total})" : phase;
        if (total > 0)
        {
            _bar.IsIndeterminate = false;
            _bar.Maximum = total;
            _bar.Value = Math.Clamp(current, 0, total);
        }
        else
        {
            _bar.IsIndeterminate = true;
        }
    }
}

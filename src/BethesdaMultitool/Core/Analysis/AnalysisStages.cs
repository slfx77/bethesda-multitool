using System.Diagnostics;

namespace BethesdaMultitool.Core.Analysis;

public sealed record AnalysisStageEvent(string Stage, string Status, double ElapsedSeconds,
    long Completed = 0, long? Total = null, string? Error = null);

/// <summary>Ordered stage events and cooperative cancellation for long analysis passes.</summary>
public sealed class AnalysisStages(CancellationToken cancellationToken = default,
    Action<AnalysisStageEvent>? progress = null)
{
    public CancellationToken CancellationToken { get; } = cancellationToken;

    public T Run<T>(string name, Func<Stage, T> action)
    {
        var stage = new Stage(name, CancellationToken, progress);
        stage.Emit("started");
        try
        {
            CancellationToken.ThrowIfCancellationRequested();
            var result = action(stage);
            CancellationToken.ThrowIfCancellationRequested();
            stage.Emit("completed");
            return result;
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            stage.Emit("cancelled");
            throw;
        }
        catch (Exception ex)
        {
            stage.Emit("failed", ex.Message);
            throw;
        }
    }

    public void Run(string name, Action<Stage> action) => Run(name, stage => { action(stage); return true; });

    public async Task RunAsync(string name, Func<Stage, Task> action)
    {
        var stage = new Stage(name, CancellationToken, progress);
        stage.Emit("started");
        try
        {
            CancellationToken.ThrowIfCancellationRequested();
            await action(stage);
            CancellationToken.ThrowIfCancellationRequested();
            stage.Emit("completed");
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            stage.Emit("cancelled");
            throw;
        }
        catch (Exception ex)
        {
            stage.Emit("failed", ex.Message);
            throw;
        }
    }

    public sealed class Stage(string name, CancellationToken cancellationToken,
        Action<AnalysisStageEvent>? progress)
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private long _lastReport;
        private long _completed;
        private long? _total;

        public void Checkpoint(long completed, long? total = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _completed = completed;
            _total = total;
            var now = Stopwatch.GetTimestamp();
            if (_lastReport == 0 || Stopwatch.GetElapsedTime(_lastReport, now).TotalSeconds >= 1)
            {
                _lastReport = now;
                Emit("progress");
            }
        }

        internal void Emit(string status, string? error = null) => progress?.Invoke(new AnalysisStageEvent(
            name, status, Stopwatch.GetElapsedTime(_started).TotalSeconds, _completed, _total, error));
    }
}

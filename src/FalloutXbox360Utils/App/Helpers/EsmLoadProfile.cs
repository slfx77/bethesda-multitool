using System.Diagnostics;

namespace FalloutXbox360Utils;

internal sealed class EsmLoadProfile
{
    private readonly List<(string Stage, TimeSpan Elapsed)> _stages = [];
    private readonly Stopwatch _total = Stopwatch.StartNew();

    public async Task<T> TimeAsync<T>(string stage, Func<Task<T>> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return await action();
        }
        finally
        {
            sw.Stop();
            _stages.Add((stage, sw.Elapsed));
        }
    }

    public async Task TimeAsync(string stage, Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await action();
        }
        finally
        {
            sw.Stop();
            _stages.Add((stage, sw.Elapsed));
        }
    }

    public T Time<T>(string stage, Func<T> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return action();
        }
        finally
        {
            sw.Stop();
            _stages.Add((stage, sw.Elapsed));
        }
    }

    public void Time(string stage, Action action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            action();
        }
        finally
        {
            sw.Stop();
            _stages.Add((stage, sw.Elapsed));
        }
    }

    public void Log(string filePath)
    {
        _total.Stop();
        var lines = new List<string>
        {
            $"[ESM Load] {Path.GetFileName(filePath)} total={_total.Elapsed.TotalMilliseconds:N0} ms"
        };
        lines.AddRange(_stages.Select(s => $"  {s.Stage}: {s.Elapsed.TotalMilliseconds:N0} ms"));
        Debug.WriteLine(string.Join(Environment.NewLine, lines));
    }

    internal void AddStage(string stage, TimeSpan elapsed)
    {
        _stages.Add((stage, elapsed));
    }
}

internal readonly struct EsmLoadStageTimer(EsmLoadProfile profile, string stage) : IDisposable
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public void Dispose()
    {
        _sw.Stop();
        profile.AddStage(stage, _sw.Elapsed);
    }
}

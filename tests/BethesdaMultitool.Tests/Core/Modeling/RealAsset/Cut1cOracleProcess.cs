using System.ComponentModel;
using System.Diagnostics;
using BethesdaMultitool.Tests.Helpers;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Runs a gate-1c Python oracle under <c>tools/scripts/gate1c</c> as a separate process (cut-1c plan section 9), the
///     way <see cref="NifCurveEvalProcess" /> runs hop A8's evaluator: arguments passed as a list, standard output and
///     error captured, the wait cancellable (the process tree is killed on cancellation).
/// </summary>
/// <remarks>
///     The interpreter is the first of <c>python</c>, <c>py -3</c> and <c>python3</c> on the PATH that can import numpy
///     (the A6 probe's corner test needs it; probed once per process). None is an unavailable fixture, which the caller
///     turns into a skip with the reason.
/// </remarks>
internal static class Cut1cOracleProcess
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    private static readonly Lazy<(string File, string[] Prefix)?> LazyInterpreter = new(Find);

    /// <summary>The interpreter (file and leading arguments), or null when no Python with numpy is on the PATH.</summary>
    public static (string File, string[] Prefix)? Interpreter => LazyInterpreter.Value;

    /// <summary>The absolute path of a gate-1c script in this checkout.</summary>
    public static string ScriptPath(string name)
    {
        return Path.Combine(SourceContract.RepoRoot, "tools", "scripts", "gate1c", name);
    }

    /// <summary>Runs a gate-1c script with the given arguments.</summary>
    /// <returns>The exit code and the captured standard error.</returns>
    /// <exception cref="InvalidOperationException">No interpreter is available.</exception>
    public static (int ExitCode, string Error) Run(string script, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var interpreter = Interpreter ?? throw new InvalidOperationException("No Python interpreter with numpy.");
        var all = interpreter.Prefix.Concat(new[] { ScriptPath(script) }).Concat(arguments).ToArray();
        var (exitCode, _, error) = Execute(interpreter.File, all, cancellationToken, null);
        return (exitCode, error);
    }

    private static (string File, string[] Prefix)? Find()
    {
        (string File, string[] Prefix)[] candidates =
        [
            ("python", Array.Empty<string>()), ("py", new[] { "-3" }), ("python3", Array.Empty<string>())
        ];
        foreach (var candidate in candidates)
        {
            try
            {
                var arguments = candidate.Prefix.Concat(new[] { "-c", "import numpy" }).ToArray();
                var (exitCode, _, _) = Execute(candidate.File, arguments, CancellationToken.None, ProbeTimeout);
                if (exitCode == 0)
                {
                    return candidate;
                }
            }
            catch (Win32Exception)
            {
                // Not on the PATH; try the next spelling.
            }
            catch (TimeoutException)
            {
                // A launcher that hangs (for example a store stub) is not usable.
            }
        }

        return null;
    }

    private static (int ExitCode, string Output, string Error) Execute(string file, string[] arguments,
        CancellationToken cancellationToken, TimeSpan? timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException($"Failed to start {file}.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } span)
        {
            limit.CancelAfter(span);
        }

        try
        {
            process.WaitForExitAsync(limit.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{file} did not exit within {timeout}.");
            }

            throw;
        }

        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
}

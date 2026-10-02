using System.ComponentModel;
using System.Diagnostics;
using BethesdaMultitool.Tests.Helpers;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Runs <c>tools/scripts/nif_curve_eval.py</c> (hop A8-sample's independent evaluator) as a separate process, the way
///     <c>NifGlbKhronosValidator</c> runs the glTF validator: arguments passed as a list, standard output and error
///     captured, the wait cancellable (the process tree is killed on cancellation).
/// </summary>
/// <remarks>
///     The interpreter is the first of <c>python</c>, <c>py -3</c> and <c>python3</c> on the PATH that can import numpy
///     (probed once per process); none is an unavailable fixture, which the caller turns into a skip with the reason.
/// </remarks>
internal static class NifCurveEvalProcess
{
    /// <summary>The script's repository-relative path.</summary>
    public const string RelativeScriptPath = "tools/scripts/nif_curve_eval.py";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    private static readonly Lazy<(string File, string[] Prefix)?> LazyInterpreter = new(Find);

    /// <summary>The script's absolute path in this checkout.</summary>
    public static string ScriptPath =>
        Path.Combine(SourceContract.RepoRoot, "tools", "scripts", "nif_curve_eval.py");

    /// <summary>The interpreter (file and leading arguments), or null when no Python with numpy is on the PATH.</summary>
    public static (string File, string[] Prefix)? Interpreter => LazyInterpreter.Value;

    /// <summary>Runs the script on one input file, writing the output file.</summary>
    /// <param name="input">The input JSON path.</param>
    /// <param name="output">The output JSON path.</param>
    /// <param name="cancellationToken">Cancels the wait; the process is killed.</param>
    /// <returns>The exit code and the captured standard error.</returns>
    /// <exception cref="InvalidOperationException">No interpreter is available.</exception>
    public static (int ExitCode, string Error) Run(string input, string output, CancellationToken cancellationToken)
    {
        var interpreter = Interpreter ?? throw new InvalidOperationException("No Python interpreter with numpy.");
        var arguments = interpreter.Prefix.Concat(new[] { ScriptPath, input, output }).ToArray();
        var (exitCode, _, error) = Execute(interpreter.File, arguments, cancellationToken, null);
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

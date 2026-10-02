using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>Opt-in child roles inside the existing executable test host; no external shell or helper project.</summary>
internal static class PipeProcessFixture
{
    private const string ModeVariable = "BMT_PIPE_FIXTURE_MODE";
    internal const string PidFileVariable = "BMT_PIPE_FIXTURE_PIDS";

    internal static ProcessStartInfo StartInfo(string mode)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(PipeProcessFixture).Assembly.Location);
        start.Environment[ModeVariable] = mode;
        return start;
    }

    [ModuleInitializer]
    internal static void RunChildRole()
    {
        var mode = Environment.GetEnvironmentVariable(ModeVariable);
        if (string.IsNullOrEmpty(mode)) return;
        Environment.SetEnvironmentVariable(ModeVariable, null);
        try
        {
            switch (mode)
            {
                case "backpressure":
                case "backpressure-error":
                    var output = Console.OpenStandardOutput();
                    var prefix = new byte[1024 * 1024];
                    Array.Fill(prefix, (byte)65);
                    output.Write(prefix);
                    if (mode == "backpressure-error") Console.Error.Write(new string('E', 128 * 1024));
                    Console.OpenStandardInput().CopyTo(output);
                    output.Flush();
                    Environment.Exit(0);
                    break;
                case "early-error":
                    Console.Error.Write("fixture decoder failure");
                    Environment.Exit(7);
                    break;
                case "stall":
                case "exit-with-child":
                    using (var child = Process.Start(StartInfo("grandchild")))
                    {
                        if (child is null) Environment.Exit(65);
                        File.WriteAllText(Environment.GetEnvironmentVariable(PidFileVariable)!,
                            $"{Environment.ProcessId},{child!.Id}");
                    }
                    if (mode == "exit-with-child") Environment.Exit(0);
                    Thread.Sleep(Timeout.Infinite);
                    break;
                case "grandchild":
                    Thread.Sleep(Timeout.Infinite);
                    break;
                default:
                    Environment.Exit(64);
                    break;
            }
        }
        catch (Exception exception)
        {
            Console.Error.Write(exception.Message);
            Environment.Exit(66);
        }
    }
}

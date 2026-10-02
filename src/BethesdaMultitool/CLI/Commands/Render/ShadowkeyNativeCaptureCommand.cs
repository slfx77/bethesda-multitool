using System.CommandLine;

namespace BethesdaMultitool.CLI.Commands.Render;

/// <summary>Registers a bounded native offscreen diagnostic route with explicit portable unavailability.</summary>
internal static class ShadowkeyNativeCaptureCommand
{
    /// <summary>Builds the command without opening inputs, creating outputs, or initializing a native device.</summary>
    /// <returns>The same syntax on portable and Windows builds; portable execution returns status two.</returns>
    internal static Command Create()
    {
        var command = new Command("capture-shadowkey-native",
            "Capture one real Shadowkey pack slot as a new 512x512 native PNG and provenance receipt (Windows GUI build only).");
        var pack = new Argument<string>("pack") { Description = "Real models.huge path; requires sibling models.idx and models.txt" };
        var output = new Option<string>("--output", "-o") { Required = true, Description = "New PNG destination; never overwrites" };
        var slot = new Option<int>("--slot") { Required = true, Description = "Original zero-based pack slot" };
        var frame = new Option<int>("--frame") { DefaultValueFactory = _ => 0, Description = "Static frame, default 0" };
        var skin = new Option<int>("--skin") { DefaultValueFactory = _ => 0, Description = "Skin, default 0" };
        var key = new Option<bool>("--magenta-key") { Description = "Explicitly enable the hypothetical magenta transparency key" };
        command.Arguments.Add(pack);
        command.Options.Add(output);
        command.Options.Add(slot);
        command.Options.Add(frame);
        command.Options.Add(skin);
        command.Options.Add(key);
        command.SetAction((parsed, token) =>
        {
            try
            {
                var options = new ShadowkeyNativeCaptureOptions(parsed.GetValue(pack)!, parsed.GetValue(output)!,
                    parsed.GetValue(slot), parsed.GetValue(frame), parsed.GetValue(skin), parsed.GetValue(key));
#if WINDOWS_GUI
                return ExecuteAsync(options, token);
#else
                _ = options;
                if (token.IsCancellationRequested) return Task.FromResult(3);
                Console.Error.WriteLine("Native Shadowkey capture is unavailable in this portable build; use the Windows GUI build.");
                return Task.FromResult(2);
#endif
            }
            catch (ArgumentException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return Task.FromResult(1);
            }
        });
        return command;
    }

#if WINDOWS_GUI
    /// <summary>Maps capture outcomes to stable success, failure, unsupported, and cancellation exit statuses.</summary>
    /// <param name="options">Validated paths and explicit source selection.</param>
    /// <param name="token">Command cancellation; GPU retirement remains mandatory after cancellation.</param>
    /// <returns>Zero on complete PNG plus receipt, one on failure, two on unsupported, or three on cancellation.</returns>
    private static async Task<int> ExecuteAsync(ShadowkeyNativeCaptureOptions options, CancellationToken token)
    {
        try
        {
            await ShadowkeyNativeCapture.RunAsync(options, token).ConfigureAwait(false);
            Console.WriteLine(options.OutputPath);
            Console.WriteLine(options.ReceiptPath);
            return 0;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Native Shadowkey capture was canceled.").ConfigureAwait(false);
            return 3;
        }
        catch (NotSupportedException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 2;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.ToString()).ConfigureAwait(false);
            return 1;
        }
    }
#endif
}

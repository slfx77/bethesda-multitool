using System.Diagnostics;
using BethesdaMultitool.CLI.Rendering.Gltf;
using BethesdaMultitool.Core;
using BethesdaMultitool.Tests.Helpers;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>Runs the Khronos glTF validator on encoded GLB bytes when it is available on this machine.</summary>
/// <remarks>
///     <see cref="GltfValidatorRunner.ValidateOrThrow" /> returns silently when no validator is installed, so a gate
///     calling it cannot tell "valid" from "not validated". This helper finds the executable the same way (the
///     <see cref="EnvironmentVariables.Cli.GltfValidatorExecutable" /> override, then <c>tools/gltf_validator_bin</c>
///     in this checkout or a sibling NeversoftMultitool checkout) and reports availability explicitly, reusing
///     <see cref="GltfValidatorRunner.ParseReport" /> for the report format.
/// </remarks>
internal static class NifGlbKhronosValidator
{
    /// <summary>Set to <c>1</c> to make an unavailable validator fail the gate instead of skipping validation.</summary>
    internal const string RequireVariable = "BMT_REQUIRE_GLTF_VALIDATOR";

    /// <summary>Whether this run requires the validator.</summary>
    internal static bool IsRequired =>
        string.Equals(Environment.GetEnvironmentVariable(RequireVariable), "1", StringComparison.Ordinal);

    /// <summary>Finds the validator executable, or null when none is installed.</summary>
    /// <returns>The executable path, or null.</returns>
    internal static string? FindExecutable()
    {
        var configured = EnvironmentVariables.Get(EnvironmentVariables.Cli.GltfValidatorExecutable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        string[] candidates =
        [
            Path.Combine(SourceContract.RepoRoot, "tools", "gltf_validator_bin", "gltf_validator.exe"),
            Path.Combine(SourceContract.RepoRoot, "..", "NeversoftMultitool", "tools", "gltf_validator_bin",
                "gltf_validator.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Validates one GLB and returns the validator's issue counts and first messages.</summary>
    /// <param name="executable">A path from <see cref="FindExecutable" />.</param>
    /// <param name="glb">The encoded GLB.</param>
    /// <param name="cancellationToken">Cancels the wait; the validator process is killed.</param>
    /// <returns>The parsed report; a nonzero exit code is reported as one extra error.</returns>
    internal static GltfValidatorRunner.GltfValidationSummary Validate(string executable, byte[] glb,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(glb);
        var path = Path.Combine(Path.GetTempPath(), $"bmt-nif-parity-{Guid.NewGuid():N}.glb");
        File.WriteAllBytes(path, glb);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(path);
            using var process = Process.Start(startInfo) ??
                                throw new InvalidOperationException("Failed to start the glTF validator.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            var summary = GltfValidatorRunner.ParseReport(output.GetAwaiter().GetResult());
            _ = error.GetAwaiter().GetResult();
            return process.ExitCode == 0
                ? summary
                : summary with
                {
                    NumErrors = summary.NumErrors + 1,
                    Messages = [.. summary.Messages, $"validator exit code {process.ExitCode}"]
                };
        }
        finally
        {
            File.Delete(path);
        }
    }
}

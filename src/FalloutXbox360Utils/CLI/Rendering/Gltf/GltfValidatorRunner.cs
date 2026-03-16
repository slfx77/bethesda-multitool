using System.Diagnostics;
using System.Text.Json;

namespace FalloutXbox360Utils.CLI.Rendering.Gltf;

internal static class GltfValidatorRunner
{
    internal static void ValidateOrThrow(string assetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);

        var validatorPath = TryFindValidatorPath();
        if (validatorPath == null)
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = validatorPath,
            Arguments = $"-o \"{assetPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Failed to start glTF validator process.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        var result = ParseReport(stdout);
        if (process.ExitCode != 0 || result.NumErrors > 0 || result.NumWarnings > 0)
        {
            var summary = $"glTF validator reported {result.NumErrors} error(s) and {result.NumWarnings} warning(s)";
            var details = result.Messages.Count > 0
                ? string.Join("; ", result.Messages.Take(3))
                : stderr.Trim();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(details)
                    ? summary
                    : $"{summary}: {details}");
        }
    }

    internal static GltfValidationSummary ParseReport(string reportJson)
    {
        if (string.IsNullOrWhiteSpace(reportJson))
        {
            return GltfValidationSummary.Empty;
        }

        using var document = JsonDocument.Parse(reportJson);
        if (!document.RootElement.TryGetProperty("issues", out var issuesElement))
        {
            return GltfValidationSummary.Empty;
        }

        var messages = new List<string>();
        if (issuesElement.TryGetProperty("messages", out var messagesElement))
        {
            foreach (var messageElement in messagesElement.EnumerateArray())
            {
                if (!messageElement.TryGetProperty("code", out var codeElement) ||
                    !messageElement.TryGetProperty("message", out var textElement))
                {
                    continue;
                }

                messages.Add($"{codeElement.GetString()}: {textElement.GetString()}");
            }
        }

        return new GltfValidationSummary(
            issuesElement.TryGetProperty("numErrors", out var errorsElement) ? errorsElement.GetInt32() : 0,
            issuesElement.TryGetProperty("numWarnings", out var warningsElement) ? warningsElement.GetInt32() : 0,
            issuesElement.TryGetProperty("numInfos", out var infosElement) ? infosElement.GetInt32() : 0,
            issuesElement.TryGetProperty("numHints", out var hintsElement) ? hintsElement.GetInt32() : 0,
            messages);
    }

    private static string? TryFindValidatorPath()
    {
        var envPath = Environment.GetEnvironmentVariable("GLTF_VALIDATOR_EXE");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return envPath;
        }

        foreach (var candidate in EnumerateCandidatePaths())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidatePaths()
    {
        var candidateRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory
        };

        foreach (var root in candidateRoots.ToArray())
        {
            var current = root;
            for (var depth = 0; depth < 6 && !string.IsNullOrWhiteSpace(current); depth++)
            {
                candidateRoots.Add(current);
                var parent = Directory.GetParent(current);
                if (parent == null)
                {
                    break;
                }

                current = parent.FullName;
            }
        }

        foreach (var root in candidateRoots)
        {
            yield return Path.Combine(root, "tools", "gltf_validator_bin", "gltf_validator.exe");
            yield return Path.Combine(root, "NeversoftMultitool", "tools", "gltf_validator_bin", "gltf_validator.exe");
            yield return Path.Combine(root, "..", "NeversoftMultitool", "tools", "gltf_validator_bin",
                "gltf_validator.exe");
        }
    }

    internal readonly record struct GltfValidationSummary(
        int NumErrors,
        int NumWarnings,
        int NumInfos,
        int NumHints,
        IReadOnlyList<string> Messages)
    {
        internal static readonly GltfValidationSummary Empty = new(0, 0, 0, 0, []);
    }
}

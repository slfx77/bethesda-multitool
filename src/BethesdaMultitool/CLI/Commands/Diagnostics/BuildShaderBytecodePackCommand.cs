using System.CommandLine;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

namespace BethesdaMultitool.CLI.Commands.Diagnostics;

/// <summary>
///     Build hook exposed as a CLI command so the same just-built assembly, embedded sources,
///     include resolver, flags, and authoritative permutation inventory produce the shipped DXBC.
/// </summary>
internal static class BuildShaderBytecodePackCommand
{
    internal const string CommandName = "build-shader-bytecode-pack";

    internal static Command Create()
    {
        var outputArgument = new Argument<string>("output")
        {
            Description = "Destination .dxbcpack sidecar path"
        };
        var command = new Command(
            CommandName,
            "Compile the authoritative renderer shader inventory into a validated DXBC sidecar");
        command.Arguments.Add(outputArgument);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                var outputPath = parseResult.GetValue(outputArgument)!;
                return await BuildAsync(outputPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                Console.Error.WriteLine($"Shader bytecode pack generation failed: {ex.Message}");
                return 1;
            }
        });
        return command;
    }

    internal static async Task<int> BuildAsync(string outputPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var permutations = ShaderPermutations.All
            .Select(permutation => (
                Permutation: permutation,
                Key: GpuShaderCompiler12.BuildCacheKey(
                    permutation.File,
                    permutation.EntryPoint,
                    permutation.Profile,
                    permutation.Macros)))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();

        var fingerprint = GpuShaderBytecodePack12.ComputeCurrentFingerprint();
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (File.Exists(fullOutputPath))
        {
            try
            {
                GpuShaderBytecodePack12 currentPack;
                await using (var existing = new FileStream(
                                 fullOutputPath,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.Read,
                                 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    currentPack = GpuShaderBytecodePack12.Read(
                        existing,
                        fingerprint,
                        permutations.Select(item => item.Key));
                }

                cancellationToken.ThrowIfCancellationRequested();
                // The MSBuild target deliberately launches this validator on every interactive
                // build so a newer corrupt/stale sidecar cannot bypass the fingerprint and digest
                // checks. Refresh the timestamp so downstream copy/publish steps see this validated
                // physical output as current; FXC is still skipped because the content was accepted.
                TryRefreshLastWriteTime(fullOutputPath);
                Console.WriteLine(
                    $"Shader bytecode pack is current ({currentPack.Count} permutations); " +
                    $"reusing {fullOutputPath}");
                return 0;
            }
            catch (InvalidDataException ex)
            {
                // A source, include, compiler-contract, or inventory change invalidates the exact
                // fingerprint/key-set check. Rebuild below; corrupt/truncated packs take the same
                // safe path and are replaced atomically only after every permutation compiles.
                Console.WriteLine($"Shader bytecode pack is stale ({ex.Message}); regenerating.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Reading and validating an already-built DXBC pack is platform-neutral. Keep the OS gate
        // below that fast path so compiler-free validation/reuse works in cross-platform tooling;
        // only a missing, stale, or corrupt pack actually requires the Windows FXC backend.
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Shader bytecode pack generation requires Windows D3DCompiler/FXC.");
            return 1;
        }

        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var item in permutations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var permutation = item.Permutation;
            // Deliberately bypass Compile(): generation must never read an older sidecar. The
            // explicit inventory already contains each opt-in PCF variant as a distinct key.
            var bytecode = GpuShaderCompiler12.CompileSource(
                GpuShaderCompiler12.ReadSource(permutation.File),
                permutation.File,
                permutation.EntryPoint,
                permutation.Profile,
                permutation.Macros);
            if (!entries.TryAdd(item.Key, bytecode))
            {
                throw new InvalidOperationException(
                    $"Duplicate shader permutation key '{item.Key}' ({permutation.Purpose}).");
            }
        }

        await GpuShaderBytecodePack12.WriteAtomicallyAsync(
                fullOutputPath,
                fingerprint,
                entries,
                cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine(
            $"Wrote {entries.Count} shader permutations ({new FileInfo(fullOutputPath).Length:N0} bytes) " +
            $"to {fullOutputPath}");
        return 0;
    }

    private static void TryRefreshLastWriteTime(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Content reuse is still correct if another process briefly holds the sidecar. The
            // worst case is one extra validation on the next build, not another FXC pass.
            Console.WriteLine(
                $"Shader bytecode pack is current, but its build timestamp could not be refreshed " +
                $"({ex.Message}).");
        }
    }
}

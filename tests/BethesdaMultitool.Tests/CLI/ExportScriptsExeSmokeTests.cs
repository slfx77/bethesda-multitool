using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.CLI;

/// <summary>
///     Runs <c>export scripts</c> through the SHIPPED exe (see <see cref="CliExeRunner" />) on the synthetic
///     FalloutNV.esm of <see cref="ExportScriptsTestPlugin" />. The trimmed exe disables reflection-based JSON,
///     which the test host allows, so only a run under the exe's own runtimeconfig shows that
///     <c>scripts.manifest.json</c> is written without it. The runs also pin the command's exit codes as a
///     process sees them: 0 for an export, 1 for a second run into the same directory (nothing touched), 1 for an
///     <c>--id</c> that matches nothing (no directory created), and a parse error for a bad <c>--decompiled</c>.
/// </summary>
public sealed class ExportScriptsExeSmokeTests
{
    [Fact]
    public async Task ExportScripts_TrimmedExe_WritesGekAndManifest()
    {
        CliExeRunner.AssertShippedJsonReflectionIsDisabled();
        using var directory = CliExeRunner.CreateTempDirectory();
        var plugin = CliExeRunner.WriteFalloutNvEsm(directory, ExportScriptsTestPlugin.Build());
        var outDir = Path.Combine(directory.Path, "out");
        var cancellationToken = TestContext.Current.CancellationToken;

        var result = await CliExeRunner.RunAsync(
            ["--plain", "export", "scripts", plugin, "-o", outDir],
            cancellationToken,
            workingDirectory: directory.Path);

        Assert.True(result.ExitCode == 0, result.Describe());
        Assert.DoesNotContain("Reflection-based serialization", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Exported 2 script(s)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(
            ["SmokeAuthoredSCRIPT.gek", "SmokeCompiledOnlySCRIPT.decompiled.gek", "scripts.manifest.json"],
            FileNames(outDir));

        var authoredSctx = ExportScriptsTestPlugin.AuthoredSctx();
        Assert.Equal(authoredSctx, File.ReadAllBytes(Path.Combine(outDir, "SmokeAuthoredSCRIPT.gek")));
        var decompiled = CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetString(
            File.ReadAllBytes(Path.Combine(outDir, "SmokeCompiledOnlySCRIPT.decompiled.gek")));
        Assert.StartsWith(
            "; Reconstruction from SCDA — BethesdaMultitool\r\n",
            decompiled,
            StringComparison.Ordinal);
        Assert.EndsWith("\r\nBegin OnAdd\r\nEnd", decompiled, StringComparison.Ordinal);

        // The manifest the trimmed exe wrote parses, and every hash in it is the hash of the file on disk.
        var manifestPath = Path.Combine(outDir, "scripts.manifest.json");
        var manifestBytes = File.ReadAllBytes(manifestPath);
        using (var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath)))
        {
            var root = manifest.RootElement;
            Assert.Equal("bethesda-multitool/script-export", root.GetProperty("schema").GetString());
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("toolVersion").GetString()));
            var source = root.GetProperty("source");
            Assert.Equal("plugin", source.GetProperty("kind").GetString());
            Assert.Equal("FalloutNewVegas", source.GetProperty("game").GetString());
            Assert.Equal(Sha256(File.ReadAllBytes(plugin)), source.GetProperty("sha256").GetString());
            Assert.Equal(".gek", root.GetProperty("options").GetProperty("extension").GetString());
            Assert.Equal("missing", root.GetProperty("options").GetProperty("decompiled").GetString());

            var files = root.GetProperty("scripts").EnumerateArray()
                .SelectMany(script => script.GetProperty("files").EnumerateArray())
                .ToList();
            Assert.Equal(2, files.Count);
            foreach (var file in files)
            {
                var onDisk = File.ReadAllBytes(Path.Combine(outDir, file.GetProperty("path").GetString()!));
                Assert.Equal(Sha256(onDisk), file.GetProperty("sha256").GetString());
                Assert.Equal(onDisk.Length, file.GetProperty("byteLength").GetInt32());
            }

            var summary = root.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("storedSourceFiles").GetInt32());
            Assert.Equal(1, summary.GetProperty("decompiledFiles").GetInt32());
            Assert.Equal(2, summary.GetProperty("filesWritten").GetInt32());
        }

        // The same command again: refused with exit 1, and nothing it would have written is touched.
        var again = await CliExeRunner.RunAsync(
            ["--plain", "export", "scripts", plugin, "-o", outDir],
            cancellationToken,
            workingDirectory: directory.Path);
        Assert.True(again.ExitCode == 1, again.Describe());
        Assert.Contains("already exist", again.StandardError, StringComparison.Ordinal);
        Assert.Equal(manifestBytes, File.ReadAllBytes(manifestPath));
        Assert.Equal(authoredSctx, File.ReadAllBytes(Path.Combine(outDir, "SmokeAuthoredSCRIPT.gek")));

        // Overrides: an EditorID in another case plus a FormID, the .txt extension, every reconstruction, a label.
        var optionsDir = Path.Combine(directory.Path, "options");
        var options = await CliExeRunner.RunAsync(
            [
                "--plain", "export", "scripts", plugin, "-o", optionsDir, "--id", "smokeauthoredscript", "--id",
                "0x00005002", "--ext", "txt", "--decompiled", "all", "--build-label", "Smoke Build"
            ],
            cancellationToken,
            workingDirectory: directory.Path);
        Assert.True(options.ExitCode == 0, options.Describe());
        Assert.Equal(
            [
                "SmokeAuthoredSCRIPT.decompiled.txt", "SmokeAuthoredSCRIPT.txt",
                "SmokeCompiledOnlySCRIPT.decompiled.txt", "scripts.manifest.json"
            ],
            FileNames(optionsDir));
        Assert.Equal(authoredSctx, File.ReadAllBytes(Path.Combine(optionsDir, "SmokeAuthoredSCRIPT.txt")));
        using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(optionsDir, "scripts.manifest.json"))))
        {
            var source = manifest.RootElement.GetProperty("source");
            Assert.Equal("Smoke Build", source.GetProperty("buildLabel").GetString());
            Assert.Equal("user", source.GetProperty("buildLabelSource").GetString());
        }

        // An id that matches nothing: exit 1 before anything is written.
        var unmatchedDir = Path.Combine(directory.Path, "unmatched");
        var unmatched = await CliExeRunner.RunAsync(
            ["--plain", "export", "scripts", plugin, "-o", unmatchedDir, "--id", "NoSuchScript"],
            cancellationToken,
            workingDirectory: directory.Path);
        Assert.True(unmatched.ExitCode == 1, unmatched.Describe());
        Assert.Contains("NoSuchScript", unmatched.StandardError, StringComparison.Ordinal);
        Assert.False(Directory.Exists(unmatchedDir));

        // A policy outside missing|all|none is a parse error; nothing runs.
        var bogusDir = Path.Combine(directory.Path, "bogus");
        var bogus = await CliExeRunner.RunAsync(
            ["--plain", "export", "scripts", plugin, "-o", bogusDir, "--decompiled", "bogus"],
            cancellationToken,
            workingDirectory: directory.Path);
        Assert.True(bogus.ExitCode != 0, bogus.Describe());
        Assert.Contains("bogus", bogus.StandardError, StringComparison.Ordinal);
        Assert.False(Directory.Exists(bogusDir));
    }

    private static string[] FileNames(string directory)
    {
        return Directory.GetFiles(directory)
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string Sha256(byte[] bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}

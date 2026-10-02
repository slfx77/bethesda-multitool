using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Core.Vfs;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The checked-in PC twins of the big-endian cut-1a cover files (<c>cut1a-console-twins.json</c>, written by
///     <c>tools/scripts/nif_console_twins.py</c>): for every X360 and PS3 manifest file, the PC file at the same
///     Data-relative path pinned by SHA-256 and size, or the reason none exists (an FO3-era console file with no FNV
///     PC file). The twin's bytes are read through the same Data folders the C# fixture resolver mounts (the Steam
///     Final build's, then the installed Steam Fallout New Vegas Data folder) and must reproduce the pin; a present
///     file with other bytes is rejected as a failure, so a re-sourced build or a modded install cannot pass as the
///     twin, and an absent one skips the row.
/// </summary>
/// <remarks>
///     Measured 2026-09-24 when the file was generated: all 95 big-endian manifest files have a twin, every one in
///     <c>Fallout - Meshes.bsa</c> of the Steam Final build (each record's <c>pcStep</c> is <c>steamFinalBuild</c>,
///     one of the two step names of <see cref="StepNames" />; a record naming any other step, or none, fails the
///     load), every twin at <c>20.2.0.7/uv11/bs34/LE</c>. The <c>missing</c> map exists for the FO3-era console files
///     a future manifest may add; it is empty today.
/// </remarks>
internal static class Cut1aConsoleTwins
{
    /// <summary>The twin file's repository-relative path.</summary>
    public const string RelativePath = "tests/BethesdaMultitool.Tests/Core/Modeling/Samples/cut1a-console-twins.json";

    /// <summary>The first resolution step: the Steam Final build's Data folder.</summary>
    public const string SteamFinalBuildStep = "steamFinalBuild";

    /// <summary>The second resolution step: the installed Steam Fallout New Vegas Data folder.</summary>
    public const string SteamInstallStep = "steamInstall";

    /// <summary>The step names in resolution order; the only values a record's <c>pcStep</c> may carry.</summary>
    public static readonly IReadOnlyList<string> StepNames = [SteamFinalBuildStep, SteamInstallStep];

    private static readonly Lazy<(IReadOnlyDictionary<string, Cut1aConsoleTwin> Twins,
        IReadOnlyDictionary<string, string> Missing)> LazyRecords = new(Load);

    /// <summary>Every twin the table records, keyed by the console file's SHA-256 (empty when the table is absent).</summary>
    public static IReadOnlyDictionary<string, Cut1aConsoleTwin> Twins => LazyRecords.Value.Twins;

    /// <summary>The twin of a big-endian manifest file, or a skip when the file has none or the table is absent.</summary>
    public static Cut1aConsoleTwin Require(Cut1aCoverFile file)
    {
        Assert.True(file.IsBigEndian, $"{file}: only a big-endian file has a console twin.");
        var path = Cut1aCoverManifest.RepoFile(RelativePath);
        Assert.SkipWhen(path is null || !File.Exists(path),
            $"{RelativePath} is not present; run tools/scripts/nif_console_twins.py to generate it.");
        var (twins, missing) = LazyRecords.Value;
        if (missing.TryGetValue(file.Sha256, out var reason))
        {
            Assert.Skip($"{file}: no PC twin: {reason}");
        }

        var found = twins.TryGetValue(file.Sha256, out var twin);
        Assert.SkipWhen(!found, $"{file}: {RelativePath} records no twin for SHA-256 {file.Sha256}; regenerate it.");
        Assert.Equal(file.Entry, twin!.Entry);
        return twin;
    }

    /// <summary>
    ///     The twin's bytes from the first Data folder that reproduces the pin: the Steam Final build's, then the Steam
    ///     install's. A folder that holds the path with other bytes fails the row (a wrong twin is rejected, never
    ///     compared); when no folder holds it at all, the row skips naming what was tried.
    /// </summary>
    public static byte[] ReadPcBytes(Cut1aConsoleTwin twin)
    {
        var tried = new List<string>();
        var rejected = new List<string>();
        byte[]? bytes = null;
        foreach (var (step, folder) in Folders())
        {
            if (folder is null)
            {
                tried.Add($"{step}: the Data folder is not present");
                continue;
            }

            var read = folder.TryReadAllBytesBounded(twin.PcPath, NifModelReader.MaximumSourceBytes);
            if (read is null)
            {
                tried.Add($"{step}: {twin.PcPath} is in no layer of {folder.Label}");
                continue;
            }

            if (read.Data.LongLength == twin.PcSize &&
                string.Equals(Cut1aFixtureResolver.Sha256(read.Data), twin.PcSha256, StringComparison.Ordinal))
            {
                bytes = read.Data;
                break;
            }

            rejected.Add($"{step}: {twin.PcPath} ({read.Entry.Source}) is {read.Data.LongLength} bytes with SHA-256 " +
                         $"{Cut1aFixtureResolver.Sha256(read.Data)}");
        }

        Assert.True(bytes is not null || rejected.Count == 0,
            $"{twin.Entry}: the PC twin at {twin.PcPath} is present but does not reproduce the pinned SHA-256 " +
            $"{twin.PcSha256} ({twin.PcSize} bytes): {string.Join("; ", rejected)}. A re-sourced build or a modded " +
            $"install is not the twin.");
        Assert.SkipWhen(bytes is null,
            $"{twin.Entry}: no Data folder holds the PC twin {twin.PcPath}. Tried: {string.Join("; ", tried)}. " +
            RealAssetPaths.SkipMessage("the Steam Final build"));
        return bytes!;
    }

    /// <summary>
    ///     Parses the table's JSON text. Every twin must name the step that found it with one of <see cref="StepNames" />;
    ///     a record with another token, or none, is rejected so the generator and this reader cannot drift apart silently.
    /// </summary>
    /// <exception cref="InvalidDataException">A twin's <c>pcStep</c> is missing or is not a known step name.</exception>
    internal static (IReadOnlyDictionary<string, Cut1aConsoleTwin> Twins, IReadOnlyDictionary<string, string> Missing)
        Parse(string json)
    {
        var twins = new Dictionary<string, Cut1aConsoleTwin>(StringComparer.Ordinal);
        var missing = new Dictionary<string, string>(StringComparer.Ordinal);
        var root = JsonNode.Parse(json)!.AsObject();
        foreach (var (sha256, value) in root["twins"]!.AsObject())
        {
            var twin = value!.AsObject();
            var step = twin["pcStep"]?.GetValue<string>() ??
                       throw new InvalidDataException($"{RelativePath}: twin {sha256} names no pcStep.");
            if (!StepNames.Contains(step, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"{RelativePath}: twin {sha256} names the step '{step}', which is none of {string.Join(", ", StepNames)}.");
            }

            twins[sha256] = new Cut1aConsoleTwin(sha256, twin["entry"]!.GetValue<string>(),
                twin["platform"]!.GetValue<string>(), twin["pcPath"]!.GetValue<string>(),
                twin["pcSha256"]!.GetValue<string>(), twin["pcSize"]!.GetValue<long>(),
                twin["pcLayer"]!.GetValue<string>(), step);
        }

        foreach (var (sha256, value) in root["missing"]!.AsObject())
        {
            missing[sha256] = value!["reason"]!.GetValue<string>();
        }

        return (twins, missing);
    }

    private static IEnumerable<(string Step, LayeredGameFileSystem? Folder)> Folders()
    {
        yield return (SteamFinalBuildStep, Cut1aFixtureResolver.DataFolder(Cut1aFixtureResolver.SteamFinalBuildData));
        yield return (SteamInstallStep, Cut1aFixtureResolver.SteamInstallDataFolder());
    }

    private static (IReadOnlyDictionary<string, Cut1aConsoleTwin>, IReadOnlyDictionary<string, string>) Load()
    {
        if (Cut1aCoverManifest.RepoFile(RelativePath) is not { } path || !File.Exists(path))
        {
            return (new Dictionary<string, Cut1aConsoleTwin>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        return Parse(File.ReadAllText(path));
    }
}

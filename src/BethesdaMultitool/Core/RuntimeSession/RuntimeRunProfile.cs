using System.Security.Cryptography;
using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

public sealed record RuntimeProfileFile(string OriginalPath, string CopyPath, string Sha256);
public sealed record RuntimeRunProfile(string Schema, int Version, string Root, string Activation,
    int? ProcessId, string? ExecutableSha256, IReadOnlyList<RuntimeProfileFile> Files,
    RuntimeIsolationLayout? Isolation = null, string? ProcessStartedUtc = null)
{
    public const string FileName = "runtime-profile.json";

    public static async Task<RuntimeRunProfile> PrepareAsync(IEnumerable<string> configurationFiles,
        string savesDirectory, string destination, CancellationToken token = default)
    {
        var root = Path.GetFullPath(destination);
        if (Directory.Exists(root) || File.Exists(root))
            throw new IOException("Use a new profile directory; existing profiles are preserved.");
        var sources = configurationFiles.Select(Path.GetFullPath).Select(p => (Path: p, Kind: "config"))
            .Concat(Directory.EnumerateFiles(savesDirectory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint
            }).Select(p => (Path: Path.GetFullPath(p), Kind: "saves"))).ToArray();
        if (sources.Length > 1024) throw new IOException("Profile contains more than 1024 files.");
        if (sources.Sum(s => new FileInfo(s.Path).Length) > 2L * 1024 * 1024 * 1024)
            throw new IOException("Profile copies exceed 2 GiB.");
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if ((File.GetAttributes(source.Path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Profile input cannot be a reparse point.");
            if (!targets.Add(Path.Combine(source.Kind, Path.GetFileName(source.Path))))
                throw new IOException("Profile input names collide.");
        }
        Directory.CreateDirectory(root);
        var files = new List<RuntimeProfileFile>();
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            var target = Path.Combine(root, source.Kind, Path.GetFileName(source.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var before = await Hash(source.Path, token);
            await using (var input = File.OpenRead(source.Path))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                await input.CopyToAsync(output, token);
            if (before != await Hash(target, token) || before != await Hash(source.Path, token))
                throw new IOException("Profile input changed while copying.");
            files.Add(new(source.Path, target, before));
        }
        var profile = new RuntimeRunProfile("bmt/runtime-profile", 1, root, "prepared-not-engine-verified", null, null, files);
        await File.WriteAllTextAsync(Path.Combine(root, FileName),
            JsonSerializer.Serialize(profile, RuntimeJsonContext.Default.RuntimeRunProfile), token);
        return profile;
    }

    public async Task ValidateForRunAsync(JsonElement identity, CancellationToken token = default)
    {
        if (Schema != "bmt/runtime-profile" || Version != 1) throw new InvalidDataException("Unsupported profile manifest.");
        if (Activation != "verified-usvfs" || Isolation is null || ProcessId != identity.GetProperty("processId").GetInt32() ||
            ExecutableSha256 != identity.GetProperty("executableFileSha256").GetString() ||
            ProcessStartedUtc != identity.GetProperty("processStartedUtc").GetString())
            throw new InvalidOperationException("Profile copies exist, but this engine process is not verified to use them.");
        if (Isolation.ReusedInstallation is not null) RuntimeInstallationLease.RequireOwner(this);
        await VerifyOriginalsAsync(token);
        var state = JsonSerializer.Deserialize(await RuntimeIsolationService.ReadSharedText(Path.Combine(Root, RuntimeIsolationService.StateFileName), token),
            RuntimeJsonContext.Default.RuntimeIsolationState) ?? throw new InvalidDataException("Isolation controller state is missing.");
        if (state.Status != "active" || state.GameProcessId != ProcessId || state.ProcessStartedUtc != ProcessStartedUtc ||
            state.ExecutableSha256 != ExecutableSha256 || state.BridgeSha256 != Isolation.BridgeSha256 ||
            !state.VfsProcessIds.Contains(ProcessId.Value) || !DateTimeOffset.TryParse(state.UpdatedUtc, out var updated) ||
            DateTimeOffset.UtcNow - updated > TimeSpan.FromSeconds(10))
            throw new InvalidOperationException("The isolation controller has no current matching engine membership observation.");
        if (await Hash(Isolation.BridgePath, token) != Isolation.BridgeSha256)
            throw new InvalidDataException("Runtime bridge binary changed.");
        if (state.BridgeMappedFile is null || !RuntimeModuleIdentity.IsBackingFile(state.BridgeMappedFile, Isolation.BridgePath))
            throw new InvalidOperationException("The controller has no matching mapped backing-file identity for the runtime bridge.");
    }

    internal async Task VerifyCopiesAsync(CancellationToken token)
    {
        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Isolation?.ReusedInstallation is not null) RuntimeInstallationLease.RequireOwner(this);
        foreach (var file in Files)
        {
            if (!Path.GetFullPath(file.CopyPath).StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                !(Isolation?.ReusedInstallation is { } reused && RuntimeIsolationService.ContainsPath(reused, file.CopyPath)))
                throw new InvalidDataException("Profile copy escapes its root.");
            if (await Hash(file.CopyPath, token) != file.Sha256)
                throw new InvalidDataException($"Profile copy changed before the run: {file.CopyPath}");
        }
    }

    public async Task VerifyOriginalsAsync(CancellationToken token = default)
    {
        if (Isolation is not null)
        {
            await RuntimeIsolationService.VerifySourceInventoriesAsync(Isolation, token);
            return;
        }
        foreach (var file in Files)
            if (await Hash(file.OriginalPath, token) != file.Sha256)
                throw new IOException($"Original profile file changed: {file.OriginalPath}");
    }

    private static async Task<string> Hash(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
}

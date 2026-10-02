using System.Security.Cryptography;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace BethesdaMultitool.Core.RuntimeSession;

public sealed record RuntimeGuestRunOptions(string GuestManifestPath, string GameDirectory,
    string ConfigurationFile, string SavesDirectory, string ScenarioPath, string Destination,
    string? InitialSaveRelativePath = null, string? GameConfigurationFile = null);

public sealed record RuntimeGuestRunEnvironment(string GameRoot, string StorageRoot, string ContentRoot,
    string CacheRoot, string ConfigPath, string? GameConfigPath);

/// <summary>Controller validation of files and effective routes; capture binding is a separate native handshake.</summary>
public sealed record RuntimeGuestRunAdmission(string ProfilePath, string ProfileSha256, int ProcessId,
    string ProcessStartedUtc, string ScenarioSha256, RuntimeGuestRunEnvironment Environment);

/// <summary>A fresh Xbox writable environment, with immutable input pins and separately mutable copied outputs.</summary>
public sealed record RuntimeGuestRunProfile(string Schema, int Version, string PreparedUtc, string Root,
    RuntimeGuestManifest GuestManifest, RuntimeProfileFile GuestManifestFile, RuntimeProfileFile Scenario,
    RuntimeProfileFile Configuration, RuntimeProfileFile? GameConfiguration, RuntimeProfileFile? InitialSave,
    RuntimeGuestRunEnvironment Environment, IReadOnlyList<RuntimeProfileFile> Files,
    IReadOnlyList<RuntimeSourceRoot> SourceRoots, IReadOnlyList<RuntimeSourceFile> SourceFiles,
    IReadOnlyList<string> InitialDirectories)
{
    public const string FileName = "runtime-guest-profile.json";
    private const int MaximumFiles = 100_000;
    private const int MaximumCopies = 4096;
    private const long MaximumSourceBytes = 64L * 1024 * 1024 * 1024;
    private const long MaximumCopyBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumManifestBytes = 32 * 1024 * 1024;

    [JsonIgnore] public string ProfilePath => Path.Combine(Root, FileName);
    [JsonIgnore] public string? ProfileSha256 { get; private init; }

    public static async Task<RuntimeGuestRunProfile> PrepareAsync(RuntimeGuestRunOptions options,
        CancellationToken token = default)
    {
        RequireWindows();
        token.ThrowIfCancellationRequested();
        var root = FullPath(options.Destination);
        if (Path.Exists(root)) throw new IOException("Use a new guest-session directory; earlier sessions are preserved.");
        var game = FullPath(options.GameDirectory);
        var saves = FullPath(options.SavesDirectory);
        var manifestPath = FullPath(options.GuestManifestPath);
        var config = FullPath(options.ConfigurationFile);
        var scenario = FullPath(options.ScenarioPath);
        var gameConfig = options.GameConfigurationFile is { } gc ? FullPath(gc) : null;
        foreach (var path in new[] { root, game, saves, manifestPath, config, scenario }.Concat(gameConfig is null ? [] : new[] { gameConfig }))
            RejectReparsePath(path);
        if (!Directory.Exists(game) || !Directory.Exists(saves))
            throw new DirectoryNotFoundException("Game and original save directories must exist (an empty save directory is supported).");
        RejectOverlap(game, saves);
        RejectOverlap(root, game);
        RejectOverlap(root, saves);
        foreach (var path in new[] { manifestPath, config, scenario }.Concat(gameConfig is null ? [] : new[] { gameConfig }))
            if (Contains(root, path)) throw new IOException("An original input is inside the session directory.");

        var manifestBytes = await ReadBoundedAsync(manifestPath, 1024 * 1024, token);
        var manifest = JsonSerializer.Deserialize(manifestBytes, RuntimeJsonContext.Default.RuntimeGuestManifest)
            ?? throw new InvalidDataException("Guest manifest is empty.");
        var emulator = manifest.Emulator ?? throw new InvalidDataException("Guest manifest emulator is required.");
        var guest = manifest.Guest ?? throw new InvalidDataException("Guest manifest guest executable is required.");
        // Reuse the same backing-file admission as an actual connection, without claiming a running process.
        using (var syntheticIdentity = JsonDocument.Parse(new JsonObject
        {
            ["backend"] = "xenia-canary", ["executablePath"] = emulator.Path,
            ["executableFileSha256"] = emulator.Sha256,
            ["guestExecutablePath"] = guest.Path, ["guestExecutableSha256"] = guest.Sha256
        }.ToJsonString()))
            await manifest.ValidateAsync(syntheticIdentity.RootElement, token);
        foreach (var backing in manifest.Plugins.Prepend(guest).Prepend(emulator))
        {
            RejectReparsePath(backing.Path);
            if (Contains(root, backing.Path)) throw new IOException("Pinned backing file is inside the writable session.");
        }
        if (!Contains(game, guest.Path) || manifest.Plugins.Any(p => !Contains(game, p.Path)))
            throw new InvalidDataException("The guest executable and candidate plugins must belong to the declared game root.");

        var gameScan = Scan(game, token);
        var saveScan = Scan(saves, token);
        var standalone = new[] { manifestPath, config, scenario, emulator.Path }
            .Concat(gameConfig is null ? [] : new[] { gameConfig });
        var originalPaths = gameScan.Files.Concat(saveScan.Files).Concat(standalone)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (originalPaths.Length > MaximumFiles || originalPaths.Sum(p => new FileInfo(p).Length) > MaximumSourceBytes)
            throw new IOException("Guest originals exceed 100,000 files or 64 GiB.");
        var originalFiles = new List<RuntimeSourceFile>();
        foreach (var path in originalPaths)
            originalFiles.Add(new(path, await StableHashAsync(path, token)));
        if (originalFiles.Single(f => SamePath(f.Path, manifestPath)).Sha256 != Convert.ToHexStringLower(SHA256.HashData(manifestBytes)))
            throw new IOException("Guest manifest changed during preparation.");

        var storage = Path.Combine(root, "storage");
        var content = Path.Combine(root, "content");
        var cache = Path.Combine(root, "cache");
        var inputs = Path.Combine(root, "inputs");
        var configCopy = Path.Combine(storage, "xenia-canary.config.toml");
        var gameConfigCopy = gameConfig is null ? null : Path.Combine(storage, "config", Path.GetFileName(gameConfig));
        var copyPairs = new List<(string Source, string Copy)>
        {
            (manifestPath, Path.Combine(inputs, "guest-manifest.json")),
            (scenario, Path.Combine(inputs, "scenario.json")), (config, configCopy)
        };
        if (gameConfig is not null) copyPairs.Add((gameConfig, gameConfigCopy!));
        copyPairs.AddRange(saveScan.Files.Select(p => (p, Path.Combine(content, Path.GetRelativePath(saves, p)))));
        if (copyPairs.Count > MaximumCopies || copyPairs.Sum(p => new FileInfo(p.Source).Length) > MaximumCopyBytes)
            throw new IOException("Guest configuration/save copies exceed 4096 files or 2 GiB.");
        var selectedSave = options.InitialSaveRelativePath is { } relative ? RelativeFile(saves, relative) : null;
        if ((saveScan.Files.Length != 0 && selectedSave is null) ||
            (selectedSave is not null && !saveScan.Files.Contains(selectedSave, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("Select an existing relative initial save, or supply an empty save directory.");

        // Every write is CreateNew below a newly created root. A cancelled/failed preparation is retained.
        Directory.CreateDirectory(root);
        foreach (var path in new[] { storage, content, cache, inputs, Path.Combine(storage, "config") })
            Directory.CreateDirectory(path);
        foreach (var directory in saveScan.Directories) Directory.CreateDirectory(RelativeFile(content, directory));
        var copies = new List<RuntimeProfileFile>();
        foreach (var (source, target) in copyPairs)
        {
            token.ThrowIfCancellationRequested();
            RejectReparsePath(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var expected = originalFiles.Single(f => SamePath(f.Path, source)).Sha256;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, token);
            if (await StableHashAsync(target, token) != expected || await StableHashAsync(source, token) != expected)
                throw new IOException($"Guest input changed while copying: {source}");
            copies.Add(new(source, target, expected));
        }
        var profile = new RuntimeGuestRunProfile("bmt/runtime-guest-run-profile", 1, DateTimeOffset.UtcNow.ToString("O"), root,
            manifest, copies[0], copies[1], copies[2], gameConfig is null ? null : copies[3],
            selectedSave is null ? null : copies.Single(f => SamePath(f.OriginalPath, selectedSave) && Contains(content, f.CopyPath)),
            new(game, storage, content, cache, configCopy, gameConfigCopy), copies,
            [new(game, true, gameScan.Directories), new(saves, true, saveScan.Directories)], originalFiles,
            Scan(root, token).Directories);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(profile, RuntimeJsonContext.Default.RuntimeGuestRunProfile);
        if (bytes.Length > MaximumManifestBytes) throw new IOException("Guest run manifest exceeds 32 MiB.");
        await using (var output = new FileStream(profile.ProfilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await output.WriteAsync(bytes, token);
        profile = profile with { ProfileSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        await profile.VerifyOriginalsAsync(token);
        await profile.VerifyInitialCopiesAsync(token);
        return profile;
    }

    public static async Task<RuntimeGuestRunProfile> LoadAsync(string path, string? expectedSha256 = null,
        CancellationToken token = default)
    {
        RequireWindows();
        RejectReparsePath(path);
        var bytes = await ReadBoundedAsync(path, MaximumManifestBytes, token);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (expectedSha256 is not null && !sha.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Guest run profile hash changed.");
        var profile = JsonSerializer.Deserialize(bytes, RuntimeJsonContext.Default.RuntimeGuestRunProfile)
            ?? throw new InvalidDataException("Guest run profile is empty.");
        profile.ValidateStructure();
        if (!SamePath(path, profile.ProfilePath)) throw new InvalidDataException("Guest profile is outside its declared root.");
        return profile with { ProfileSha256 = sha };
    }

    /// <summary>Pre-launch only: Xenia may rewrite copied config/saves after launch.</summary>
    public async Task VerifyInitialCopiesAsync(CancellationToken token = default)
    {
        await VerifyManifestAsync(token);
        var scan = Scan(Root, token);
        var expected = Files.Select(f => FullPath(f.CopyPath)).Append(ProfilePath).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!scan.Files.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase) ||
            !scan.Directories.SequenceEqual(InitialDirectories, StringComparer.OrdinalIgnoreCase))
            throw new IOException("Initial guest-session inventory changed.");
        foreach (var file in Files)
        {
            RejectSharedFileLinks(file.CopyPath);
            if (await StableHashAsync(file.CopyPath, token) != file.Sha256)
                throw new IOException($"Initial guest copy changed: {file.CopyPath}");
        }
    }

    public async Task VerifyOriginalsAsync(CancellationToken token = default)
    {
        await VerifyManifestAsync(token);
        foreach (var root in SourceRoots)
        {
            var actual = Scan(root.Path, token);
            var expected = SourceFiles.Where(f => Contains(root.Path, f.Path)).Select(f => f.Path)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (Directory.Exists(root.Path) != root.Existed ||
                !actual.Files.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase) ||
                !actual.Directories.SequenceEqual(root.Directories, StringComparer.OrdinalIgnoreCase))
                throw new IOException($"Original guest directory inventory changed: {root.Path}");
        }
        foreach (var file in SourceFiles)
            if (await StableHashAsync(file.Path, token) != file.Sha256)
                throw new IOException($"Original guest file changed: {file.Path}");
    }

    public async Task<RuntimeGuestRunAdmission> ValidateForRunAsync(JsonElement identity, string scenarioSha256,
        CancellationToken token = default)
    {
        await VerifyOriginalsAsync(token);
        if (!Scenario.Sha256.Equals(scenarioSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Scenario does not match the prepared guest run.");
        foreach (var input in new[] { Scenario, GuestManifestFile })
            if (await StableHashAsync(input.CopyPath, token) != input.Sha256)
                throw new InvalidDataException("Immutable copied run input changed.");
        var backingManifest = JsonSerializer.Deserialize(await ReadBoundedAsync(GuestManifestFile.CopyPath, 1024 * 1024, token),
            RuntimeJsonContext.Default.RuntimeGuestManifest) ?? throw new InvalidDataException("Copied guest manifest is empty.");
        if (!JsonSerializer.SerializeToUtf8Bytes(backingManifest, RuntimeJsonContext.Default.RuntimeGuestManifest).AsSpan()
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(GuestManifest, RuntimeJsonContext.Default.RuntimeGuestManifest)))
            throw new InvalidDataException("Profile guest pins differ from the copied guest manifest.");
        await GuestManifest.ValidateAsync(identity, token);
        ValidateEffectiveEnvironment(identity);
        if (!identity.TryGetProperty("processId", out var pid) || !pid.TryGetInt32(out var processId) || processId <= 0 ||
            Text(identity, "processStartedUtc") is not { } started ||
            !DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.None, out var startedAt))
            throw new InvalidDataException("Guest process identity is unavailable.");
        var observed = identity.GetProperty("effectiveEnvironment");
        if (!observed.TryGetProperty("processId", out var owner) || !owner.TryGetInt32(out var ownerId) || ownerId != processId ||
            !ulong.TryParse(Text(observed, "processCreationFileTime"), NumberStyles.None, CultureInfo.InvariantCulture, out var created) ||
            startedAt.UtcDateTime.Year < 1601 || created != (ulong)startedAt.UtcDateTime.ToFileTimeUtc() ||
            !IsBoolean(observed, "windowValid", true) ||
            !observed.TryGetProperty("windowProcessId", out var windowOwner) || !windowOwner.TryGetInt32(out var windowOwnerId) || windowOwnerId != processId)
            throw new InvalidDataException("Effective guest environment belongs to another process or window.");
        // Detect escaped writable descendants after startup; ordinary new output files are allowed.
        foreach (var route in new[] { Environment.StorageRoot, Environment.ContentRoot, Environment.CacheRoot })
            foreach (var path in Scan(route, token).Files) RejectSharedFileLinks(path);
        await VerifyManifestAsync(token);
        return new(ProfilePath, ProfileSha256!, processId, started, Scenario.Sha256, Environment);
    }

    public void ValidateEffectiveEnvironment(JsonElement identity)
    {
        ValidateStructure();
        if (identity.ValueKind != JsonValueKind.Object || !identity.TryGetProperty("effectiveEnvironment", out var observed) ||
            observed.ValueKind != JsonValueKind.Object || Text(observed, "status") != "observed" ||
            !observed.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var schemaVersion) || schemaVersion != 1 ||
            !SamePath(Text(observed, "storageRoot"), Environment.StorageRoot) ||
            !SamePath(Text(observed, "contentRoot"), Environment.ContentRoot) ||
            !SamePath(Text(observed, "cacheRoot"), Environment.CacheRoot) ||
            !SamePath(Text(observed, "gameRoot"), Environment.GameRoot) ||
            !SamePath(Text(observed, "configPath"), Environment.ConfigPath) ||
            !IsBoolean(observed, "gameReadOnly", true) || !IsBoolean(observed, "allowPlugins", false) ||
            !IsBoolean(observed, "allowGameRelativeWrites", false) ||
            Text(observed, "globalConfigStatus") != "loaded" ||
            !SamePath(Text(observed, "globalConfigReadPath"), Configuration.CopyPath))
            throw new InvalidOperationException("Emulator effective routes or policies do not match the isolated guest profile.");
        var gamePath = Text(observed, "gameConfigPath");
        if (GameConfiguration is null)
        {
            if (Text(observed, "gameConfigStatus") != "absent" || string.IsNullOrWhiteSpace(gamePath) ||
                !SamePath(Path.GetDirectoryName(gamePath), Path.Combine(Environment.StorageRoot, "config")) || Path.Exists(gamePath))
                throw new InvalidOperationException("An unprepared per-game configuration was loaded or its route is unavailable.");
        }
        else if (Text(observed, "gameConfigStatus") != "loaded" || !SamePath(gamePath, GameConfiguration.CopyPath))
            throw new InvalidOperationException("The effective per-game configuration does not match its private copy.");
        foreach (var path in new[] { Environment.GameRoot, Environment.StorageRoot, Environment.ContentRoot,
                     Environment.CacheRoot, Environment.ConfigPath, gamePath! }) RejectReparsePath(path);
    }

    private async Task VerifyManifestAsync(CancellationToken token)
    {
        ValidateStructure();
        if (ProfileSha256 is null) throw new InvalidOperationException("Load or prepare the pinned guest profile first.");
        var bytes = await ReadBoundedAsync(ProfilePath, MaximumManifestBytes, token);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ProfileSha256)
            throw new InvalidDataException("Guest profile changed after admission.");
        var saved = JsonSerializer.Deserialize(bytes, RuntimeJsonContext.Default.RuntimeGuestRunProfile)
            ?? throw new InvalidDataException("Guest profile is empty.");
        if (!JsonSerializer.SerializeToUtf8Bytes(saved, RuntimeJsonContext.Default.RuntimeGuestRunProfile).AsSpan()
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(this, RuntimeJsonContext.Default.RuntimeGuestRunProfile)))
            throw new InvalidDataException("Guest profile object differs from its pinned manifest.");
    }

    private void ValidateStructure()
    {
        RequireWindows();
        if (Schema != "bmt/runtime-guest-run-profile" || Version != 1 || string.IsNullOrWhiteSpace(Root) ||
            !Path.IsPathFullyQualified(Root) || GuestManifest is null || Environment is null ||
            Files is null || Files.Count is < 3 or > MaximumCopies || SourceFiles is null || SourceFiles.Count is < 1 or > MaximumFiles ||
            SourceRoots is null || SourceRoots.Count != 2 || InitialDirectories is null ||
            new[] { GuestManifestFile, Scenario, Configuration }.Any(f => f is null))
            throw new InvalidDataException("Unsupported guest run profile.");
        foreach (var path in new[] { Root, Environment.GameRoot, Environment.StorageRoot, Environment.ContentRoot,
                     Environment.CacheRoot, Environment.ConfigPath })
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new InvalidDataException("Guest profile requires absolute routes.");
        RejectReparsePath(Root);
        foreach (var (path, leaf) in new[] { (Environment.StorageRoot, "storage"), (Environment.ContentRoot, "content"), (Environment.CacheRoot, "cache") })
            if (!SamePath(path, Path.Combine(Root, leaf))) throw new InvalidDataException("Writable guest roots must be fresh private siblings.");
        if (!SamePath(Environment.ConfigPath, Configuration.CopyPath) ||
            !SamePath(Configuration.CopyPath, Path.Combine(Environment.StorageRoot, "xenia-canary.config.toml")) ||
            (GameConfiguration is null ? Environment.GameConfigPath is not null : !SamePath(Environment.GameConfigPath, GameConfiguration.CopyPath)))
            throw new InvalidDataException("Private config route is inconsistent.");
        if (GameConfiguration is not null && !Contains(Path.Combine(Environment.StorageRoot, "config"), GameConfiguration.CopyPath))
            throw new InvalidDataException("Per-game config escapes private storage.");
        if (!SourceRoots.Any(r => SamePath(r.Path, Environment.GameRoot)) || SourceRoots.Any(r => !r.Existed))
            throw new InvalidDataException("Original root inventory is incomplete.");
        RejectOverlap(SourceRoots[0].Path, SourceRoots[1].Path);
        foreach (var source in SourceRoots)
        {
            RejectOverlap(Root, source.Path);
            RejectReparsePath(source.Path);
            if (source.Directories is null) throw new InvalidDataException("Original directory inventory is missing.");
            foreach (var directory in source.Directories) _ = RelativeFile(source.Path, directory);
        }
        var originals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in SourceFiles)
            if (file is null || !ValidHash(file.Sha256) || !Path.IsPathFullyQualified(file.Path) || Contains(Root, file.Path) || !originals.Add(FullPath(file.Path)))
                throw new InvalidDataException("Original file pins are invalid or ambiguous.");
        var copies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (file is null || !ValidHash(file.Sha256) || !Path.IsPathFullyQualified(file.CopyPath) ||
                !Contains(Root, file.CopyPath) || SamePath(file.CopyPath, ProfilePath) || !copies.Add(FullPath(file.CopyPath)) ||
                !SourceFiles.Any(f => SamePath(f.Path, file.OriginalPath) && f.Sha256 == file.Sha256))
                throw new InvalidDataException("Guest copy escapes its root or lacks an original pin.");
            RejectReparsePath(file.CopyPath);
        }
        foreach (var selected in new[] { GuestManifestFile, Scenario, Configuration, GameConfiguration, InitialSave }.OfType<RuntimeProfileFile>())
            if (!Files.Contains(selected)) throw new InvalidDataException("Selected guest input is not in the copy inventory.");
        if (InitialSave is not null && !Contains(Environment.ContentRoot, InitialSave.CopyPath))
            throw new InvalidDataException("Selected initial save is outside content storage.");
        foreach (var directory in InitialDirectories) _ = RelativeFile(Root, directory);
    }

    private static (string[] Files, string[] Directories) Scan(string root, CancellationToken token)
    {
        RejectReparsePath(root);
        if (!Directory.Exists(root)) return ([], []);
        var files = new List<string>();
        var directories = new List<string>();
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"Guest path is a reparse point: {entry}");
                if ((attributes & FileAttributes.Directory) != 0) { directories.Add(Path.GetRelativePath(root, entry)); pending.Push(entry); }
                else files.Add(FullPath(entry));
                if (files.Count + directories.Count > MaximumFiles) throw new IOException("Guest inventory exceeds 100,000 entries.");
            }
        }
        return (files.Order(StringComparer.OrdinalIgnoreCase).ToArray(), directories.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static async Task<string> StableHashAsync(string path, CancellationToken token)
    {
        RejectReparsePath(path);
        var before = new FileInfo(path);
        var length = before.Length; var stamp = before.LastWriteTimeUtc;
        var hash = await RuntimeIsolationService.Hash(path, token);
        RejectReparsePath(path);
        var after = new FileInfo(path);
        if (length != after.Length || stamp != after.LastWriteTimeUtc) throw new IOException($"Guest input changed while hashing: {path}");
        return hash;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken token)
    {
        RejectReparsePath(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is 0 || input.Length > maximum) throw new InvalidDataException("Guest manifest size is invalid.");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    private static void RejectReparsePath(string path)
    {
        var current = FullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Guest path traverses a reparse point: {current}");
            current = Path.GetDirectoryName(current);
        }
    }

    private static string RelativeFile(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("A relative guest path is required.");
        var full = FullPath(Path.Combine(root, relative));
        if (!Contains(root, full) || SamePath(root, full)) throw new InvalidDataException("Relative guest path escapes its root.");
        return full;
    }
    private static void RejectOverlap(string a, string b)
    {
        if (Contains(a, b) || Contains(b, a)) throw new IOException("Guest session and original directory scopes must not overlap.");
    }
    private static string FullPath(string path)
    {
        var result = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        // Device aliases, alternate streams and Win32 trailing-dot/space aliases cannot define isolation roots.
        if (result.StartsWith(@"\\?\", StringComparison.Ordinal) || result.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            result[Path.GetPathRoot(result)!.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.Contains(':') || part.EndsWith('.') || part.EndsWith(' ')))
            throw new InvalidDataException("Guest paths must use unambiguous filesystem names.");
        return result;
    }

    internal static void RejectSharedFileLinks(string path)
    {
        const uint readAttributes = 0x80, shareReadWriteDelete = 7, openExisting = 3;
        const uint openReparsePoint = 0x00200000, reparsePointAttribute = 0x400;
        // Link metadata does not require access to an engine-owned file's contents.
        using var handle = CreateFileW(path, readAttributes, shareReadWriteDelete, nint.Zero,
            openExisting, openReparsePoint, nint.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & reparsePointAttribute) != 0)
            throw new IOException($"A writable guest file is a reparse point: {path}");
        if (info.Links != 1) throw new IOException($"A writable guest file has shared hard links: {path}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security,
        uint creationDisposition, uint flags, nint template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    private static bool Contains(string root, string path) => RuntimeIsolationService.ContainsPath(root, path);
    private static bool SamePath(string? a, string? b) => RuntimeGuestManifest.SamePath(a, b);
    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit);
    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static bool IsBoolean(JsonElement value, string name, bool expected) => value.TryGetProperty(name, out var item) && item.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False);
    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Guest run admission requires Windows backing-file observations.");
    }
}

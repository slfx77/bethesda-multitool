using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace BethesdaMultitool.Core.RuntimeSession;

public sealed record RuntimeBackingFile(string Name, string Path, string Sha256, long Length,
    ulong LastWriteFileTime, uint VolumeSerial, ulong FileIndex);

/// <summary>Pins candidate backing files. Only a later engine query supplies their loaded order.</summary>
public sealed record RuntimeGuestManifest(string Schema, int Version, string PreparedUtc,
    RuntimeBackingFile Emulator, RuntimeBackingFile Guest, IReadOnlyList<RuntimeBackingFile> Plugins)
{
    public static async Task<RuntimeGuestManifest> PrepareAsync(string emulator, string guest, string data,
        string output, CancellationToken token = default)
    {
        RequireWindows();
        if (File.Exists(output)) throw new IOException("Use a new guest manifest path.");
        var paths = Directory.EnumerateFiles(data, "*", SearchOption.TopDirectoryOnly)
            .Where(p => Path.GetExtension(p).Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetExtension(p).Equals(".esp", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length is < 1 or > 255) throw new IOException("Guest manifest requires 1 to 255 candidate plugins.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plugins = new List<RuntimeBackingFile>();
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (!names.Add(name)) throw new InvalidDataException("Candidate plugin names collide.");
            plugins.Add(await ReadBackingAsync(path, name, token));
        }
        var manifest = new RuntimeGuestManifest("bmt/runtime-guest-manifest", 1, DateTimeOffset.UtcNow.ToString("O"),
            await ReadBackingAsync(emulator, Path.GetFileName(emulator), token),
            await ReadBackingAsync(guest, Path.GetFileName(guest), token), plugins);
        await using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        await JsonSerializer.SerializeAsync(destination, manifest, RuntimeJsonContext.Default.RuntimeGuestManifest, token);
        return manifest;
    }

    public async Task ValidateAsync(JsonElement hello, CancellationToken token = default)
    {
        if (Schema != "bmt/runtime-guest-manifest" || Version != 1 || Emulator is null || Guest is null ||
            Plugins is null || Plugins.Count is < 1 or > 255 ||
            Plugins.Prepend(Guest).Prepend(Emulator).Any(p => !ValidFile(p)) ||
            Plugins.Any(p => Path.GetFileName(p.Name) != p.Name ||
                !(p.Name.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || p.Name.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))) ||
            Plugins.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Plugins.Count)
            throw new InvalidDataException("Unsupported or ambiguous guest manifest.");
        if (Text(hello, "backend") != "xenia-canary" ||
            !SamePath(Text(hello, "executablePath"), Emulator.Path) ||
            !SamePath(Text(hello, "guestExecutablePath"), Guest.Path) ||
            !string.Equals(Text(hello, "executableFileSha256"), Emulator.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Text(hello, "guestExecutableSha256"), Guest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Guest manifest does not match this emulator and guest.");
        foreach (var pinned in Plugins.Prepend(Guest).Prepend(Emulator))
            if (!SameBacking(pinned, await ReadBackingAsync(pinned.Path, pinned.Name, token)))
                throw new InvalidDataException($"Pinned backing file changed: {pinned.Name}");
    }

    private static bool ValidFile(RuntimeBackingFile? file) => file is not null &&
        !string.IsNullOrWhiteSpace(file.Name) && !string.IsNullOrWhiteSpace(file.Path) &&
        Path.IsPathFullyQualified(file.Path) && file.Length is >= 0 and <= 1024L * 1024 * 1024 &&
        file.Sha256 is { Length: 64 } hash && hash.All(char.IsAsciiHexDigit);

    internal static bool MatchesObservedBacking(RuntimeBackingFile file, JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("backingObservationLimitReached", out var limit) || limit.ValueKind != JsonValueKind.False ||
            !entry.TryGetProperty("backingFiles", out var candidates) || candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() != 1) return false;
        var observed = candidates[0];
        return observed.ValueKind == JsonValueKind.Object && Text(observed, "status") == "verified" &&
            Text(observed, "hashScope") == "successful-host-open-handle" &&
            observed.TryGetProperty("readOnlyDevice", out var readOnly) && readOnly.ValueKind == JsonValueKind.True &&
            SamePath(Text(observed, "path"), file.Path) &&
            string.Equals(Text(observed, "sha256"), file.Sha256, StringComparison.OrdinalIgnoreCase) &&
            Number(observed, "length", out var length) && length == (ulong)file.Length &&
            Number(observed, "lastWriteFileTime", out var write) && write == file.LastWriteFileTime &&
            Number(observed, "volumeSerial", out var volume) && volume == file.VolumeSerial &&
            Number(observed, "fileIndex", out var index) && index == file.FileIndex;
    }

    internal static async Task<RuntimeBackingFile> ReadBackingAsync(string path, string name, CancellationToken token)
    {
        RequireWindows();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var before = ReadIdentity(stream.SafeFileHandle);
        if (before.Length > 1024L * 1024 * 1024) throw new IOException("Backing file exceeds the 1 GiB hash limit.");
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
        var after = ReadIdentity(stream.SafeFileHandle);
        if (before != after || stream.Position != before.Length)
            throw new IOException("Backing file changed while hashing.");
        return before with { Name = name, Sha256 = hash };
    }

    private static RuntimeBackingFile ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var path = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new("", NormalizePath(path.ToString()), "", checked((long)(((ulong)info.SizeHigh << 32) | info.SizeLow)),
            ((ulong)info.WriteHigh << 32) | info.WriteLow, info.Volume,
            ((ulong)info.IndexHigh << 32) | info.IndexLow);
    }

    internal static bool SameBacking(RuntimeBackingFile expected, RuntimeBackingFile actual) =>
        SamePath(expected.Path, actual.Path) && expected.Name.Equals(actual.Name, StringComparison.OrdinalIgnoreCase) &&
        expected.Sha256.Equals(actual.Sha256, StringComparison.OrdinalIgnoreCase) && expected.Length == actual.Length &&
        expected.LastWriteFileTime == actual.LastWriteFileTime && expected.VolumeSerial == actual.VolumeSerial &&
        expected.FileIndex == actual.FileIndex;

    internal static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return NormalizePath(left).Equals(NormalizePath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    private static string NormalizePath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.GetFullPath(path);
    }
    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var item) &&
        item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static bool Number(JsonElement value, string name, out ulong result)
    {
        result = 0;
        return value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetUInt64(out result);
    }
    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Xbox backing-file identity requires Windows.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
}

using System.Security.Cryptography;
using System.Text;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>A separate virtual save directory avoids usvfs overlay fall-through to existing user saves.</summary>
internal static class RuntimeLiveSaveIsolation
{
    internal static readonly string[] IniFiles = ["Fallout.ini", "FalloutPrefs.ini", "FalloutCustom.ini"];

    internal static string DirectoryName(string profileRoot)
    {
        var identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileRoot)).ToUpperInvariant();
        return "BMT_Live_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
    }

    internal static string IniValue(string profileRoot) => DirectoryName(profileRoot) + "\\";

    internal static bool IsPrepared(RuntimeRunProfile profile)
    {
        var marker = Path.Combine(profile.Root, "live-config", "Fallout.ini");
        return profile.Files.Any(file => string.Equals(file.OriginalPath, marker, StringComparison.OrdinalIgnoreCase));
    }

    internal static (string Physical, string Logical) Resolve(RuntimeRunProfile profile)
    {
        var layout = profile.Isolation ?? throw new InvalidDataException("The live profile has no isolation layout.");
        var physical = Path.Combine(layout.DocumentsCopy, "Saves");
        var logical = Path.Combine(layout.DocumentsSource, DirectoryName(profile.Root));
        RuntimeIsolationService.RejectReparseAncestors(physical);
        RuntimeIsolationService.RejectReparseAncestors(logical);
        if (Path.Exists(logical))
            throw new IOException("The private virtual save path exists in the original documents directory; live launch refused: " + logical);
        return (physical, logical);
    }

    internal static async Task PrepareAsync(RuntimeRunProfile profile, CancellationToken token)
    {
        var (physical, _) = Resolve(profile);
        Directory.CreateDirectory(physical);
        // Older live profiles used Saves\\, an overlay of the user's real save
        // directory. Upgrade only private INIs; save data and originals stay put.
        foreach (var name in IniFiles)
        {
            var path = Path.Combine(profile.Isolation!.DocumentsCopy, name);
            RuntimeIsolationService.RejectReparseAncestors(path);
            if (!File.Exists(path)) continue;
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Live configuration exceeds 1 MiB.");
            var old = await File.ReadAllTextAsync(path, token);
            var next = RuntimeLiveConfiguration.SetValue(old, "General", "SLocalSavePath", IniValue(profile.Root));
            if (old != next) await RuntimeIsolationService.AtomicWrite(path, next, token);
        }
    }
}

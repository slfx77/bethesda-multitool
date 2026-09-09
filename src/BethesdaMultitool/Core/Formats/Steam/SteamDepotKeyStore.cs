using System.Globalization;
using System.Text.RegularExpressions;

namespace BethesdaMultitool.Core.Formats.Steam;

/// <summary>
///     Per-depot decryption keys for a Steam retail disc, loaded from a file the user supplies.
///     <para>
///         ⚠⚠ NO KEYS ARE EMBEDDED IN THIS REPOSITORY, deliberately. A depot key is Valve's, is
///         issued against ownership, and a bundled table would be someone else's harvested data
///         checked into our source. The reader therefore LISTS without any key — the <c>.sim</c>
///         manifest is plaintext — and only <see cref="SteamInstallerArchive.Extract" /> needs one.
///     </para>
///     <para>
///         ⚑ The key wanted is the LEGACY (Steam2-era) 16-BYTE depot key, not the 32-byte Steam3
///         key a modern client keeps in <c>config.vdf</c>. They are different generations: on a
///         sample where both were available, 43 depots appeared in both tables and ZERO of the 43
///         carried the same key. Feeding the Steam3 key produces a clean-looking failure — every
///         block decrypts to noise — so the size is worth checking before blaming the format.
///     </para>
///     <para>
///         Accepted file shapes, both parsed by the same pass:
///         <code>
///         "22381"  "5748d83ae06f7e2458013e50f3b837c4"   // legacydepotdata.vdf style
///         22381:5748d83ae06f7e2458013e50f3b837c4        // depot:key style
///         </code>
///         Lines that match neither are ignored, so a real <c>legacydepotdata.vdf</c> can be
///         pointed at directly without stripping its surrounding structure.
///     </para>
/// </summary>
internal sealed partial class SteamDepotKeyStore
{
    /// <summary>Length of a legacy depot key.</summary>
    internal const int KeyLength = 16;

    private readonly Dictionary<uint, byte[]> _keys;

    private SteamDepotKeyStore(Dictionary<uint, byte[]> keys)
    {
        _keys = keys;
    }

    /// <summary>An empty store — listing works, extraction does not.</summary>
    internal static SteamDepotKeyStore Empty { get; } = new([]);

    /// <summary>Depots this store can decrypt.</summary>
    internal IReadOnlyCollection<uint> Depots => _keys.Keys;

    /// <summary>File names probed beside a manifest, in order.</summary>
    internal static IReadOnlyList<string> ConventionalFileNames { get; } =
        ["legacydepotdata.vdf", "depot_keys.txt", "steam_depot_keys.txt"];

    /// <summary>Parses a key file. Unparseable lines are skipped, not fatal.</summary>
    internal static SteamDepotKeyStore Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var keys = new Dictionary<uint, byte[]>();
        foreach (var line in File.ReadLines(path))
        {
            var match = KeyLinePattern().Match(line);
            if (!match.Success)
            {
                continue;
            }

            if (!uint.TryParse(match.Groups["depot"].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var depot))
            {
                continue;
            }

            var hex = match.Groups["key"].Value;
            if (hex.Length != KeyLength * 2)
            {
                continue;
            }

            keys[depot] = Convert.FromHexString(hex);
        }

        return new SteamDepotKeyStore(keys);
    }

    /// <summary>
    ///     Environment variable naming a key file to use ahead of the beside-the-manifest probe.
    ///     ⚑ This is how a corpus generator hands the CLI a key file that lives OUTSIDE the build
    ///     tree: the ruling is that a build directory holds only the unpacked build, so a
    ///     <c>depot_keys.txt</c> next to the <c>.sim</c> would itself be contamination. The keys sit
    ///     in <c>Sample/Catalog/</c> instead and the generator sets this variable per invocation.
    /// </summary>
    internal const string EnvironmentVariable = "BETHESDA_STEAM_DEPOT_KEYS";

    /// <summary>
    ///     Loads keys from <see cref="EnvironmentVariable" /> when it names a readable file, else
    ///     the first conventional key file beside <paramref name="manifestPath" />, else
    ///     <see cref="Empty" />. Never throws for a missing file.
    /// </summary>
    internal static SteamDepotKeyStore LoadBeside(string manifestPath)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            try
            {
                return Load(fromEnvironment);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fall through to the beside-the-manifest probe.
            }
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
        if (directory is null)
        {
            return Empty;
        }

        foreach (var name in ConventionalFileNames)
        {
            var candidate = Path.Combine(directory, name);
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                return Load(candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable candidate must not mask the others.
            }
        }

        return Empty;
    }

    /// <summary>The 16-byte key for a depot, or null when the store has none.</summary>
    internal byte[]? Find(uint depotId)
    {
        return _keys.TryGetValue(depotId, out var key) ? key : null;
    }

    // "22381" "hex"  |  22381:hex  |  22381 = hex
    [GeneratedRegex("""^\s*"?(?<depot>\d{1,10})"?\s*[:=]?\s*"?(?<key>[0-9a-fA-F]{32})"?\s*$""")]
    private static partial Regex KeyLinePattern();
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace SampleGenerator;

/// <summary>
///     Reads Steam's own <c>appmanifest_*.acf</c> for an installed game.
///     <para>
///         A Steam install is not a dated release — it is whatever the depot last shipped, and it
///         changes under you. Skyrim Special Edition was patched on 2026-08-31; the four classic
///         re-releases were repackaged the same day. So a Steam build is identified by the depot's
///         <c>LastUpdated</c> and <c>buildid</c>, not by its executable's COFF timestamp, which
///         records when the exe was compiled and can be years older than the content beside it.
///     </para>
/// </summary>
internal static partial class SampleGeneratorSteam
{
    /// <summary>What Steam records about an installed game.</summary>
    /// <param name="LastUpdatedUtc">When the depot content last changed.</param>
    /// <param name="BuildId">The depot build the install is on.</param>
    internal readonly record struct DepotState(DateTime LastUpdatedUtc, string BuildId);

    /// <summary>
    ///     The depot state for an install directory, or null when no manifest names it.
    ///     <para>
    ///         ⚠ More than one manifest can point at the same install directory — this machine
    ///         carries both <c>Fallout New Vegas</c> (buildid 1510068, 2022-05-24) and
    ///         <c>fallout new vegas</c> (buildid 52174, 2026-01-19), which on Windows are the same
    ///         folder. The install's real age is the LATEST of them, so they are compared rather
    ///         than first-match-wins.
    ///     </para>
    /// </summary>
    internal static DepotState? ReadDepotState(string installDirectory, string? appId = null)
    {
        var steamApps = Path.GetDirectoryName(Path.GetDirectoryName(
            Path.TrimEndingDirectorySeparator(installDirectory)));
        if (steamApps is null || !Directory.Exists(steamApps))
        {
            return null;
        }

        var installName = Path.GetFileName(Path.TrimEndingDirectorySeparator(installDirectory));
        DepotState? newest = null;

        foreach (var manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
        {
            string text;
            try
            {
                text = File.ReadAllText(manifest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Prefer the appid: it identifies the app exactly, where installdir does not.
            if (appId is not null)
            {
                if (Field(text, "appid") != appId)
                {
                    continue;
                }
            }
            else
            {
                var installDir = Field(text, "installdir");
                if (installDir is null ||
                    !installDir.Equals(installName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            var updated = Field(text, "LastUpdated");
            if (updated is null ||
                !long.TryParse(updated, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ||
                seconds <= 0)
            {
                continue;
            }

            var when = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            if (newest is null || when > newest.Value.LastUpdatedUtc)
            {
                newest = new DepotState(when, Field(text, "buildid") ?? "?");
            }
        }

        return newest;
    }

    private static string? Field(string manifest, string key)
    {
        var match = Regex.Match(
            manifest,
            "\"" + Regex.Escape(key) + "\"\\s+\"([^\"]*)\"",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(5));
        return match.Success ? match.Groups[1].Value : null;
    }
}

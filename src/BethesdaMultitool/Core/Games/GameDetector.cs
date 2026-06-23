using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm;

namespace BethesdaMultitool.Core.Games;

/// <summary>
///     The one game-detection entry point. Combines the structural plugin probe (TES3/TES4 + HEDR
///     offset, via <see cref="PluginFormat.Detect" />) with master-list/filename refinement for the
///     24-byte TES4 family, whose HEDR version floats overlap between games. Returns a full
///     <see cref="GameProfile" /> so callers never re-derive game-specific data.
/// </summary>
public static class GameDetector
{
    private const int HeaderProbeBytes = 64 * 1024;

    /// <summary>
    ///     Detect the game from a plugin's leading bytes. <paramref name="fileName" /> (the plugin's own
    ///     filename, when known) participates in name refinement alongside the master list.
    /// </summary>
    public static GameProfile DetectFromBytes(ReadOnlySpan<byte> data, string? fileName = null)
    {
        // Framing + coarse game from the structural probe (this reads GameProfiles data, not us).
        var format = PluginFormat.Detect(data);

        // Morrowind and Oblivion are structurally unambiguous — no version/name overlap to resolve.
        if (format.Game is BethesdaGame.Morrowind or BethesdaGame.Oblivion)
        {
            return GameProfiles.For(format.Game);
        }

        // 24-byte TES4 family: refine by master list + filename, falling back to the structural guess.
        // ParseFileHeader re-runs the structural probe internally; that's fine (it never calls back here).
        var masters = EsmParser.ParseFileHeader(data)?.Masters ?? [];
        var refined = GameProfiles.ResolveByNames(masters.Append(fileName)) ?? format.Game;
        return GameProfiles.For(refined);
    }

    /// <summary>
    ///     Detect the game from a plugin file path, reading only its leading bytes. Returns the
    ///     <c>Unknown</c> profile on any I/O failure or for a missing/empty path.
    /// </summary>
    public static GameProfile DetectFromFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return GameProfiles.For(BethesdaGame.Unknown);
        }

        try
        {
            byte[] head;
            using (var fs = File.OpenRead(filePath))
            {
                var len = (int)Math.Min(HeaderProbeBytes, fs.Length);
                head = new byte[len];
                fs.ReadExactly(head, 0, len);
            }

            return DetectFromBytes(head, Path.GetFileName(filePath));
        }
        catch
        {
            return GameProfiles.For(BethesdaGame.Unknown);
        }
    }
}


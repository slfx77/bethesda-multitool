using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing;

/// <summary>
///     Binary layout descriptor for a plugin file's record/group framing — the convenient view the
///     record/group walker consumes. The framing values are a projection of the file's
///     <see cref="GameProfile" />: the TES4 record header is 24 bytes for FO3/FNV/Skyrim/FO4/Starfield
///     but only 20 for Oblivion (no VCS/version trailer), and 16 for Morrowind (TES3). Threading this
///     descriptor lets a single code path enumerate every TES4-family game; callers that don't supply
///     one default to <see cref="Fnv" /> (24-byte) so existing Fallout-only paths are unchanged.
/// </summary>
public readonly record struct PluginFormat(
    int RecordHeaderSize,
    int GroupHeaderSize,
    bool HasRecordVersionTrailer,
    BethesdaGame Game)
{
    /// <summary>The 24-byte TES4 layout shared by FO3/FNV/Skyrim/FO4/Starfield. Default for callers.</summary>
    public static readonly PluginFormat Fnv = FromProfile(GameProfiles.For(GameProfiles.DefaultGame));

    /// <summary>The 20-byte TES4 layout used by Oblivion (no VCS/version trailer).</summary>
    public static readonly PluginFormat Oblivion = FromProfile(GameProfiles.For(BethesdaGame.Oblivion));

    /// <summary>Project the framing fields of a <see cref="GameProfile" /> into a <see cref="PluginFormat" />.</summary>
    private static PluginFormat FromProfile(GameProfile profile) => new(
        profile.RecordHeaderSize,
        profile.GroupHeaderSize,
        profile.HasRecordVersionTrailer,
        profile.Game);

    /// <summary>
    ///     Detect the plugin framing from the file's leading bytes. Uses a STRUCTURAL probe — the
    ///     first subrecord of a TES4 file header is always "HEDR", so its position (offset 24 vs 20)
    ///     distinguishes the 24-byte and 20-byte layouts unambiguously. The 24-byte family's game
    ///     subtype is a coarse guess from the HEDR version float (see
    ///     <see cref="GameProfiles.ResolveByHedrVersion" />); for an authoritative game use
    ///     <see cref="GameDetector" />, which adds master-name refinement. Falls back to
    ///     <see cref="Fnv" /> when the input is too short or not a recognized TES4 header.
    /// </summary>
    public static PluginFormat Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6)
        {
            return Fnv;
        }

        var bigEndian = EsmParser.IsBigEndian(data);
        var magic = ReadSig(data, 0, bigEndian);

        // Morrowind (TES3) is a different format entirely — flag it for the caller, which routes to
        // the dedicated TES3 parser family.
        if (magic == "TES3")
        {
            return FromProfile(GameProfiles.For(BethesdaGame.Morrowind));
        }

        if (magic != "TES4")
        {
            return Fnv;
        }

        // "HEDR" at offset 24 ⇒ 24-byte header; at offset 20 ⇒ Oblivion's 20-byte header.
        if (HasHedrAt(data, 24, bigEndian))
        {
            var version = ReadHedrVersion(data, 24, bigEndian);
            var game = version is { } v ? GameProfiles.ResolveByHedrVersion(v) : GameProfiles.DefaultGame;
            return FromProfile(GameProfiles.For(game));
        }

        if (HasHedrAt(data, 20, bigEndian))
        {
            return Oblivion;
        }

        return Fnv;
    }

    private static bool HasHedrAt(ReadOnlySpan<byte> data, int offset, bool bigEndian)
        => data.Length >= offset + 4 && ReadSig(data, offset, bigEndian) == "HEDR";

    /// <summary>
    ///     Read the HEDR version float of a TES4 file. HEDR layout: signature(4) + dataLength(2) +
    ///     version(float), so the version sits at <paramref name="recordHeaderSize" /> + 6. Null when
    ///     the data is too short.
    /// </summary>
    private static float? ReadHedrVersion(ReadOnlySpan<byte> data, int recordHeaderSize, bool bigEndian)
    {
        var versionOffset = recordHeaderSize + 6;
        if (data.Length < versionOffset + 4)
        {
            return null;
        }

        return bigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(data.Slice(versionOffset, 4))
            : BinaryPrimitives.ReadSingleLittleEndian(data.Slice(versionOffset, 4));
    }

    private static string ReadSig(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        if (data.Length < offset + 4)
        {
            return string.Empty;
        }

        var slice = data.Slice(offset, 4);
        if (!bigEndian)
        {
            return Encoding.ASCII.GetString(slice);
        }

        Span<byte> reversed = stackalloc byte[4];
        reversed[0] = slice[3];
        reversed[1] = slice[2];
        reversed[2] = slice[1];
        reversed[3] = slice[0];
        return Encoding.ASCII.GetString(reversed);
    }
}

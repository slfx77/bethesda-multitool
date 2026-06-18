using System.Buffers.Binary;
using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Identifies the Bethesda game/engine a plugin file belongs to. Broader than
///     <see cref="FalloutGame" /> (which carries Fallout-specific gameplay semantics): this drives
///     the binary <see cref="PluginFormat" /> used while walking records and groups.
/// </summary>
public enum BethesdaGame
{
    Unknown = 0,

    /// <summary>Morrowind (TES3): flat records, 16-byte headers, no GRUPs, no FormIDs.</summary>
    Morrowind,

    /// <summary>Oblivion (TES4): 20-byte record + 20-byte GRUP headers (no VCS/version trailer).</summary>
    Oblivion,

    /// <summary>Fallout 3 (TES4): 24-byte headers.</summary>
    Fallout3,

    /// <summary>Fallout: New Vegas (TES4): 24-byte headers.</summary>
    FalloutNewVegas,

    /// <summary>Skyrim / Skyrim Special Edition (TES4): 24-byte headers.</summary>
    Skyrim,

    /// <summary>Fallout 4 (TES4): 24-byte headers.</summary>
    Fallout4,

    /// <summary>Fallout 76 (TES4): 24-byte headers; main master <c>SeventySix.esm</c>.</summary>
    Fallout76,

    /// <summary>Starfield (TES4): 24-byte headers.</summary>
    Starfield
}

/// <summary>
///     Binary layout descriptor for a plugin file's record/group framing. The TES4 record header is
///     24 bytes for Fallout 3 / New Vegas / Skyrim / Fallout 4 / Starfield, but only 20 bytes for
///     Oblivion (which omits the version-control / form-version trailer). The GRUP header follows the
///     same split (24 vs 20). Threading this descriptor through the parser lets a single code path
///     enumerate every TES4-family game; callers that don't supply one default to
///     <see cref="Fnv" /> (24-byte) so existing Fallout-only paths are unchanged.
/// </summary>
public readonly record struct PluginFormat(
    int RecordHeaderSize,
    int GroupHeaderSize,
    bool HasRecordVersionTrailer,
    BethesdaGame Game)
{
    /// <summary>The 24-byte TES4 layout shared by FO3/FNV/Skyrim/FO4/Starfield. Default for callers.</summary>
    public static readonly PluginFormat Fnv = new(24, 24, true, BethesdaGame.FalloutNewVegas);

    /// <summary>The 20-byte TES4 layout used by Oblivion (no VCS/version trailer).</summary>
    public static readonly PluginFormat Oblivion = new(20, 20, false, BethesdaGame.Oblivion);

    /// <summary>
    ///     Detect the plugin format from the file's leading bytes. Uses a STRUCTURAL probe — the
    ///     first subrecord of a TES4 file header is always "HEDR", so its position (offset 24 vs 20)
    ///     distinguishes the 24-byte and 20-byte layouts unambiguously. This avoids the HEDR
    ///     version-float overlap between games (e.g. Skyrim 0.94 ≈ FO3 0.94). Falls back to
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

        // Morrowind (TES3) is a different format entirely — flag it for the caller; record walking
        // for TES3 is not yet implemented (see V4 roadmap milestone 2).
        if (magic == "TES3")
        {
            return new PluginFormat(16, 0, false, BethesdaGame.Morrowind);
        }

        if (magic != "TES4")
        {
            return Fnv;
        }

        // "HEDR" at offset 24 ⇒ 24-byte header; at offset 20 ⇒ Oblivion's 20-byte header.
        if (HasHedrAt(data, 24, bigEndian))
        {
            return Fnv with { Game = DetectTes4Game(data, 24, bigEndian) };
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
    ///     Best-effort game subtype for a 24-byte TES4 file from the HEDR version float. Not
    ///     load-bearing for parsing (all 24-byte games share one framing); used only for display.
    /// </summary>
    private static BethesdaGame DetectTes4Game(ReadOnlySpan<byte> data, int recordHeaderSize, bool bigEndian)
    {
        // HEDR layout: signature(4) + dataLength(2) + version(float). Version sits at +6.
        var versionOffset = recordHeaderSize + 6;
        if (data.Length < versionOffset + 4)
        {
            return BethesdaGame.FalloutNewVegas;
        }

        var version = bigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(data.Slice(versionOffset, 4))
            : BinaryPrimitives.ReadSingleLittleEndian(data.Slice(versionOffset, 4));

        // Ranges overlap across games (Skyrim 0.94 == FO3 0.94), so this is a coarse best guess.
        // Fallout 76 is the exception: its HEDR version is an order of magnitude higher (e.g. 263.0),
        // so anything >= 2.0 is unambiguously FO76 (no other TES4 game exceeds Skyrim SE's 1.7).
        return version switch
        {
            >= 2.0f => BethesdaGame.Fallout76,
            >= 1.30f => BethesdaGame.FalloutNewVegas,
            >= 0.955f => BethesdaGame.Starfield,
            >= 0.945f => BethesdaGame.Fallout4,
            >= 0.93f => BethesdaGame.Fallout3,
            _ => BethesdaGame.FalloutNewVegas
        };
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

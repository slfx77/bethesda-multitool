using System.Buffers.Binary;
using BethesdaMultitool.Core.Utils;
using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.Formats.Esm.Localization;

/// <summary>
///     The three external string tables (<c>.STRINGS</c> / <c>.DLSTRINGS</c> / <c>.ILSTRINGS</c>)
///     for a <b>localized</b> plugin (TES5+ with the TES4 header "Localized" flag, 0x80, set).
///     In a localized plugin, lstring subrecords (FULL, DESC, dialogue responses, …) store a
///     4-byte string ID instead of inline text; this class maps those IDs back to text.
///     <para>
///         Vanilla Skyrim SE ships the tables <i>inside</i> <c>Skyrim - Interface.bsa</c> (under
///         <c>strings\</c>), not as loose files; Fallout 76 ships them inside a <c>.ba2</c>
///         (<c>SeventySix - Localization.ba2</c>). So the loader checks loose <c>Data\Strings</c>
///         first, then the Data folder's BSAs, then its BA2s. The table byte layout is identical
///         across Skyrim/FO4/FO76, so only the container differs.
///     </para>
/// </summary>
public sealed class LocalizedStringTables
{
    private const uint LocalizedFlag = 0x80;

    private readonly IReadOnlyDictionary<uint, string> _strings;
    private readonly IReadOnlyDictionary<uint, string> _dlStrings;
    private readonly IReadOnlyDictionary<uint, string> _ilStrings;

    private LocalizedStringTables(
        IReadOnlyDictionary<uint, string> strings,
        IReadOnlyDictionary<uint, string> dlStrings,
        IReadOnlyDictionary<uint, string> ilStrings)
    {
        _strings = strings;
        _dlStrings = dlStrings;
        _ilStrings = ilStrings;
    }

    /// <summary>
    ///     Load the string tables for <paramref name="esmPath" /> if it is a localized plugin and at
    ///     least one table can be found. Returns <c>null</c> for non-localized plugins (FNV/FO3/
    ///     Oblivion, every Xbox 360 ESM) so callers fall back to reading inline zstrings unchanged.
    /// </summary>
    public static LocalizedStringTables? TryLoad(string esmPath, string language = "English")
    {
        if (!IsLocalizedPlugin(esmPath))
        {
            return null;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(esmPath));
        if (dir == null)
        {
            return null;
        }

        var baseName = Path.GetFileNameWithoutExtension(esmPath);

        // One lazy Data-folder mount serves all table lookups with the exact precedence
        // the hand-rolled loose→BSA→BA2 chain implemented: loose Data\Strings first (zero archive
        // opens on a hit), then *.bsa alphabetical, then *.ba2, first hit wins, per-source failures
        // falling through. Alternate language suffixes are resolved within each layer, so a later
        // archive's preferred spelling cannot beat an earlier layer's fallback spelling. Lazy
        // layers keep each table lookup's early-exit (archives past its first suffix hit are not
        // touched by that lookup), and
        // one mounted folder reuses each opened layer across its table lookups. Disposing this mount
        // releases its leases; cross-plugin reuse requires a deliberately bounded warm/cache scope.
        using var dataFolder = GameFileSystem.OpenDataFolder(dir, registry: ArchiveHandleRegistry.Shared);
        var strings = LoadTable(dataFolder, baseName, language, "STRINGS", lengthPrefixed: false);
        var dlStrings = LoadTable(dataFolder, baseName, language, "DLSTRINGS", lengthPrefixed: true);
        var ilStrings = LoadTable(dataFolder, baseName, language, "ILSTRINGS", lengthPrefixed: true);

        if (strings.Count == 0 && dlStrings.Count == 0 && ilStrings.Count == 0)
        {
            // Localized plugin but no tables present — leave resolution to the zstring fallback
            // rather than blanking every name.
            return null;
        }

        return new LocalizedStringTables(strings, dlStrings, ilStrings);
    }

    /// <summary>
    ///     Resolve a string ID against the requested table. Returns <c>null</c> when the ID is
    ///     unknown (caller decides the fallback); ID 0 is Bethesda's "no string" and maps to empty.
    /// </summary>
    public string? Resolve(uint stringId, LStringKind kind)
    {
        if (stringId == 0)
        {
            return string.Empty;
        }

        var table = kind switch
        {
            LStringKind.DlStrings => _dlStrings,
            LStringKind.IlStrings => _ilStrings,
            _ => _strings
        };
        return table.TryGetValue(stringId, out var value) ? value : null;
    }

    private static bool IsLocalizedPlugin(string esmPath)
    {
        try
        {
            using var fs = new FileStream(esmPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[12];
            if (fs.Read(header) < 12)
            {
                return false;
            }

            // TES4/TES5 file header record: type[4] "TES4", dataSize[4], flags[4]. The flags are
            // little-endian on every localized (PC) build. Bit 0x80 = "Localized".
            if (header[0] != (byte)'T' || header[1] != (byte)'E' || header[2] != (byte)'S' || header[3] != (byte)'4')
            {
                return false;
            }

            var flags = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
            return (flags & LocalizedFlag) != 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Maps a full language name to its 2-letter file-suffix code. Skyrim names its tables with the
    ///     full word (<c>Skyrim_English.STRINGS</c>); Fallout 4 / Fallout 76 use the 2-letter code
    ///     (<c>Fallout4_en.STRINGS</c>, <c>seventysix_en.strings</c>). The loader tries both.
    /// </summary>
    private static readonly Dictionary<string, string> TwoLetterLanguageCodes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["English"] = "en",
            ["German"] = "de",
            ["French"] = "fr",
            ["Spanish"] = "es",
            ["Italian"] = "it",
            ["Polish"] = "pl",
            ["Russian"] = "ru",
            ["Portuguese"] = "pt",
            ["Czech"] = "cs",
            ["Japanese"] = "ja",
            ["Chinese"] = "zh"
        };

    private static Dictionary<uint, string> LoadTable(
        LayeredGameFileSystem dataFolder, string baseName, string language, string extension, bool lengthPrefixed)
    {
        // Within each loose/archive layer, prefer the full Skyrim language name and then the
        // FO4/FO76 two-letter code. Evaluating the pair layer-by-layer preserves source precedence
        // and avoids opening every archive for a guaranteed full-name miss before trying the code.
        var candidatePaths = LanguageTokens(language)
            .Select(token => $"Strings\\{baseName}_{token}.{extension}")
            .ToArray();
        var bytes = dataFolder.TryReadFirstAvailable(candidatePaths);
        return bytes == null
            ? new Dictionary<uint, string>()
            : ParseTable(bytes, lengthPrefixed);
    }

    private static IEnumerable<string> LanguageTokens(string language)
    {
        yield return language;
        if (TwoLetterLanguageCodes.TryGetValue(language, out var code) &&
            !string.Equals(code, language, StringComparison.OrdinalIgnoreCase))
        {
            yield return code;
        }
    }

    /// <summary>
    ///     Parse a Bethesda string table. Layout: <c>count</c> (u32), <c>dataSize</c> (u32), a
    ///     directory of <c>count</c> × { id (u32), offset (u32) }, then the data section. For
    ///     <c>.STRINGS</c> each entry is a null-terminated string at its offset; for
    ///     <c>.DL/.ILSTRINGS</c> each entry is a u32 length (incl. terminator) followed by the bytes.
    /// </summary>
    internal static Dictionary<uint, string> ParseTable(byte[] data, bool lengthPrefixed)
    {
        var result = new Dictionary<uint, string>();
        if (data.Length < 8)
        {
            return result;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
        var directoryBytes = checked((long)count * 8);
        var dataStart = 8 + directoryBytes;
        if (dataStart > data.Length)
        {
            return result;
        }

        for (var i = 0; i < count; i++)
        {
            var entry = 8 + i * 8;
            var stringId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry, 4));
            var relOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 4, 4));
            var start = dataStart + relOffset;
            if (start < dataStart || start >= data.Length)
            {
                continue;
            }

            var value = lengthPrefixed
                ? ReadLengthPrefixed(data, (int)start)
                : ReadNullTerminated(data, (int)start);
            if (value != null)
            {
                result[stringId] = value;
            }
        }

        return result;
    }

    private static string? ReadNullTerminated(byte[] data, int start)
    {
        var end = start;
        while (end < data.Length && data[end] != 0)
        {
            end++;
        }

        return EsmStringUtils.DecodeGameText(data.AsSpan(start, end - start));
    }

    private static string? ReadLengthPrefixed(byte[] data, int start)
    {
        if (start + 4 > data.Length)
        {
            return null;
        }

        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start, 4));
        if (length <= 0 || start + 4 + length > data.Length)
        {
            return null;
        }

        // Length includes the trailing null terminator.
        var textLength = data[start + 4 + length - 1] == 0 ? length - 1 : length;
        return EsmStringUtils.DecodeGameText(data.AsSpan(start + 4, textLength));
    }
}

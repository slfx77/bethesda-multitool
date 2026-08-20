using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Localization;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Bsa;

/// <summary>
///     Verifies the Fallout 76 string-table path: a localized plugin whose <c>.STRINGS</c> live inside
///     a <c>.ba2</c> (not loose, not a BSA) resolves lstring IDs to text — the fix that unblocks FO76
///     worldspace FULL names. The table byte layout is the shared Bethesda format, so only the BA2
///     container is exercised here.
/// </summary>
public class LocalizedStringTablesBa2Tests
{
    [Fact]
    public void TryLoad_StringsTableInsideBa2_ResolvesLString()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ba2strings_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // Localized plugin stub: TES4 header with the 0x80 "Localized" flag set.
            var esm = Path.Combine(dir, "MyMod.esm");
            File.WriteAllBytes(esm, BuildLocalizedTes4Stub());

            // .STRINGS table with id 1 -> "Appalachia", packed into a BA2 under strings\.
            var table = BuildStringsTable(1u, "Appalachia");
            var ba2 = Path.Combine(dir, "SeventySix - Localization.ba2");
            File.WriteAllBytes(ba2, BuildGnrlBa2("strings\\MyMod_English.STRINGS", table));

            var tables = LocalizedStringTables.TryLoad(esm);

            Assert.NotNull(tables);
            Assert.Equal("Appalachia", tables!.Resolve(1u, LStringKind.Strings));
            Assert.Null(tables.Resolve(999u, LStringKind.Strings)); // unknown id
            Assert.Equal(string.Empty, tables.Resolve(0u, LStringKind.Strings)); // Bethesda "no string"
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TryLoad_TwoLetterLanguageCodeInBa2_ResolvesLString()
    {
        // FO4/FO76 name string tables with the 2-letter code (mymod_en.strings), NOT the Skyrim-style
        // full name (mymod_English.strings). The loader must fall back to the code form.
        var dir = Path.Combine(Path.GetTempPath(), $"ba2strcode_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var esm = Path.Combine(dir, "MyMod.esm");
            File.WriteAllBytes(esm, BuildLocalizedTes4Stub());

            var table = BuildStringsTable(7u, "Appalachia");
            var ba2 = Path.Combine(dir, "SeventySix - Localization.ba2");
            File.WriteAllBytes(ba2, BuildGnrlBa2("strings\\mymod_en.strings", table));

            var tables = LocalizedStringTables.TryLoad(esm); // default language "English" -> also tries "en"

            Assert.NotNull(tables);
            Assert.Equal("Appalachia", tables!.Resolve(7u, LStringKind.Strings));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TryLoad_EarlierArchiveFallbackSuffixBeatsLaterArchivePreferredSuffix()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ba2strlayers_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var esm = Path.Combine(dir, "MyMod.esm");
            File.WriteAllBytes(esm, BuildLocalizedTes4Stub());

            File.WriteAllBytes(
                Path.Combine(dir, "bbb.ba2"),
                BuildGnrlBa2("strings\\mymod_en.strings", BuildStringsTable(8u, "Earlier fallback")));
            File.WriteAllBytes(
                Path.Combine(dir, "zzz.ba2"),
                BuildGnrlBa2("strings\\mymod_English.strings", BuildStringsTable(8u, "Later preferred")));

            var tables = LocalizedStringTables.TryLoad(esm);

            Assert.NotNull(tables);
            Assert.Equal("Earlier fallback", tables!.Resolve(8u, LStringKind.Strings));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static byte[] BuildLocalizedTes4Stub()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("TES4"u8.ToArray());
        bw.Write(0u); // dataSize (unused by the localized-flag check)
        bw.Write(0x80u); // flags: Localized
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    ///     Builds a non-length-prefixed (.STRINGS) table: count, dataSize, a directory of
    ///     { id, relOffset }, then the null-terminated string data.
    /// </summary>
    private static byte[] BuildStringsTable(uint id, string text)
    {
        var textBytes = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(1u); // count
        bw.Write((uint)(textBytes.Length + 1)); // dataSize (informational)
        bw.Write(id); // entry id
        bw.Write(0u); // entry relative offset
        bw.Write(textBytes); // string data
        bw.Write((byte)0); // null terminator
        bw.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildGnrlBa2(string name, byte[] data)
    {
        const int headerSize = 24;
        const int recordSize = 36;
        var dataOffset = (ulong)(headerSize + recordSize);
        var nameTableOffset = dataOffset + (ulong)data.Length;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, true);
        bw.Write("BTDX"u8.ToArray());
        bw.Write(1u);
        bw.Write("GNRL"u8.ToArray());
        bw.Write(1u);
        bw.Write(nameTableOffset);

        bw.Write(0x1234u); // nameHash
        var ext = new byte[4];
        Encoding.ASCII.GetBytes("STR").CopyTo(ext, 0);
        bw.Write(ext);
        bw.Write(0x5678u); // dirHash
        bw.Write(0u); // flags
        bw.Write(dataOffset);
        bw.Write(0u); // packedSize 0 == uncompressed
        bw.Write((uint)data.Length); // realSize
        bw.Write(0u); // align

        bw.Write(data);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        bw.Write((ushort)nameBytes.Length);
        bw.Write(nameBytes);
        bw.Flush();
        return ms.ToArray();
    }
}
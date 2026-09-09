// The slot layout is ported from daggerfall-unity's DaggerfallConnect save readers (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/Save/SaveGames.cs (the
//   SAVE? directory pattern, the rule that a slot is recognised by SAVETREE.DAT rather than by the
//   directory existing, and the SAVETREE.DAT / SAVEVARS.DAT / SAVENAME.TXT / BIO.DAT / RUMOR.DAT
//   file names) and Assets/Scripts/API/Save/SaveImage.cs (IMAGE.RAW is a headerless 80 x 50 image
//   whose palette is ART_PAL.COL, loaded separately). License texts are collected centrally in
//   THIRD_PARTY_LICENSES.
//
// OUR OWN measurement, not in the reference (retail SAVE0, 2026-09-07):
//   * DaggerfallSaveAutomapFile — the AT<LocationId>.AMF files (u32 stamp + 10,240 bytes, equal to
//     the tree's type-0x33 record's first 10,240 bytes except root bytes 39..41 and 67..69).
//   * BIO.DAT's line rule (NUL-separated, terminated by an EMPTY line; the file's last byte is a
//     NUL, so a naive split leaves two trailing empties) — the reference's BioFile is not staged
//     here and was not consulted.
//   * That ART_PAL.COL must be read as FULL-RANGE 8-bit for IMAGE.RAW, and the 80-pixel stride,
//     both settled by rendering (see the class doc below).

using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     One <c>AT&lt;LocationId&gt;.AMF</c> beside a save: the dungeon automap the player has
///     uncovered. A u32 game-time stamp then 10,240 payload bytes.
///     <para>
///         ⚑ The payload is the SAME IMAGE as the tree's DungeonAutomap (type 0x33) record: on
///         SAVE0 <c>AT50050.AMF</c>'s bytes 4..10,243 equal that record's first 10,240 bytes
///         (root included) at 10,234 of 10,240 positions, the six exceptions being the root's
///         parent-id bytes 39..41 and parent-type bytes 67..69, which the standalone file zeroes.
///         The stamp equals SAVEVARS <c>gameTime</c> — one sample, so it is exposed as a raw u32.
///     </para>
/// </summary>
internal sealed class DaggerfallSaveAutomapFile
{
    /// <summary>Payload bytes after the stamp.</summary>
    public const int PayloadLength = 10_240;

    /// <summary>The whole file: stamp plus payload.</summary>
    public const int FileLength = 4 + PayloadLength;

    private DaggerfallSaveAutomapFile(string name, uint timeStamp, ReadOnlyMemory<byte> payload)
    {
        Name = name;
        TimeStamp = timeStamp;
        Payload = payload;
    }

    /// <summary>The file name, e.g. <c>AT50050.AMF</c>.</summary>
    public string Name { get; }

    /// <summary>The dungeon's LocationId, parsed out of the file name (50,050 for AT50050.AMF).</summary>
    public int LocationId =>
        int.TryParse(Path.GetFileNameWithoutExtension(Name).AsSpan(2), out var id) ? id : -1;

    /// <summary>The leading u32 — a game-time stamp on the one measured sample.</summary>
    public uint TimeStamp { get; }

    /// <summary>The 10,240 automap bytes.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>Parses a complete image; throws <see cref="InvalidDataException" /> on any other length.</summary>
    public static DaggerfallSaveAutomapFile Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);
        if (bytes.Length != FileLength)
        {
            throw new InvalidDataException(
                $"{name}: an AMF is {FileLength} bytes (u32 + {PayloadLength}); this one is {bytes.Length}.");
        }

        return new DaggerfallSaveAutomapFile(
            name,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            new ReadOnlyMemory<byte>(bytes, 4, PayloadLength));
    }
}

/// <summary>
///     A Daggerfall save slot: one of the <c>SAVE0</c>..<c>SAVE5</c> directories beside
///     <c>ARENA2</c>, holding <c>SAVETREE.DAT</c>, <c>SAVEVARS.DAT</c>, <c>SAVENAME.TXT</c>,
///     <c>BIO.DAT</c>, <c>RUMOR.DAT</c>, <c>IMAGE.RAW</c>, <c>MAPSAVE.SAV</c> and one
///     <c>AT&lt;LocationId&gt;.AMF</c> per visited dungeon.
///     <para>
///         ⚠ Steam ships all six directories whether or not they hold a save — an unused one has
///         only <c>steam_autocloud.vdf</c> and <c>temp.txt</c> — so a slot is recognised by
///         SAVETREE.DAT, never by the directory existing.
///     </para>
///     <para>
///         <c>SAVENAME.TXT</c> is 32 bytes: the name the player typed, NUL-terminated and
///         NUL-padded ("Hans" on the fixture, which is also the character's name in the tree —
///         they are separate fields that happen to agree).
///     </para>
///     <para>
///         <c>IMAGE.RAW</c> is 4,000 bytes = 80 x 50 8-bit indices with no header, lit by
///         <c>ARENA2\ART_PAL.COL</c> — the same palette the game's art uses, resolved from ARENA2
///         beside the slot's parent. Rendered, SAVE0's is a first-person dungeon view with the
///         classic HUD (portrait, condition bars, the spell/weapon buttons) along the bottom, which
///         is what settles both the 80-pixel stride and the palette: at any other stride the HUD
///         shears, and through a 6-bit-promoted palette it comes out four times too dark.
///     </para>
///     <para>
///         <c>BIO.DAT</c> is NUL-separated ASCII: 72 lines of the character's back story on SAVE0,
///         then an empty line that terminates the list (the file's last byte is a NUL, so a naive
///         split yields two trailing empties).
///     </para>
/// </summary>
internal sealed class DaggerfallSaveSlot
{
    public const string TreeFileName = DaggerfallSaveTree.FileName;
    public const string VarsFileName = DaggerfallSaveVars.FileName;
    public const string RumorFileName = DaggerfallSaveRumorFile.FileName;
    public const string NameFileName = "SAVENAME.TXT";
    public const string BiographyFileName = "BIO.DAT";
    public const string ImageFileName = "IMAGE.RAW";
    public const string MapSaveFileName = "MAPSAVE.SAV";

    /// <summary>Bytes in SAVENAME.TXT.</summary>
    public const int SaveNameLength = 32;

    /// <summary>Thumbnail width in pixels.</summary>
    public const int ImageWidth = 80;

    /// <summary>Thumbnail height in pixels.</summary>
    public const int ImageHeight = 50;

    /// <summary>Bytes in IMAGE.RAW: one index per pixel, no header.</summary>
    public const int ImageLength = ImageWidth * ImageHeight;

    /// <summary>The palette file, in ARENA2.</summary>
    public const string PaletteFileName = "ART_PAL.COL";

    /// <summary>The data directory the palette lives in, beside the slot's parent.</summary>
    public const string DataDirectoryName = "ARENA2";

    private DaggerfallSaveSlot(
        string directory,
        DaggerfallSaveTree tree,
        DaggerfallSaveVars? vars,
        DaggerfallSaveRumorFile? rumors,
        string? saveName,
        IReadOnlyList<string> biographyLines,
        IndexedBitmap? image,
        IReadOnlyList<DaggerfallSaveAutomapFile> automaps)
    {
        Directory = directory;
        Tree = tree;
        Vars = vars;
        RumorFile = rumors;
        SaveName = saveName;
        BiographyLines = biographyLines;
        Image = image;
        Automaps = automaps;
    }

    /// <summary>The slot directory.</summary>
    public string Directory { get; }

    /// <summary>The object tree — the only required file.</summary>
    public DaggerfallSaveTree Tree { get; }

    /// <summary>Null when SAVEVARS.DAT is absent.</summary>
    public DaggerfallSaveVars? Vars { get; }

    /// <summary>Null when RUMOR.DAT is absent.</summary>
    public DaggerfallSaveRumorFile? RumorFile { get; }

    /// <summary>The name the player gave the save; null when SAVENAME.TXT is absent.</summary>
    public string? SaveName { get; }

    /// <summary>BIO.DAT's lines, the terminating empty line excluded; empty when the file is absent.</summary>
    public IReadOnlyList<string> BiographyLines { get; }

    /// <summary>The 80 x 50 thumbnail as palette indices; null when IMAGE.RAW is absent.</summary>
    public IndexedBitmap? Image { get; }

    /// <summary>The AT*.AMF dungeon automaps in the slot, in file-name order.</summary>
    public IReadOnlyList<DaggerfallSaveAutomapFile> Automaps { get; }

    /// <summary>True when the directory holds a SAVETREE.DAT.</summary>
    public static bool IsSaveSlot(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return File.Exists(Path.Combine(directory, TreeFileName));
    }

    /// <summary>
    ///     Loads the slot. SAVETREE.DAT is required (a <see cref="FileNotFoundException" />
    ///     otherwise); every other file is optional. Any file that is present but does not tile
    ///     throws <see cref="InvalidDataException" /> — a partial save is a fault, not a shrug.
    /// </summary>
    public static DaggerfallSaveSlot Load(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var treePath = Path.Combine(directory, TreeFileName);
        if (!File.Exists(treePath))
        {
            throw new FileNotFoundException($"{directory} holds no {TreeFileName}; it is not a Daggerfall save slot.",
                treePath);
        }

        var tree = DaggerfallSaveTree.Parse(File.ReadAllBytes(treePath), TreeFileName);

        var varsPath = Path.Combine(directory, VarsFileName);
        var vars = File.Exists(varsPath) ? DaggerfallSaveVars.Parse(File.ReadAllBytes(varsPath), VarsFileName) : null;

        var rumorPath = Path.Combine(directory, RumorFileName);
        var rumors = File.Exists(rumorPath)
            ? DaggerfallSaveRumorFile.Parse(File.ReadAllBytes(rumorPath), RumorFileName)
            : null;

        var namePath = Path.Combine(directory, NameFileName);
        var saveName = File.Exists(namePath) ? ReadSaveName(File.ReadAllBytes(namePath), NameFileName) : null;

        var bioPath = Path.Combine(directory, BiographyFileName);
        var biography = File.Exists(bioPath) ? ReadBiography(File.ReadAllBytes(bioPath)) : [];

        var imagePath = Path.Combine(directory, ImageFileName);
        var image = File.Exists(imagePath) ? ReadImage(File.ReadAllBytes(imagePath), ImageFileName) : null;

        var automaps = new List<DaggerfallSaveAutomapFile>();
        foreach (var path in System.IO.Directory.EnumerateFiles(directory, "AT*.AMF")
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            automaps.Add(DaggerfallSaveAutomapFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path)));
        }

        return new DaggerfallSaveSlot(directory, tree, vars, rumors, saveName, biography, image, automaps);
    }

    /// <summary>Reads SAVENAME.TXT: exactly 32 bytes, the name up to its first NUL.</summary>
    public static string ReadSaveName(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (bytes.Length != SaveNameLength)
        {
            throw new InvalidDataException(
                $"{name}: {NameFileName} is {SaveNameLength} bytes, this one is {bytes.Length}.");
        }

        var end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]);
    }

    /// <summary>
    ///     Splits BIO.DAT on NULs and stops at the first empty line, which terminates the list.
    ///     ⚠ The file's last byte is a NUL, so an unguarded split leaves two empty entries; both
    ///     are dropped here, and any prose after a mid-file empty line would be dropped too — none
    ///     exists on the fixture (the only empties are the last two).
    /// </summary>
    public static IReadOnlyList<string> ReadBiography(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<string>();
        var position = 0;
        while (position < bytes.Length)
        {
            var rest = bytes[position..];
            var end = rest.IndexOf((byte)0);
            var line = Encoding.Latin1.GetString(end < 0 ? rest : rest[..end]);
            if (line.Length == 0)
            {
                break;
            }

            lines.Add(line);
            if (end < 0)
            {
                break;
            }

            position += end + 1;
        }

        return lines;
    }

    /// <summary>Reads IMAGE.RAW: exactly 4,000 headerless 8-bit indices, 80 per row.</summary>
    public static IndexedBitmap ReadImage(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);
        if (bytes.Length != ImageLength)
        {
            throw new InvalidDataException(
                $"{name}: {ImageFileName} is {ImageLength} bytes ({ImageWidth} x {ImageHeight} indices); this one is {bytes.Length}.");
        }

        return new IndexedBitmap(ImageWidth, ImageHeight, bytes);
    }

    /// <summary>
    ///     Loads <c>ART_PAL.COL</c> from the ARENA2 directory beside the slot's parent (a slot sits
    ///     at <c>DAGGER\SAVEn</c> and the data at <c>DAGGER\ARENA2</c>). Null when it is not there,
    ///     so a slot copied away from its install still loads — without a palette.
    /// </summary>
    public Palette? TryLoadPalette()
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Directory));
        if (parent is null)
        {
            return null;
        }

        var path = Path.Combine(parent, DataDirectoryName, PaletteFileName);
        return File.Exists(path) ? Palette.LoadDaggerfallCol(File.ReadAllBytes(path)) : null;
    }

    /// <summary>Encodes the thumbnail as a PNG through <paramref name="palette" />.</summary>
    public byte[] EncodeImagePng(Palette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (Image is null)
        {
            throw new InvalidOperationException($"{Directory} holds no {ImageFileName}.");
        }

        var texture = Image.ToDecodedTexture(palette);
        return PngWriter.EncodeRgba(texture.Pixels, texture.Width, texture.Height);
    }

    /// <summary>Writes the thumbnail as a PNG to <paramref name="path" />.</summary>
    public void SaveImagePng(string path, Palette palette)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllBytes(path, EncodeImagePng(palette));
    }
}

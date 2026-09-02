using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.CLI.Rendering.Sprite;

/// <summary>
///     The classic-game 2D pipeline behind the <c>sprite</c> command group — the sibling of the NIF
///     render pipeline for palettized DOS-era art. Loads a sprite/image from a loose file or an
///     archive entry, decodes it to <see cref="IndexedBitmap" /> frames, resolves a palette
///     (embedded &gt; explicit <c>--palette</c> &gt; a <c>PAL.COL</c> beside the source), and writes
///     one PNG per frame/tile. Arena formats today (IMG/MNU/SET/CIF/DFA); the other classic families
///     (FRM, ZAR, TIL, SPR, TEXTURE.nnn, BSI) join per game vertical.
/// </summary>
internal static class SpriteRenderPipeline
{
    internal sealed record RenderedFrame(string Path, int Width, int Height, int XOffset, int YOffset);

    internal sealed record RenderResult(IReadOnlyList<RenderedFrame> Frames, string PaletteSource);

    /// <summary>
    ///     Decodes <paramref name="input" /> (or <paramref name="entryName" /> inside it when the
    ///     input is an archive) and writes PNG frames into <paramref name="outputDir" />.
    /// </summary>
    public static RenderResult Render(string input, string? entryName, string outputDir, string? palettePath)
    {
        var (bytes, logicalName, sourceDir) = LoadSource(input, entryName);
        var frames = DecodeFrames(bytes, logicalName, out var embeddedPalette, out var labels);

        var (palette, paletteSource) = ResolvePalette(embeddedPalette, palettePath, sourceDir);
        if (TransparentZeroByDefault(logicalName))
        {
            palette = palette.WithTransparentIndex(0);
        }

        Directory.CreateDirectory(outputDir);

        // Daggerfall texture sets are all named TEXTURE.nnn — the digits ARE the identity, so
        // stripping them as an "extension" would collide every set onto one base name.
        var baseName = DaggerfallTextureFile.IsTextureFileName(logicalName)
            ? logicalName.Replace('.', '_')
            : Path.GetFileNameWithoutExtension(logicalName);
        if (string.IsNullOrEmpty(baseName))
        {
            baseName = logicalName.Replace('.', '_');
        }

        var written = new List<RenderedFrame>(frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            var label = labels is not null ? $"_{labels[i]}" : frames.Count == 1 ? string.Empty : $"_f{i:D2}";
            var path = Path.Combine(outputDir, $"{baseName}{label}.png");
            var texture = frame.ToDecodedTexture(palette);
            PngWriter.SaveRgba(texture.Pixels, texture.Width, texture.Height, path);
            written.Add(new RenderedFrame(path, frame.Width, frame.Height, frame.XOffset, frame.YOffset));
        }

        return new RenderResult(written, paletteSource);
    }

    /// <summary>Decodes without writing — the <c>sprite info</c> half.</summary>
    public static IReadOnlyList<IndexedBitmap> Inspect(string input, string? entryName, out string logicalName)
    {
        var (bytes, name, _) = LoadSource(input, entryName);
        logicalName = name;
        return DecodeFrames(bytes, name, out _, out _);
    }

    private static (byte[] Bytes, string LogicalName, string SourceDir) LoadSource(string input, string? entryName)
    {
        if (!File.Exists(input))
        {
            throw new FileNotFoundException($"Input not found: {input}", input);
        }

        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".";
        if (entryName is null)
        {
            return (File.ReadAllBytes(input), Path.GetFileName(input), sourceDir);
        }

        using var reader = ArchiveReader.Open(input);
        var bytes = reader.ReadFile(entryName) ??
                    throw new FileNotFoundException(
                        $"Entry '{entryName}' not found in {Path.GetFileName(input)} ({reader.FormatName}, {reader.TotalFiles} files).");
        return (bytes, Path.GetFileName(entryName.Replace('/', '\\')), sourceDir);
    }

    private static IReadOnlyList<IndexedBitmap> DecodeFrames(
        byte[] bytes,
        string logicalName,
        out Palette? embeddedPalette,
        out IReadOnlyList<string>? frameLabels)
    {
        embeddedPalette = null;
        frameLabels = null;

        // Daggerfall's TEXTURE.nnn sets are named by convention, not extension, and hold multiple
        // records each with frames — so their outputs are labelled rNN_fMM instead of numbered.
        if (DaggerfallTextureFile.IsTextureFileName(logicalName))
        {
            var texture = DaggerfallTextureFile.Parse(bytes, logicalName);
            var images = new List<IndexedBitmap>();
            var labels = new List<string>();
            foreach (var record in texture.Records)
            {
                for (var f = 0; f < record.Frames.Count; f++)
                {
                    images.Add(record.Frames[f]);
                    labels.Add(record.Frames.Count == 1 ? $"r{record.Index:D2}" : $"r{record.Index:D2}_f{f:D2}");
                }
            }

            frameLabels = labels;
            return images;
        }

        var extension = Path.GetExtension(logicalName).ToLowerInvariant();
        switch (extension)
        {
            case ".img":
            case ".mnu":
            case ".set":
                var result = ArenaImgDecoder.Decode(bytes, logicalName);
                embeddedPalette = result.EmbeddedPalette;
                return result.Images;
            case ".cif":
                return ArenaCifDecoder.Decode(bytes, logicalName);
            case ".dfa":
                return ArenaDfaDecoder.Decode(bytes);
            case ".cfa":
                return ArenaCfaDecoder.Decode(bytes, logicalName);
            default:
                throw new NotSupportedException(
                    $"'{extension}' is not a supported sprite format yet. Supported today: " +
                    ".IMG/.MNU/.SET/.CIF/.DFA/.CFA (Arena) and TEXTURE.nnn sets (Daggerfall); " +
                    "the other classic families land per game vertical.");
        }
    }

    private static (Palette Palette, string Source) ResolvePalette(
        Palette? embedded,
        string? palettePath,
        string sourceDir)
    {
        if (embedded is not null)
        {
            return (embedded, "embedded");
        }

        if (palettePath is not null)
        {
            return (LoadPaletteFile(palettePath), palettePath);
        }

        // Game conventions, in order: Arena's global palette, then Daggerfall's texture palette
        // (everything palettized in ARENA2 indexes ART_PAL.COL except the map screens).
        foreach (var candidate in new[] { "PAL.COL", "ART_PAL.COL" })
        {
            var probe = Path.Combine(sourceDir, candidate);
            if (File.Exists(probe))
            {
                return (LoadPaletteFile(probe), probe);
            }
        }

        throw new FileNotFoundException(
            "No palette: the image has none embedded, --palette was not given, and neither PAL.COL " +
            $"nor ART_PAL.COL sits beside the source ({sourceDir}).");
    }

    private static Palette LoadPaletteFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return bytes.Length switch
        {
            Palette.ColFileLength => Palette.LoadArenaCol(bytes),
            Palette.RgbByteCount => LoadRawPalette(bytes),
            _ => throw new InvalidDataException(
                $"Palette file '{Path.GetFileName(path)}' is {bytes.Length} bytes; expected " +
                $"{Palette.ColFileLength} (COL) or {Palette.RgbByteCount} (raw RGB).")
        };
    }

    /// <summary>
    ///     Raw 768-byte palettes are ambiguous about their component range: Daggerfall's MAP.PAL
    ///     and OLDMAP.PAL are genuine 6-bit VGA data (every component ≤ 63) while OLDPAL.PAL in
    ///     the same directory is full 8-bit (components up to 255). The range itself is the only
    ///     discriminator, so a file whose components never exceed the VGA ceiling is promoted and
    ///     anything else is taken as already 8-bit.
    /// </summary>
    private static Palette LoadRawPalette(byte[] bytes)
    {
        var isVga = true;
        foreach (var component in bytes)
        {
            if (component > 0x3F)
            {
                isVga = false;
                break;
            }
        }

        return isVga ? Palette.FromVga6Bit(bytes) : Palette.FromRgb8(bytes);
    }

    /// <summary>
    ///     Sprites (CIF weapon/creature frames, DFA animations) key color 0 out; full-screen images
    ///     (IMG/MNU backdrops, SET wall tiles) are opaque surfaces where index 0 is a real color.
    /// </summary>
    private static bool TransparentZeroByDefault(string logicalName)
    {
        var extension = Path.GetExtension(logicalName).ToLowerInvariant();
        return extension is ".cif" or ".dfa" or ".cfa";
    }
}

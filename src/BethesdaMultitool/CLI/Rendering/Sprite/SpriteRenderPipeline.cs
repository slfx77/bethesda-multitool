using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.CLI.Rendering.Sprite;

/// <summary>
///     Which classic game's decoders the sprite pipeline should apply. Arena and Daggerfall share
///     the <c>.IMG</c>/<c>.CIF</c>/<c>.RCI</c> extensions and even a near-identical 12-byte image
///     header, but their codecs, headerless-size tables and palettes all differ — so the extension
///     alone cannot choose, and the choice has to be made explicitly or sniffed from context.
/// </summary>
internal enum ClassicSpriteGame
{
    /// <summary>Decide from the palette file beside the source: ART_PAL.COL means Daggerfall, else Arena.</summary>
    Auto,
    Arena,
    Daggerfall
}

/// <summary>
///     The classic-game 2D pipeline behind the <c>sprite</c> command group — the sibling of the NIF
///     render pipeline for palettized DOS-era art. Loads a sprite/image from a loose file or an
///     archive entry, decodes it to <see cref="IndexedBitmap" /> frames, resolves a palette
///     (embedded &gt; explicit <c>--palette</c> &gt; the format's own preferred palette &gt; PAL.COL
///     / ART_PAL.COL beside the source), and writes one PNG per frame/tile.
///     <para>
///         Arena: IMG/MNU/SET/CIF/DFA/CFA. Daggerfall: TEXTURE.nnn sets, IMG (headered and
///         headerless), CIF (plain and weapon animations) and RCI. The other classic families
///         (FRM, ZAR, TIL, SPR, BSI) join per game vertical.
///     </para>
/// </summary>
internal static class SpriteRenderPipeline
{
    internal sealed record RenderedFrame(string Path, int Width, int Height, int XOffset, int YOffset);

    internal sealed record RenderResult(IReadOnlyList<RenderedFrame> Frames, string PaletteSource, ClassicSpriteGame Game);

    /// <summary>
    ///     Everything a decode yields besides the frames themselves. <see cref="PalettePerFrame" />
    ///     is set by formats that light each frame differently (a Daggerfall sky set carries 32
    ///     time-of-day palettes); it overrides every other palette source for those frames.
    /// </summary>
    private sealed record Decoded(
        IReadOnlyList<IndexedBitmap> Frames,
        IReadOnlyList<string>? Labels,
        Palette? EmbeddedPalette,
        string? PreferredPaletteFile,
        bool TransparentZero,
        IReadOnlyList<Palette>? PalettePerFrame = null);

    /// <summary>
    ///     Decodes <paramref name="input" /> (or <paramref name="entryName" /> inside it when the
    ///     input is an archive) and writes PNG frames into <paramref name="outputDir" />.
    /// </summary>
    public static RenderResult Render(
        string input,
        string? entryName,
        string outputDir,
        string? palettePath,
        ClassicSpriteGame game = ClassicSpriteGame.Auto)
    {
        var (bytes, logicalName, sourceDir) = LoadSource(input, entryName);
        var resolvedGame = ResolveGame(game, sourceDir);
        var decoded = DecodeFrames(bytes, logicalName, resolvedGame);

        Palette? palette = null;
        string paletteSource;
        if (decoded.PalettePerFrame is not null)
        {
            paletteSource = "embedded (one per frame)";
        }
        else
        {
            (palette, paletteSource) = ResolvePalette(decoded.EmbeddedPalette, palettePath, decoded.PreferredPaletteFile, sourceDir);
            if (decoded.TransparentZero)
            {
                palette = palette.WithTransparentIndex(0);
            }
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

        var frames = decoded.Frames;
        var written = new List<RenderedFrame>(frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            string label;
            if (decoded.Labels is not null)
            {
                label = $"_{decoded.Labels[i]}";
            }
            else
            {
                label = frames.Count == 1 ? string.Empty : $"_f{i:D2}";
            }

            var path = Path.Combine(outputDir, $"{baseName}{label}.png");
            var texture = frame.ToDecodedTexture(decoded.PalettePerFrame?[i] ?? palette!);
            PngWriter.SaveRgba(texture.Pixels, texture.Width, texture.Height, path);
            written.Add(new RenderedFrame(path, frame.Width, frame.Height, frame.XOffset, frame.YOffset));
        }

        return new RenderResult(written, paletteSource, resolvedGame);
    }

    /// <summary>Decodes without writing — the <c>sprite info</c> half.</summary>
    public static IReadOnlyList<IndexedBitmap> Inspect(
        string input,
        string? entryName,
        ClassicSpriteGame game,
        out string logicalName,
        out ClassicSpriteGame resolvedGame)
    {
        var (bytes, name, sourceDir) = LoadSource(input, entryName);
        logicalName = name;
        resolvedGame = ResolveGame(game, sourceDir);
        return DecodeFrames(bytes, name, resolvedGame).Frames;
    }

    /// <summary>
    ///     An explicit choice wins. Otherwise the palette file beside the source decides: the two
    ///     retail installs are disjoint here (Arena ships only PAL.COL, Daggerfall only
    ///     ART_PAL.COL), and an archive entry inherits its archive's directory. With neither
    ///     present the historical default — Arena — applies, so existing invocations keep working.
    /// </summary>
    private static ClassicSpriteGame ResolveGame(ClassicSpriteGame requested, string sourceDir)
    {
        if (requested != ClassicSpriteGame.Auto)
        {
            return requested;
        }

        if (File.Exists(Path.Combine(sourceDir, "ART_PAL.COL")) && !File.Exists(Path.Combine(sourceDir, "PAL.COL")))
        {
            return ClassicSpriteGame.Daggerfall;
        }

        return ClassicSpriteGame.Arena;
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

    private static Decoded DecodeFrames(byte[] bytes, string logicalName, ClassicSpriteGame game)
    {
        // Daggerfall's TEXTURE.nnn sets are named by convention, not extension, and are
        // unambiguous whichever game was resolved.
        if (DaggerfallTextureFile.IsTextureFileName(logicalName))
        {
            var texture = DaggerfallTextureFile.Parse(bytes, logicalName);
            var (images, labels) = Flatten(texture.Records.Select(r => (r.Index, r.Frames)));
            return new Decoded(images, labels, null, "ART_PAL.COL", TransparentZero: false);
        }

        return game == ClassicSpriteGame.Daggerfall
            ? DecodeDaggerfall(bytes, logicalName)
            : DecodeArena(bytes, logicalName);
    }

    private static Decoded DecodeArena(byte[] bytes, string logicalName)
    {
        var extension = Path.GetExtension(logicalName).ToLowerInvariant();
        switch (extension)
        {
            case ".img":
            case ".mnu":
            case ".set":
                var result = ArenaImgDecoder.Decode(bytes, logicalName);
                return new Decoded(result.Images, null, result.EmbeddedPalette, null, TransparentZero: false);
            case ".cif":
                return new Decoded(ArenaCifDecoder.Decode(bytes, logicalName), null, null, null, TransparentZero: true);
            case ".dfa":
                return new Decoded(ArenaDfaDecoder.Decode(bytes), null, null, null, TransparentZero: true);
            case ".cfa":
                return new Decoded(ArenaCfaDecoder.Decode(bytes, logicalName), null, null, null, TransparentZero: true);
            default:
                throw new NotSupportedException(
                    $"'{extension}' is not a supported Arena sprite format. Supported: " +
                    ".IMG/.MNU/.SET/.CIF/.DFA/.CFA. (Pass --game daggerfall for Daggerfall files of the same extension.)");
        }
    }

    private static Decoded DecodeDaggerfall(byte[] bytes, string logicalName)
    {
        if (DaggerfallSkyFile.IsSkyFileName(logicalName))
        {
            // 64 frames — west half w00..w31, east half e00..e31 — each lit by its own
            // time-of-day palette.
            var sky = DaggerfallSkyFile.Parse(bytes, logicalName);
            var labels = new List<string>(DaggerfallSkyFile.FrameCount);
            var palettes = new List<Palette>(DaggerfallSkyFile.FrameCount);
            for (var i = 0; i < DaggerfallSkyFile.FrameCount; i++)
            {
                labels.Add($"{(i < DaggerfallSkyFile.FramesPerHalf ? 'w' : 'e')}{i % DaggerfallSkyFile.FramesPerHalf:D2}");
                palettes.Add(sky.PaletteFor(i));
            }

            return new Decoded(sky.Frames, labels, null, null, TransparentZero: false, palettes);
        }

        if (DaggerfallImgFile.IsImgFileName(logicalName))
        {
            var img = DaggerfallImgFile.Parse(bytes, logicalName);

            // Six fullscreens carry their own palette; everything else names an external one
            // (ART_PAL.COL for most, FMAP_PAL/NIGHTSKY/DANKBMAP/MAP.PAL for the special screens).
            var preferred = img.EmbeddedPalette is null ? DaggerfallImgFile.GetPaletteFileName(logicalName) : null;
            return new Decoded([img.Bitmap], null, img.EmbeddedPalette, preferred, TransparentZero: false);
        }

        if (DaggerfallCifRciFile.IsCifRciFileName(logicalName))
        {
            var cif = DaggerfallCifRciFile.Parse(bytes, logicalName);
            var (images, labels) = Flatten(cif.Records.Select(r => (r.Index, r.Frames)));

            // CIF/RCI hold sprites, portraits and weapon frames: index 0 is the keyed-out background.
            return new Decoded(images, labels, null, DaggerfallCifRciFile.PaletteName, TransparentZero: true);
        }

        var extension = Path.GetExtension(logicalName).ToLowerInvariant();
        throw new NotSupportedException(
            $"'{extension}' is not a supported Daggerfall sprite format. Supported: " +
            "TEXTURE.nnn, .IMG, .CIF, .RCI, SKYnn.DAT.");
    }

    /// <summary>Record-major flattening with <c>rNN</c> / <c>rNN_fMM</c> labels.</summary>
    private static (List<IndexedBitmap> Images, List<string> Labels) Flatten(
        IEnumerable<(int Index, IReadOnlyList<IndexedBitmap> Frames)> records)
    {
        var images = new List<IndexedBitmap>();
        var labels = new List<string>();
        foreach (var (index, frames) in records)
        {
            for (var f = 0; f < frames.Count; f++)
            {
                images.Add(frames[f]);
                labels.Add(frames.Count == 1 ? $"r{index:D2}" : $"r{index:D2}_f{f:D2}");
            }
        }

        return (images, labels);
    }

    private static (Palette Palette, string Source) ResolvePalette(
        Palette? embedded,
        string? palettePath,
        string? preferredPaletteFile,
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

        // The format's own routing first (a Daggerfall map screen names MAP.PAL, a night sky
        // NIGHTSKY.COL), then the game conventions: Arena's global palette, then Daggerfall's
        // texture palette.
        var candidates = new List<string>(3);
        if (!string.IsNullOrEmpty(preferredPaletteFile))
        {
            candidates.Add(preferredPaletteFile);
        }

        candidates.Add("PAL.COL");
        candidates.Add("ART_PAL.COL");

        foreach (var candidate in candidates)
        {
            var probe = Path.Combine(sourceDir, candidate);
            if (File.Exists(probe))
            {
                return (LoadPaletteFile(probe), probe);
            }
        }

        throw new FileNotFoundException(
            "No palette: the image has none embedded, --palette was not given, and none of " +
            $"{string.Join(", ", candidates)} sits beside the source ({sourceDir}).");
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
        var isVga = Array.TrueForAll(bytes, component => component <= 0x3F);
        return isVga ? Palette.FromVga6Bit(bytes) : Palette.FromRgb8(bytes);
    }
}

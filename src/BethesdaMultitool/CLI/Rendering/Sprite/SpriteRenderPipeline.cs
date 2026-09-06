using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Formats.Battlespire;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Fallout;
using BethesdaMultitool.Core.Formats.Png;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Formats.Travels.Stormhold;
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
    Daggerfall,

    /// <summary>Battlespire, whose <c>.BSI</c> images carry their own palettes.</summary>
    Battlespire,

    /// <summary>Fallout 1 and 2, whose FRM sprites index the shared COLOR.PAL.</summary>
    Fallout,

    /// <summary>Redguard, whose <c>.GXA</c> images carry their own palettes.</summary>
    Redguard,

    /// <summary>TES Travels: Stormhold, whose <c>.cus</c> sprites carry their own 4:4:4 palettes.</summary>
    Stormhold
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
        IReadOnlyList<string>? PreferredPaletteFiles,
        bool TransparentZero,
        IReadOnlyList<Palette>? PalettePerFrame = null);

    /// <summary>
    ///     Resolves a palette FILE NAME to its bytes, wherever the caller's world keeps it — a
    ///     directory, an archive, or a merged game filesystem. Return null when it is not there.
    /// </summary>
    internal delegate byte[]? PaletteLookup(string fileName);

    /// <summary>One decoded frame as RGBA, with the label the file-writing route would give it.</summary>
    internal sealed record DecodedFrame(string Label, DecodedTexture Texture, int XOffset, int YOffset);

    /// <summary>Every frame of a source, decoded to RGBA, plus where its palette came from.</summary>
    internal sealed record DecodedFrames(
        IReadOnlyList<DecodedFrame> Frames, string PaletteSource, ClassicSpriteGame Game, string BaseName);

    /// <summary>
    ///     Decodes <paramref name="input" /> (or <paramref name="entryName" /> inside it when the
    ///     input is an archive) to RGBA <b>in memory</b>, writing nothing.
    ///     <para>
    ///         This is the route the GUI takes for thumbnails and previews, and the route
    ///         <see cref="Render" /> takes before it writes PNGs — so the CLI and the browser can
    ///         never disagree about how a sprite decodes or which palette it resolved.
    ///     </para>
    /// </summary>
    public static DecodedFrames Decode(
        string input,
        string? entryName,
        string? palettePath = null,
        ClassicSpriteGame game = ClassicSpriteGame.Auto)
    {
        var (bytes, logicalName, sourceDir, archivePath) = LoadSource(input, entryName);
        var resolvedGame = ResolveGame(game, sourceDir, logicalName);
        return DecodeCore(
            bytes, logicalName, resolvedGame,
            candidates => ResolvePalette(palettePath, candidates, sourceDir, archivePath));
    }

    /// <summary>
    ///     Decodes bytes already in hand, resolving any companion palette through
    ///     <paramref name="lookup" /> instead of the filesystem.
    ///     <para>
    ///         This is the browser's route. It exists because a merged game filesystem has no
    ///         "directory beside the source" and no single owning archive, and because re-opening a
    ///         339 MB DAT once per gallery cell is not viable. The decoding and palette-precedence
    ///         rules are the same ones <see cref="Decode" /> uses — only the lookup differs.
    ///     </para>
    /// </summary>
    public static DecodedFrames DecodeBytes(
        byte[] bytes,
        string logicalName,
        PaletteLookup lookup,
        ClassicSpriteGame game = ClassicSpriteGame.Auto)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(logicalName);
        ArgumentNullException.ThrowIfNull(lookup);

        var resolvedGame = game == ClassicSpriteGame.Auto ? ResolveGameByName(logicalName) : game;
        return DecodeCore(bytes, logicalName, resolvedGame, candidates => ResolveViaLookup(lookup, candidates));
    }

    private static DecodedFrames DecodeCore(
        byte[] bytes,
        string logicalName,
        ClassicSpriteGame resolvedGame,
        Func<IReadOnlyList<string>, (Palette Palette, string Source)> resolveFromCandidates)
    {
        // A PNG is already true colour and carries its own palette inside the file, so it skips
        // the whole companion-palette resolution below. The three J2ME TES Travels titles ship
        // all of their art this way.
        if (PngImageDecoder.HasPngSignature(bytes))
        {
            return DecodePng(bytes, logicalName, resolvedGame);
        }

        var decoded = DecodeFrames(bytes, logicalName, resolvedGame);

        Palette? palette = null;
        string paletteSource;
        if (decoded.PalettePerFrame is not null)
        {
            paletteSource = "embedded (one per frame)";
        }
        else if (decoded.EmbeddedPalette is not null)
        {
            (palette, paletteSource) = (decoded.EmbeddedPalette, "embedded");
        }
        else
        {
            // The format's own routing first (a Fallout slide names its own <STEM>.PAL, a
            // Daggerfall map screen names MAP.PAL), then the game conventions.
            var candidates = new List<string>(4);
            if (decoded.PreferredPaletteFiles is not null)
            {
                candidates.AddRange(decoded.PreferredPaletteFiles.Where(name => !string.IsNullOrEmpty(name)));
            }

            candidates.Add("PAL.COL");
            candidates.Add("ART_PAL.COL");
            (palette, paletteSource) = resolveFromCandidates(candidates);
        }

        if (palette is not null && decoded.TransparentZero)
        {
            palette = palette.WithTransparentIndex(0);
        }

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
        var result = new List<DecodedFrame>(frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            var unlabelled = frames.Count == 1 ? string.Empty : $"f{i:D2}";
            var label = decoded.Labels is not null ? decoded.Labels[i] : unlabelled;

            var texture = frame.ToDecodedTexture(decoded.PalettePerFrame?[i] ?? palette!);
            result.Add(new DecodedFrame(label, texture, frame.XOffset, frame.YOffset));
        }

        return new DecodedFrames(result, paletteSource, resolvedGame, baseName);
    }

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
        var decoded = Decode(input, entryName, palettePath, game);
        Directory.CreateDirectory(outputDir);

        var written = new List<RenderedFrame>(decoded.Frames.Count);
        foreach (var frame in decoded.Frames)
        {
            var suffix = frame.Label.Length == 0 ? string.Empty : $"_{frame.Label}";
            var path = Path.Combine(outputDir, $"{decoded.BaseName}{suffix}.png");
            var texture = frame.Texture;
            PngWriter.SaveRgba(texture.Pixels, texture.Width, texture.Height, path);
            written.Add(new RenderedFrame(path, texture.Width, texture.Height, frame.XOffset, frame.YOffset));
        }

        return new RenderResult(written, decoded.PaletteSource, decoded.Game);
    }

    /// <summary>Decodes without writing — the <c>sprite info</c> half.</summary>
    public static IReadOnlyList<IndexedBitmap> Inspect(
        string input,
        string? entryName,
        ClassicSpriteGame game,
        out string logicalName,
        out ClassicSpriteGame resolvedGame)
    {
        var (bytes, name, sourceDir, _) = LoadSource(input, entryName);
        logicalName = name;
        resolvedGame = ResolveGame(game, sourceDir, logicalName);
        return DecodeFrames(bytes, name, resolvedGame).Frames;
    }

    /// <summary>
    ///     An explicit choice wins. Otherwise the palette file beside the source decides: the two
    ///     retail installs are disjoint here (Arena ships only PAL.COL, Daggerfall only
    ///     ART_PAL.COL), and an archive entry inherits its archive's directory. With neither
    ///     present the historical default — Arena — applies, so existing invocations keep working.
    /// </summary>
    private static ClassicSpriteGame ResolveGame(ClassicSpriteGame requested, string sourceDir, string logicalName)
    {
        if (requested != ClassicSpriteGame.Auto)
        {
            return requested;
        }

        if (ResolveGameByName(logicalName) is var byName && byName != ClassicSpriteGame.Arena)
        {
            return byName;
        }

        // Only Arena and Daggerfall are still ambiguous by name, and only the palette file beside
        // the source separates them.
        if (File.Exists(Path.Combine(sourceDir, "ART_PAL.COL")) && !File.Exists(Path.Combine(sourceDir, "PAL.COL")))
        {
            return ClassicSpriteGame.Daggerfall;
        }

        return ClassicSpriteGame.Arena;
    }

    /// <summary>
    ///     The half of game resolution that needs only the file name. Several extensions belong to
    ///     exactly one game and carry their own palettes, so there is nothing to sniff for them.
    ///     <para>
    ///         ⚠ Returns <see cref="ClassicSpriteGame.Arena" /> as its "nothing matched" answer,
    ///         because Arena and Daggerfall SHARE .IMG/.CIF/.RCI and can only be told apart by the
    ///         palette sitting beside the file. A caller with no directory to sniff (the browser,
    ///         reading from a merged filesystem) therefore gets Arena's codecs for those three
    ///         extensions — correct for Arena, and wrong for Daggerfall until the caller passes the
    ///         game explicitly. Callers that know the game MUST say so.
    ///     </para>
    /// </summary>
    private static ClassicSpriteGame ResolveGameByName(string logicalName)
    {
        var extension = Path.GetExtension(logicalName);
        if (extension.Equals(".gxa", StringComparison.OrdinalIgnoreCase) ||
            RedguardTexBsiFile.IsTexBsiFileName(logicalName))
        {
            return ClassicSpriteGame.Redguard;
        }

        if (FalloutFrmFile.IsFrmFileName(logicalName))
        {
            return ClassicSpriteGame.Fallout;
        }

        if (BsiFile.IsBsiFileName(logicalName))
        {
            return ClassicSpriteGame.Battlespire;
        }

        if (extension.Equals(".cus", StringComparison.OrdinalIgnoreCase))
        {
            return ClassicSpriteGame.Stormhold;
        }

        return ClassicSpriteGame.Arena;
    }

    private static (byte[] Bytes, string LogicalName, string SourceDir, string? ArchivePath) LoadSource(
        string input, string? entryName)
    {
        if (!File.Exists(input))
        {
            throw new FileNotFoundException($"Input not found: {input}", input);
        }

        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".";
        if (entryName is null)
        {
            return (File.ReadAllBytes(input), Path.GetFileName(input), sourceDir, null);
        }

        using var reader = ArchiveReader.Open(input);
        var bytes = reader.ReadFile(entryName) ??
                    throw new FileNotFoundException(
                        $"Entry '{entryName}' not found in {Path.GetFileName(input)} ({reader.FormatName}, {reader.TotalFiles} files).");
        return (bytes, Path.GetFileName(entryName.Replace('/', '\\')), sourceDir, input);
    }

    /// <summary>
    ///     A standard PNG, decoded straight to RGBA. Returned as a single unlabelled frame with no
    ///     draw offset, because PNG carries neither — the offsets in <see cref="DecodedFrame" />
    ///     come from sprite formats that position frames on a screen.
    /// </summary>
    private static DecodedFrames DecodePng(byte[] bytes, string logicalName, ClassicSpriteGame resolvedGame)
    {
        var texture = PngImageDecoder.Decode(bytes);

        var baseName = Path.GetFileNameWithoutExtension(logicalName);
        if (string.IsNullOrEmpty(baseName))
        {
            baseName = logicalName.Replace('.', '_');
        }

        return new DecodedFrames(
            [new DecodedFrame(string.Empty, texture, 0, 0)],
            "embedded (PNG)",
            resolvedGame,
            baseName);
    }

    private static Decoded DecodeFrames(byte[] bytes, string logicalName, ClassicSpriteGame game)
    {
        // This route reports palette INDICES, which a PNG does not have — it decodes directly to
        // true colour. Say that plainly rather than failing deep inside another game's parser.
        if (PngImageDecoder.HasPngSignature(bytes))
        {
            throw new NotSupportedException(
                $"'{logicalName}' is a PNG: it decodes straight to RGBA and has no palette " +
                "indices to inspect. Use 'sprite render' to write it out.");
        }

        // A .BSI is Battlespire's and nobody else's, and it carries its own palettes, so the
        // extension decides regardless of which game was resolved.
        if (BsiFile.IsBsiFileName(logicalName))
        {
            return DecodeBsi(bytes, logicalName);
        }

        // A .GXA is Redguard's and nobody else's, and like a .BSI it carries its own palette, so
        // the extension decides here too.
        if (Path.GetExtension(logicalName).Equals(".gxa", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeGxa(bytes, logicalName);
        }

        // TEXBSI.### sets are named by convention like Daggerfall's TEXTURE.nnn, and are Redguard's
        // alone, so the name decides whichever game was resolved.
        if (RedguardTexBsiFile.IsTexBsiFileName(logicalName))
        {
            return DecodeTexBsi(bytes, logicalName);
        }

        // FRM (and its .FR0-.FR5 direction siblings) belong to the Fallout family alone.
        if (FalloutFrmFile.IsFrmFileName(logicalName))
        {
            return DecodeFrm(bytes, logicalName);
        }

        // A .cus is Stormhold's alone and carries its own palette, so — like .BSI and .GXA — the
        // extension decides whichever game was resolved.
        if (Path.GetExtension(logicalName).Equals(".cus", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeCus(bytes, logicalName);
        }

        // Daggerfall's TEXTURE.nnn sets are named by convention, not extension, and are
        // unambiguous whichever game was resolved.
        if (DaggerfallTextureFile.IsTextureFileName(logicalName))
        {
            var texture = DaggerfallTextureFile.Parse(bytes, logicalName);
            var (images, labels) = Flatten(texture.Records.Select(r => (r.Index, r.Frames)));
            return new Decoded(images, labels, null, ["ART_PAL.COL"], TransparentZero: false);
        }

        return game == ClassicSpriteGame.Daggerfall
            ? DecodeDaggerfall(bytes, logicalName)
            : DecodeArena(bytes, logicalName);
    }

    /// <summary>
    ///     Battlespire images: every frame of every image in the file, labelled <c>iNN_fMM</c> when
    ///     the file holds more than one image. The palette comes from the image's own CMAP chunk;
    ///     HICL only fills the even slots and cannot render the retail art, which indexes all 256.
    /// </summary>
    private static Decoded DecodeBsi(byte[] bytes, string logicalName)
    {
        var file = BsiFile.Parse(bytes, logicalName);
        var images = new List<IndexedBitmap>();
        var labels = new List<string>();
        var palettes = new List<Palette>();
        var fallback = Palette.FromRgb8(new byte[768]);

        for (var i = 0; i < file.Images.Count; i++)
        {
            var image = file.Images[i];
            var palette = image.ColorMap ?? image.HighColor ?? fallback;
            for (var f = 0; f < image.Frames.Count; f++)
            {
                images.Add(image.Frames[f]);
                palettes.Add(palette);
                labels.Add($"i{i:D2}_f{f:D2}");
            }
        }

        // One image: let the pipeline's own single/multi-frame naming apply.
        return new Decoded(images, file.Images.Count == 1 ? null : labels, palettes[0], null, TransparentZero: false, palettes);
    }

    /// <summary>
    ///     Redguard <c>.GXA</c> images: menus, HUD art and the full-screen location paintings. The
    ///     file carries its own 6-bit VGA palette, so nothing beside it needs resolving. Frames are
    ///     labelled <c>fNN</c> when there is more than one — GXICONS and COMPASS2 hold dozens.
    /// </summary>
    private static Decoded DecodeGxa(byte[] bytes, string logicalName)
    {
        var file = RedguardGxaFile.Parse(bytes, logicalName);
        var labels = file.Frames.Count == 1
            ? null
            : Enumerable.Range(0, file.Frames.Count).Select(static i => $"f{i:D2}").ToList();

        return new Decoded(file.Frames, labels, file.Palette, null, TransparentZero: false);
    }

    /// <summary>
    ///     Redguard 3dfx textures. Only the 56 animated images carry a CMAP of their own; the rest
    ///     take the palette the pipeline resolves (a <c>.COL</c> beside the set, or <c>--palette</c>),
    ///     so a per-frame palette list is supplied only when every frame has one.
    /// </summary>
    private static Decoded DecodeTexBsi(byte[] bytes, string logicalName)
    {
        var file = RedguardTexBsiFile.Parse(bytes, logicalName);
        var images = new List<IndexedBitmap>();
        var labels = new List<string>();
        var palettes = new List<Palette>();
        var everyFrameHasOne = true;

        for (var i = 0; i < file.Images.Count; i++)
        {
            var image = file.Images[i];
            for (var f = 0; f < image.Frames.Count; f++)
            {
                images.Add(image.Frames[f]);
                labels.Add(image.Frames.Count == 1 ? $"i{i:D2}" : $"i{i:D2}_f{f:D2}");
                if (image.Palette is { } palette)
                {
                    palettes.Add(palette);
                }
                else
                {
                    everyFrameHasOne = false;
                }
            }
        }

        return new Decoded(
            images,
            labels,
            everyFrameHasOne && palettes.Count > 0 ? palettes[0] : null,
            null,
            TransparentZero: false,
            everyFrameHasOne && palettes.Count == images.Count ? palettes : null);
    }

    /// <summary>
    ///     Stormhold <c>.cus</c> sprites: one 8-bit indexed image with an embedded 0RGB 4:4:4
    ///     palette. The reader has already resolved the transparent slot (the first entry equal to
    ///     the header's key colour, which is NOT always slot 0) and zeroed its alpha, so the
    ///     pipeline's own index-0 rule must stay off.
    ///     <para>
    ///         Multi-pose art is a horizontal strip whose frame count lives in the game's monster
    ///         descriptor tables rather than in the file, so a render emits the whole strip as one
    ///         image — slicing it needs a count no caller can honestly supply yet.
    ///     </para>
    /// </summary>
    private static Decoded DecodeCus(byte[] bytes, string logicalName)
    {
        var image = StormholdCusImage.Parse(bytes, logicalName);
        return new Decoded([image.Bitmap], null, image.Palette, null, TransparentZero: false);
    }

    /// <summary>
    ///     Fallout FRM sprites. Directions that share artwork share an offset, so only distinct
    ///     runs are emitted, labelled <c>dN_fMM</c>. The palette is the install's shared COLOR.PAL,
    ///     which the pipeline resolves like any other preferred palette file.
    /// </summary>
    private static Decoded DecodeFrm(byte[] bytes, string logicalName)
    {
        var file = FalloutFrmFile.Parse(bytes, logicalName);
        var images = new List<IndexedBitmap>();
        var labels = new List<string>();
        var seen = new HashSet<uint>();

        foreach (var direction in file.Directions)
        {
            if (!seen.Add(direction.DataOffset))
            {
                continue;
            }

            for (var f = 0; f < direction.Frames.Count; f++)
            {
                images.Add(direction.Frames[f].Bitmap);
                labels.Add($"d{direction.Index}_f{f:D2}");
            }
        }

        // Index 0 is transparent in every Fallout sprite; FalloutPalette applies that itself when
        // COLOR.PAL is the source, and TransparentZero covers the other palette routes.
        return new Decoded(
            images, images.Count == 1 ? null : labels, null, FalloutPalette.CandidatesFor(logicalName), TransparentZero: true);
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
            return new Decoded(
                [img.Bitmap], null, img.EmbeddedPalette, preferred is null ? null : [preferred], TransparentZero: false);
        }

        if (DaggerfallCifRciFile.IsCifRciFileName(logicalName))
        {
            var cif = DaggerfallCifRciFile.Parse(bytes, logicalName);
            var (images, labels) = Flatten(cif.Records.Select(r => (r.Index, r.Frames)));

            // CIF/RCI hold sprites, portraits and weapon frames: index 0 is the keyed-out background.
            return new Decoded(images, labels, null, [DaggerfallCifRciFile.PaletteName], TransparentZero: true);
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

    /// <summary>Resolves a palette by trying the caller's lookup for each candidate in order.</summary>
    private static (Palette Palette, string Source) ResolveViaLookup(PaletteLookup lookup, IReadOnlyList<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (lookup(candidate) is { } bytes)
            {
                return (LoadPaletteBytes(candidate, bytes), candidate);
            }
        }

        throw new FileNotFoundException(
            $"No palette: the image has none embedded and none of {string.Join(", ", candidates)} " +
            "is reachable in this source.");
    }

    private static (Palette Palette, string Source) ResolvePalette(
        string? palettePath,
        IReadOnlyList<string> candidates,
        string sourceDir,
        string? archivePath = null)
    {
        if (palettePath is not null)
        {
            return (LoadPaletteFile(palettePath), palettePath);
        }

        // A sprite pulled out of an archive is usually shipped with its palette in the SAME
        // archive rather than beside it on disk — Fallout keeps COLOR.PAL inside MASTER.DAT, so on
        // a stock install there is no file beside the source to find at all.
        if (archivePath is not null && TryLoadPaletteFromArchive(archivePath, candidates) is { } fromArchive)
        {
            return fromArchive;
        }

        foreach (var candidate in candidates)
        {
            var probe = Path.Combine(sourceDir, candidate);
            if (File.Exists(probe))
            {
                return (LoadPaletteFile(probe), probe);
            }
        }

        // Last, the install's OTHER archives. Fallout splits its data across MASTER.DAT and
        // CRITTER.DAT but ships one COLOR.PAL, in MASTER — so a critter sprite can only ever
        // resolve its palette from a sibling. This runs on the failure path alone, after every
        // cheaper route has missed.
        if (archivePath is not null && TryLoadPaletteFromSiblings(archivePath, candidates) is { } fromSibling)
        {
            return fromSibling;
        }

        throw new FileNotFoundException(
            "No palette: the image has none embedded, --palette was not given, and none of " +
            $"{string.Join(", ", candidates)} is inside the archive, beside the source, or in a " +
            $"sibling archive ({sourceDir}).");
    }

    /// <summary>
    ///     Looks each candidate palette up inside the archive by file name, ignoring which
    ///     directory it sits in — an archive that carries a palette carries exactly one of it.
    /// </summary>
    private static (Palette Palette, string Source)? TryLoadPaletteFromArchive(string archivePath, IReadOnlyList<string> candidates)
    {
        using var reader = ArchiveReader.Open(archivePath);
        var entries = reader.ListFiles();
        foreach (var candidate in candidates)
        {
            foreach (var entry in entries)
            {
                if (entry.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return (LoadPaletteBytes(entry.Name, reader.Extract(entry)),
                        $"{Path.GetFileName(archivePath)}:{entry.FullPath}");
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Searches the other archives of the same kind in the install directory. Ordered by name
    ///     so the search is deterministic, and failures to open a sibling are ignored — a stray
    ///     file with the same extension must not turn a missing palette into a crash.
    /// </summary>
    private static (Palette Palette, string Source)? TryLoadPaletteFromSiblings(string archivePath, IReadOnlyList<string> candidates)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(archivePath));
        if (directory is null)
        {
            return null;
        }

        var siblings = Directory.GetFiles(directory, $"*{Path.GetExtension(archivePath)}")
            .Where(path => !string.Equals(path, Path.GetFullPath(archivePath), StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);

        foreach (var sibling in siblings)
        {
            try
            {
                if (TryLoadPaletteFromArchive(sibling, candidates) is { } found)
                {
                    return found;
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
            {
                // Not an archive we can open, or not one of ours — keep looking.
            }
        }

        return null;
    }

    private static Palette LoadPaletteFile(string path)
    {
        return LoadPaletteBytes(path, File.ReadAllBytes(path));
    }

    /// <summary>
    ///     Decodes palette bytes. Fallout's COLOR.PAL is routed BY NAME rather than by length: it
    ///     is 33,536 bytes (768 + a 32x32x32 lookup cube), so the length dispatch would reject it
    ///     outright, and its 255s are sentinels, so the range sniff below would read the 768-byte
    ///     head as 8-bit and render the whole game four times too dark.
    /// </summary>
    private static Palette LoadPaletteBytes(string name, byte[] bytes)
    {
        // Fallout's palettes are identified by their own shape, not just the global name: every
        // one is 768 palette bytes plus a 32x32x32 lookup cube, which no other palette here is.
        // The per-image slide palettes (DEATH.PAL, SEQ*.PAL) arrive through this route too.
        if (bytes.Length == FalloutPalette.RetailFileLength ||
            Path.GetFileName(name).Equals(FalloutPalette.FileName, StringComparison.OrdinalIgnoreCase))
        {
            return FalloutPalette.Parse(bytes, name);
        }

        return bytes.Length switch
        {
            Palette.ColFileLength => Palette.LoadArenaCol(bytes),
            Palette.RgbByteCount => LoadRawPalette(bytes),
            _ => throw new InvalidDataException(
                $"Palette file '{Path.GetFileName(name)}' is {bytes.Length} bytes; expected " +
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

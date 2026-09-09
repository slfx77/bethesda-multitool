using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Formats.Battlespire;
using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Fallout;
using BethesdaMultitool.Core.Formats.Png;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Formats.Tactics;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
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
    Stormhold,

    /// <summary>TES Travels: Shadowkey, whose <c>.ztx</c> texture banks index the zone's own <c>.pal</c>.</summary>
    Shadowkey,

    /// <summary>Fallout Tactics, whose <c>.zar</c> images carry their own B,G,R palette and per-pixel alpha.</summary>
    Tactics,

    /// <summary>
    ///     Fallout: Brotherhood of Steel (PS2), whose <c>.tex</c> textures carry their own CLUT and
    ///     sit inside <c>.CLP</c> clumps — a clump entry renders every texture it holds.
    /// </summary>
    BrotherhoodOfSteel
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
        ClassicSpriteGame game = ClassicSpriteGame.Auto,
        string? animation = null)
    {
        var (bytes, logicalName, sourceDir, archivePath) = LoadSource(input, entryName);
        var resolvedGame = ResolveGame(game, sourceDir, logicalName);
        return DecodeCore(
            bytes, logicalName, resolvedGame,
            candidates => ResolvePalette(palettePath, candidates, sourceDir, archivePath),
            animation);
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
        Func<IReadOnlyList<string>, (Palette Palette, string Source)> resolveFromCandidates,
        string? animation = null)
    {
        // A PNG is already true colour and carries its own palette inside the file, so it skips
        // the whole companion-palette resolution below. The three J2ME TES Travels titles ship
        // all of their art this way.
        if (PngImageDecoder.HasPngSignature(bytes))
        {
            return DecodePng(bytes, logicalName, resolvedGame);
        }

        // A Fallout Tactics <zar> carries its own palette AND per-pixel alpha (translucent and
        // shadow runs), which an index plane plus one palette cannot express — so, like a PNG, it
        // decodes straight to RGBA and skips the companion-palette resolution. Recognised by its
        // tag, not its name: the same stream sits inside .til tiles and save headers.
        if (TacticsZarImage.IsZar(bytes))
        {
            return DecodeZar(bytes, logicalName);
        }

        // A Tactics .til wraps one or more <zar> streams around a shared palette, and a .spr wraps
        // four palettised LAYERS per (frame, direction) — neither is expressible as one index
        // plane plus one palette, so both decode straight to RGBA like the loose <zar> above.
        if (TacticsTileFile.IsTile(bytes))
        {
            return DecodeTile(bytes, logicalName);
        }

        if (TacticsSpriteFile.IsSprite(bytes))
        {
            return DecodeSprite(bytes, logicalName, animation);
        }

        // A Brotherhood of Steel .CLP clump holds many .tex textures, each with its own CLUT and
        // GS alpha, so — like a PNG — the route decodes straight to RGBA. Recognised by content:
        // the clump by its magic and table CRC, a loose .tex by its header relations.
        if (BosClumpFile.IsClump(bytes))
        {
            return DecodeBosClump(bytes, logicalName);
        }

        if (BosTexture.IsTexture(bytes))
        {
            return DecodeBosTexture(bytes, logicalName);
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
        ClassicSpriteGame game = ClassicSpriteGame.Auto,
        string? animation = null)
    {
        var decoded = Decode(input, entryName, palettePath, game, animation);
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

    /// <summary>
    ///     The <c>sprite info</c> route. Most formats describe themselves through the index planes
    ///     <see cref="Inspect" /> already produces; the two Fallout Tactics containers do not,
    ///     because a <c>.til</c>'s frames are separate palettised streams and a <c>.spr</c>'s
    ///     "frames" are (animation, frame, direction) triples whose pixels live in a block that can
    ///     be tens of megabytes — so those two answer from their headers instead of decoding.
    /// </summary>
    public static IReadOnlyList<FrameInfo> Describe(
        string input,
        string? entryName,
        ClassicSpriteGame game,
        out string logicalName,
        out ClassicSpriteGame resolvedGame,
        out string summary)
    {
        var (bytes, name, sourceDir, _) = LoadSource(input, entryName);
        logicalName = name;
        resolvedGame = ResolveGame(game, sourceDir, name);

        if (TacticsTileFile.IsTile(bytes))
        {
            resolvedGame = ClassicSpriteGame.Tactics;
            return DescribeTile(bytes, name, out summary);
        }

        if (TacticsSpriteFile.IsSprite(bytes))
        {
            resolvedGame = ClassicSpriteGame.Tactics;
            return DescribeSprite(bytes, name, out summary);
        }

        summary = string.Empty;
        var frames = DecodeFrames(bytes, name, resolvedGame).Frames;
        var rows = new List<FrameInfo>(frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            rows.Add(new FrameInfo(
                $"f{i:D2}", frames[i].Width, frames[i].Height, frames[i].XOffset, frames[i].YOffset, string.Empty));
        }

        return rows;
    }

    private static List<FrameInfo> DescribeTile(byte[] bytes, string logicalName, out string summary)
    {
        var tile = TacticsTileFile.Parse(bytes, logicalName);
        var header = tile.Header;
        summary =
            $"<tile> v{header.Version} · {header.Type}/{header.Material} · " +
            $"bbox {header.BoundingBoxX}x{header.BoundingBoxY}x{header.BoundingBoxZ} (class {header.BoundingBoxClass}) · " +
            $"foot ({header.FootPositionX},{header.FootPositionY}) · declared image {header.ImageWidth}x{header.ImageHeight} · " +
            $"flags {(header.Flags == TacticsTileFlags.None ? "none" : header.Flags.ToString())} · " +
            $"shared palette {tile.SharedPaletteBgrx.Length / 4} entries";

        var rows = new List<FrameInfo>(tile.Images.Count);
        for (var i = 0; i < tile.Images.Count; i++)
        {
            var entry = tile.Images[i];
            rows.Add(new FrameInfo(
                $"i{i:D2}",
                entry.Image.Width,
                entry.Image.Height,
                header.FootPositionX,
                header.FootPositionY,
                $"<zar> v{entry.Image.Version}, shadow index {entry.Image.ShadowIndex}, offset ({entry.OffsetX},{entry.OffsetY})"));
        }

        return rows;
    }

    private static List<FrameInfo> DescribeSprite(byte[] bytes, string logicalName, out string summary)
    {
        var sprite = TacticsSpriteFile.Parse(bytes, logicalName);
        summary =
            $"<sprite> v{TacticsSpriteFile.Version} · {sprite.Material} · " +
            $"bbox {sprite.BoundingBoxX},{sprite.BoundingBoxY},{sprite.BoundingBoxZ} · " +
            $"foot ({sprite.FootPositionX},{sprite.FootPositionY}) · " +
            $"{sprite.Animations.Count} animation(s), {sprite.Sequences.Count} sequence(s)";

        var rows = new List<FrameInfo>(sprite.Animations.Count);
        for (var i = 0; i < sprite.Animations.Count; i++)
        {
            var animation = sprite.Animations[i];
            var rect = animation.FrameCount > 0 && animation.DirectionCount > 0
                ? animation.Rect(0, 0)
                : default;
            rows.Add(new FrameInfo(
                $"a{i:D2} {animation.Name}",
                rect.Width,
                rect.Height,
                rect.X0,
                rect.Y0,
                $"{animation.FrameCount} frame(s) x {animation.DirectionCount} direction(s), image block @{animation.ImageBlockOffset}"));
        }

        return rows;
    }

    /// <summary>Decodes without writing — the index-plane half of <c>sprite info</c>.</summary>
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

        if (extension.Equals(".ztx", StringComparison.OrdinalIgnoreCase))
        {
            return ClassicSpriteGame.Shadowkey;
        }

        // .zar (loose GUI art), .til (map tiles) and .spr (characters) are Fallout Tactics' alone.
        // All three carry their own palettes, so nothing needs sniffing beside them.
        if (extension.Equals(".zar", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".til", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".spr", StringComparison.OrdinalIgnoreCase))
        {
            return ClassicSpriteGame.Tactics;
        }

        if (extension.Equals(".clp", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tex", StringComparison.OrdinalIgnoreCase))
        {
            return ClassicSpriteGame.BrotherhoodOfSteel;
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

    /// <summary>
    ///     A Fallout Tactics <c>&lt;zar&gt;</c>: one image, its own B,G,R palette, and per-pixel alpha
    ///     from the translucent and shadow runs, decoded as the game's <c>FUN_006f9a10</c> does. A
    ///     loose file must be exactly one record (839/839 GUI files are). A palette-less stream —
    ///     a sprite layer — cannot be rendered here because its palette belongs to the sprite.
    /// </summary>
    private static DecodedFrames DecodeZar(byte[] bytes, string logicalName)
    {
        var zar = TacticsZarImage.Parse(bytes, logicalName);
        if (!zar.HasPalette)
        {
            throw new NotSupportedException(
                $"'{logicalName}' is a <zar> without a palette: it indexes its owning sprite's layer palette, which this route cannot supply.");
        }

        var baseName = Path.GetFileNameWithoutExtension(logicalName);
        if (string.IsNullOrEmpty(baseName))
        {
            baseName = logicalName.Replace('.', '_');
        }

        return new DecodedFrames(
            [new DecodedFrame(string.Empty, zar.Decode(), 0, 0)],
            "embedded",
            ClassicSpriteGame.Tactics,
            baseName);
    }

    /// <summary>
    ///     A Fallout Tactics <c>.til</c>: the <c>&lt;tile&gt;</c> header plus one or more embedded
    ///     <c>&lt;zar&gt;</c> images. Every embedded image carries its own copy of the tile's shared
    ///     palette (31,127 of 31,127 retail images), so each decodes on its own; the shared palette
    ///     is used only as the fallback for a stream that somehow carries none.
    /// </summary>
    private static DecodedFrames DecodeTile(byte[] bytes, string logicalName)
    {
        var tile = TacticsTileFile.Parse(bytes, logicalName);
        var shared = TacticsTilePalette(tile);
        var frames = new List<DecodedFrame>(tile.Images.Count);
        for (var i = 0; i < tile.Images.Count; i++)
        {
            var image = tile.Images[i].Image;
            if (!image.HasImage)
            {
                continue;
            }

            var label = tile.Images.Count == 1 ? string.Empty : $"i{i:D2}";
            var texture = image.HasPalette ? image.Decode() : image.Decode(shared);
            frames.Add(new DecodedFrame(label, texture, tile.Header.FootPositionX, tile.Header.FootPositionY));
        }

        if (frames.Count == 0)
        {
            throw new NotSupportedException(
                $"'{logicalName}' is a <tile> with no pixels ({tile.Images.Count} image slot(s)).");
        }

        return new DecodedFrames(frames, "embedded (shared by the tile)", ClassicSpriteGame.Tactics,
            Path.GetFileNameWithoutExtension(logicalName));
    }

    /// <summary>
    ///     A Fallout Tactics <c>.spr</c>: every (frame, direction) of every animation, composited
    ///     from its four palettised layers as the editor's frame view does. Labels carry the
    ///     animation, direction and frame so a sheet can be reassembled from the file names.
    ///     <paramref name="animation" /> narrows the render to one animation by 0-based index or by
    ///     a case-insensitive name substring; without it every animation is written, which for the
    ///     largest retail sprites is several hundred frames.
    /// </summary>
    private static DecodedFrames DecodeSprite(byte[] bytes, string logicalName, string? animation)
    {
        var sprite = TacticsSpriteFile.Parse(bytes, logicalName);
        var wanted = SelectAnimations(sprite, animation, logicalName);
        var frames = new List<DecodedFrame>();
        foreach (var index in wanted)
        {
            var header = sprite.Animations[index];
            var block = sprite.ReadImageBlock(index);
            for (var frame = 0; frame < header.FrameCount; frame++)
            {
                for (var direction = 0; direction < header.DirectionCount; direction++)
                {
                    if (block.ComposeFrame(header, frame, direction) is not { } texture)
                    {
                        continue;
                    }

                    var rect = header.Rect(frame, direction);
                    frames.Add(new DecodedFrame(
                        $"a{index:D2}_{SanitiseLabel(header.Name)}_d{direction:D2}_f{frame:D3}",
                        texture,
                        rect.X0,
                        rect.Y0));
                }
            }
        }

        if (frames.Count == 0)
        {
            throw new NotSupportedException(
                $"'{logicalName}' yielded no composited frame from {wanted.Count} of its {sprite.Animations.Count} animation(s).");
        }

        return new DecodedFrames(frames, "embedded (4 layer palettes per animation)", ClassicSpriteGame.Tactics,
            Path.GetFileNameWithoutExtension(logicalName));
    }

    /// <summary>The tile's shared palette as RGB, for the (unseen on retail) palette-less stream.</summary>
    private static Palette TacticsTilePalette(TacticsTileFile tile)
    {
        var rgb = new byte[Palette.RgbByteCount];
        var source = tile.SharedPaletteBgrx.Span;
        var entries = Math.Min(source.Length / 4, Palette.EntryCount);
        for (var i = 0; i < entries; i++)
        {
            rgb[i * 3] = source[i * 4 + 2];
            rgb[i * 3 + 1] = source[i * 4 + 1];
            rgb[i * 3 + 2] = source[i * 4];
        }

        return Palette.FromRgb8(rgb);
    }

    /// <summary>Resolves the <c>--animation</c> selector to animation indices; null selects all.</summary>
    private static IReadOnlyList<int> SelectAnimations(TacticsSpriteFile sprite, string? animation, string logicalName)
    {
        if (string.IsNullOrWhiteSpace(animation))
        {
            return Enumerable.Range(0, sprite.Animations.Count).ToArray();
        }

        var trimmed = animation.Trim();
        if (int.TryParse(trimmed, out var index))
        {
            if (index < 0 || index >= sprite.Animations.Count)
            {
                throw new NotSupportedException(
                    $"'{logicalName}' has {sprite.Animations.Count} animation(s); index {index} is out of range.");
            }

            return [index];
        }

        var matches = new List<int>();
        for (var i = 0; i < sprite.Animations.Count; i++)
        {
            if (sprite.Animations[i].Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(i);
            }
        }

        if (matches.Count == 0)
        {
            throw new NotSupportedException(
                $"'{logicalName}' has no animation whose name contains '{trimmed}'. Names: " +
                string.Join(", ", sprite.Animations.Select(a => a.Name)));
        }

        return matches;
    }

    /// <summary>Makes an animation name safe to put in a file name.</summary>
    private static string SanitiseLabel(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]))
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    /// <summary>
    ///     Every <c>.tex</c> inside a Brotherhood of Steel clump, decoded through the GS layout and
    ///     labelled by the asset name the executable hashes for it (<c>bos_logo</c>,
    ///     <c>2001_ironglove</c>) or, for the ~95% of tags no string names, by <c>tag_XXXXXXXX</c>.
    ///     ⚠ A streamed <c>&lt;level&gt;_T.CLP</c> holds texture PACKS, not single textures, and
    ///     yields nothing here; the resident level clump, <c>HUD/SFX/INVENTRY/INV_SWAP/GLOBAL/ARMOR</c>
    ///     are where the decodable textures live (2,876 across the disc).
    /// </summary>
    private static DecodedFrames DecodeBosClump(byte[] bytes, string logicalName)
    {
        var clump = BosClumpFile.Parse(bytes, logicalName);
        var frames = new List<DecodedFrame>();
        foreach (var section in BosTexture.FindTextureSections(bytes, clump))
        {
            var label = BosKnownAssetNames.NameOrTag(section.Tag);
            var texture = BosTexture.Parse(BosClumpFile.Read(bytes, section), $"{logicalName}:{label}");
            frames.Add(new DecodedFrame(label, texture.ToDecodedTexture(), 0, 0));
        }

        if (frames.Count == 0)
        {
            throw new NotSupportedException(
                $"'{logicalName}' holds no .tex section among its {clump.Sections.Count} " +
                (clump.IsStreamed ? "(streamed texture packs are not single textures)." : "sections."));
        }

        return new DecodedFrames(frames, "embedded (CLUT per texture)", ClassicSpriteGame.BrotherhoodOfSteel,
            Path.GetFileNameWithoutExtension(logicalName));
    }

    /// <summary>One loose Brotherhood of Steel <c>.tex</c>.</summary>
    private static DecodedFrames DecodeBosTexture(byte[] bytes, string logicalName)
    {
        var texture = BosTexture.Parse(bytes, logicalName);
        return new DecodedFrames(
            [new DecodedFrame(string.Empty, texture.ToDecodedTexture(), 0, 0)],
            texture.IsTrueColour ? "none (true colour)" : "embedded (CLUT)",
            ClassicSpriteGame.BrotherhoodOfSteel,
            Path.GetFileNameWithoutExtension(logicalName));
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

        // A <zar> is Fallout Tactics' alone, recognised by its tag. This route reports the index
        // plane the game itself derives (FUN_006f9cd0); the alpha plane it keeps beside that is
        // carried only by the RGBA route in DecodeCore, so 'sprite info' sees the frame geometry
        // while 'sprite render' sees the transparency.
        if (TacticsZarImage.IsZar(bytes) || game == ClassicSpriteGame.Tactics)
        {
            var zar = TacticsZarImage.Parse(bytes, logicalName);
            if (!zar.HasPalette)
            {
                throw new NotSupportedException(
                    $"'{logicalName}' is a <zar> without a palette: it indexes its owning sprite's layer palette, which this route cannot supply.");
            }

            return new Decoded([zar.DecodeIndexed().Indices], null, zar.ToPalette(), null, false);
        }

        // Brotherhood of Steel: 'sprite info' reports each texture's PSMT8 index plane and size;
        // the CLUT and GS alpha are carried only by the RGBA route in DecodeCore.
        if (BosClumpFile.IsClump(bytes))
        {
            var clump = BosClumpFile.Parse(bytes, logicalName);
            var images = new List<IndexedBitmap>();
            var labels = new List<string>();
            foreach (var section in BosTexture.FindTextureSections(bytes, clump))
            {
                var label = BosKnownAssetNames.NameOrTag(section.Tag);
                images.Add(BosTexture.Parse(BosClumpFile.Read(bytes, section), $"{logicalName}:{label}")
                    .ToIndexedBitmap());
                labels.Add(label);
            }

            return new Decoded(images, labels, Palette.FromRgb8(new byte[Palette.RgbByteCount]), null, false);
        }

        if (BosTexture.IsTexture(bytes))
        {
            return new Decoded([BosTexture.Parse(bytes, logicalName).ToIndexedBitmap()], null,
                Palette.FromRgb8(new byte[Palette.RgbByteCount]), null, false);
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

        // A .ztx is Shadowkey's per-zone texture bank and nobody else's, so — like the three
        // above — the extension decides whichever game was resolved.
        if (Path.GetExtension(logicalName).Equals(".ztx", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeZtx(bytes, logicalName);
        }

        // Daggerfall's TEXTURE.nnn sets are named by convention, not extension, and are
        // unambiguous whichever game was resolved.
        if (DaggerfallTextureFile.IsTextureFileName(logicalName))
        {
            var texture = DaggerfallTextureFile.Parse(bytes, logicalName);
            var (images, labels) = Flatten(texture.Records.Select(r => (r.Index, r.Frames)));
            return new Decoded(images, labels, null, ["ART_PAL.COL"], false);
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
        return new Decoded(images, file.Images.Count == 1 ? null : labels, palettes[0], null, false, palettes);
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

        return new Decoded(file.Frames, labels, file.Palette, null, false);
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
            false,
            everyFrameHasOne && palettes.Count == images.Count ? palettes : null);
    }

    /// <summary>
    ///     Shadowkey <c>.ztx</c> texture banks: every wall, floor and sky-plane texture one zone
    ///     uses, each a 128x128 indexed bitmap, behind the size-prefixed zlib envelope the
    ///     <c>z*</c> families share.
    ///     <para>
    ///         ⚠ Unlike the other three extension-decided formats here, a bank carries NO palette
    ///         of its own — it indexes the zone's <c>&lt;stem&gt;.pal</c>, a plain 768-byte 8-bit
    ///         table beside it. That name is offered as the preferred palette so the pipeline's
    ///         existing resolution finds it in a directory or an archive alike.
    ///     </para>
    ///     <para>
    ///         ⛔ No transparency claim is made. Magenta <c>0x0F0F</c> is the colour key in
    ///         Shadowkey's sprites and mesh skins, but whether a wall texture uses it is not
    ///         established, and index 0 is not known to be transparent here.
    ///     </para>
    /// </summary>
    private static Decoded DecodeZtx(byte[] bytes, string logicalName)
    {
        var bank = ShadowkeyTextureBank.Parse(
            ShadowkeyCompressedFile.Inflate(bytes, logicalName), logicalName);

        var labels = bank.Count == 1
            ? null
            : Enumerable.Range(0, bank.Count).Select(static i => $"t{i:D2}").ToList();

        var stem = Path.GetFileNameWithoutExtension(logicalName);
        return new Decoded(
            bank.Textures, labels, null, [stem + ".pal"], false);
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
        return new Decoded([image.Bitmap], null, image.Palette, null, false);
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
            images, images.Count == 1 ? null : labels, null, FalloutPalette.CandidatesFor(logicalName), true);
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
                return new Decoded(result.Images, null, result.EmbeddedPalette, null, false);
            case ".cif":
                return new Decoded(ArenaCifDecoder.Decode(bytes, logicalName), null, null, null, true);
            case ".dfa":
                return new Decoded(ArenaDfaDecoder.Decode(bytes), null, null, null, true);
            case ".cfa":
                return new Decoded(ArenaCfaDecoder.Decode(bytes, logicalName), null, null, null, true);
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
                labels.Add(
                    $"{(i < DaggerfallSkyFile.FramesPerHalf ? 'w' : 'e')}{i % DaggerfallSkyFile.FramesPerHalf:D2}");
                palettes.Add(sky.PaletteFor(i));
            }

            return new Decoded(sky.Frames, labels, null, null, false, palettes);
        }

        if (DaggerfallImgFile.IsImgFileName(logicalName))
        {
            var img = DaggerfallImgFile.Parse(bytes, logicalName);

            // Six fullscreens carry their own palette; everything else names an external one
            // (ART_PAL.COL for most, FMAP_PAL/NIGHTSKY/DANKBMAP/MAP.PAL for the special screens).
            var preferred = img.EmbeddedPalette is null ? DaggerfallImgFile.GetPaletteFileName(logicalName) : null;
            return new Decoded(
                [img.Bitmap], null, img.EmbeddedPalette, preferred is null ? null : [preferred], false);
        }

        if (DaggerfallCifRciFile.IsCifRciFileName(logicalName))
        {
            var cif = DaggerfallCifRciFile.Parse(bytes, logicalName);
            var (images, labels) = Flatten(cif.Records.Select(r => (r.Index, r.Frames)));

            // CIF/RCI hold sprites, portraits and weapon frames: index 0 is the keyed-out background.
            return new Decoded(images, labels, null, [DaggerfallCifRciFile.PaletteName], true);
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
    private static (Palette Palette, string Source) ResolveViaLookup(PaletteLookup lookup,
        IReadOnlyList<string> candidates)
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
    private static (Palette Palette, string Source)? TryLoadPaletteFromArchive(string archivePath,
        IReadOnlyList<string> candidates)
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
    private static (Palette Palette, string Source)? TryLoadPaletteFromSiblings(string archivePath,
        IReadOnlyList<string> candidates)
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

    internal sealed record RenderedFrame(string Path, int Width, int Height, int XOffset, int YOffset);

    internal sealed record RenderResult(
        IReadOnlyList<RenderedFrame> Frames,
        string PaletteSource,
        ClassicSpriteGame Game);

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
        IReadOnlyList<DecodedFrame> Frames,
        string PaletteSource,
        ClassicSpriteGame Game,
        string BaseName);

    /// <summary>One row of <c>sprite info</c>: what a format calls a frame, and its geometry.</summary>
    internal sealed record FrameInfo(string Label, int Width, int Height, int XOffset, int YOffset, string Detail);
}

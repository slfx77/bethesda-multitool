using System.CommandLine;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Semantic;
using ImageMagick;
using ImageMagick.Drawing;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Renders map marker overlay PNGs from DMP memory dumps using Magick.NET.
///     Produces the same visual output as the GUI's World Map export, but headless.
///     Optionally composites all frames into an animated GIF.
/// </summary>
public static class DmpMapRenderCommands
{
    private const int DefaultLongEdge = 4096;
    private const float PaddingFraction = 0.05f;

    public static Command CreateRenderMapCommand()
    {
        var command = new Command("render-map",
            "Render map marker overlay PNGs from DMP memory dumps");

        var dirArg = new Argument<string>("directory") { Description = "Directory containing .dmp files" };
        command.Arguments.Add(dirArg);

        var outputOption = new Option<string?>("-o", "--output")
        { Description = "Output directory (default: {directory}/maps/)" };
        command.Options.Add(outputOption);

        var sizeOption = new Option<int>("--size")
        { Description = "Long edge in pixels (default: 4096)", DefaultValueFactory = _ => DefaultLongEdge };
        command.Options.Add(sizeOption);

        var schemeOption = new Option<string>("--scheme")
        { Description = "Color scheme: amber, green, white, blue, red (default: amber)", DefaultValueFactory = _ => "amber" };
        command.Options.Add(schemeOption);

        var gifDelayOption = new Option<int>("--gif-delay")
        { Description = "GIF frame delay in 1/100ths of a second (default: 100 = 1s per frame)", DefaultValueFactory = _ => 100 };
        command.Options.Add(gifDelayOption);

        var fo3EsmOption = new Option<string?>("--fo3-esm")
        { Description = "Path to Fallout3.esm — markers from this ESM are excluded from output" };
        command.Options.Add(fo3EsmOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var dir = parseResult.GetValue(dirArg)!;
            var output = parseResult.GetValue(outputOption);
            var size = parseResult.GetValue(sizeOption);
            var scheme = parseResult.GetValue(schemeOption)!;
            var gifDelay = parseResult.GetValue(gifDelayOption);
            var fo3Esm = parseResult.GetValue(fo3EsmOption);
            await RunAsync(dir, output, size, scheme, gifDelay, fo3Esm, cancellationToken);
        });

        return command;
    }

    private sealed record FrameData(
        string FileName, string Stem, List<PlacedReference> Markers, DateTime? ModuleTimestamp);

    private static async Task RunAsync(string dirPath, string? outputDir, int longEdge, string schemeName,
        int gifDelay, string? fo3EsmPath, CancellationToken cancellationToken)
    {
        // If the argument is an ESM file, render its map markers directly
        if (File.Exists(dirPath) && dirPath.EndsWith(".esm", StringComparison.OrdinalIgnoreCase))
        {
            await RunEsmAsync(dirPath, outputDir, longEdge, schemeName, cancellationToken);
            return;
        }

        if (!Directory.Exists(dirPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Directory not found: {dirPath}");
            return;
        }

        var dmpFiles = Directory.GetFiles(dirPath, "*.dmp")
            .Where(f => !Path.GetFileName(f).Contains("hangdump", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
            .ToList();

        if (dmpFiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .dmp files found in:[/] {dirPath}");
            return;
        }

        outputDir ??= Path.Combine(dirPath, "maps");
        Directory.CreateDirectory(outputDir);

        var scheme = ParseScheme(schemeName);

        // Load Fallout 3 marker exclusion set (if ESM path provided)
        var fo3MarkerFormIds = LoadFo3MarkerFormIds(fo3EsmPath);

        AnsiConsole.MarkupLine($"[blue]Rendering map markers from {dmpFiles.Count} DMP files...[/]");
        AnsiConsole.MarkupLine($"  Output: [cyan]{Markup.Escape(outputDir)}[/]");
        AnsiConsole.MarkupLine($"  Size: [cyan]{longEdge}px[/] long edge");
        AnsiConsole.MarkupLine($"  Scheme: [cyan]{scheme.Name}[/]");
        if (fo3MarkerFormIds.Count > 0)
        {
            AnsiConsole.MarkupLine($"  FO3 exclusion: [cyan]{fo3MarkerFormIds.Count}[/] markers from Fallout3.esm");
        }

        AnsiConsole.WriteLine();

        // Phase 1: Process all DMPs and collect markers
        var frameData = new List<FrameData>();
        var skipped = 0;

        foreach (var dmpFile in dmpFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(dmpFile);
            try
            {
                using var loaded = await SemanticFileLoader.LoadAsync(
                    dmpFile,
                    new SemanticFileLoadOptions { FileType = AnalysisFileType.Minidump },
                    cancellationToken);

                var markers = loaded.Records.MapMarkers
                    .Where(m => !fo3MarkerFormIds.Contains(m.FormId))
                    .ToList();

                if (markers.Count == 0)
                {
                    AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(fileName)}: no markers, skipping[/]");
                    skipped++;
                    continue;
                }

                DateTime? moduleTimestamp = null;
                var gameModule = loaded.RawResult?.MinidumpInfo?.FindGameModule();
                if (gameModule != null && gameModule.TimeDateStamp != 0)
                {
                    moduleTimestamp = DateTimeOffset.FromUnixTimeSeconds(gameModule.TimeDateStamp).UtcDateTime;
                }

                var stem = Path.GetFileNameWithoutExtension(fileName);
                frameData.Add(new FrameData(fileName, stem, markers, moduleTimestamp));
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]{Markup.Escape(fileName)}: {Markup.Escape(ex.Message)}[/]");
            }
        }

        if (frameData.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No DMP files contained map markers.[/]");
            return;
        }

        // Sort frames by module timestamp (chronological order for GIF timeline)
        frameData.Sort((a, b) =>
        {
            if (a.ModuleTimestamp == null && b.ModuleTimestamp == null) return 0;
            if (a.ModuleTimestamp == null) return 1;
            if (b.ModuleTimestamp == null) return -1;
            return a.ModuleTimestamp.Value.CompareTo(b.ModuleTimestamp.Value);
        });

        // Phase 2: Compute shared world bounds across ALL frames for consistent coordinates
        var allMarkers = frameData.SelectMany(f => f.Markers).ToList();
        var bounds = ComputeWorldBounds(allMarkers);

        // Phase 3: Render individual PNGs
        AnsiConsole.WriteLine();
        var rendered = 0;

        foreach (var frame in frameData)
        {
            var outputPath = Path.Combine(outputDir, $"{frame.Stem}.markers.png");
            var title = FormatFrameTitle(frame);
            RenderMarkerMap(frame.Markers, outputPath, longEdge, scheme, bounds, title);
            rendered++;

            var tsLabel = frame.ModuleTimestamp?.ToString("yyyy-MM-dd") ?? "no timestamp";
            AnsiConsole.MarkupLine(
                $"  [green]{Markup.Escape(frame.FileName)}[/] ({tsLabel}): {frame.Markers.Count} markers → [cyan]{Markup.Escape(Path.GetFileName(outputPath))}[/]");
        }

        // Phase 4: Composite animated GIF from all frames (already sorted by timestamp)
        if (frameData.Count >= 2)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue]Compositing animated GIF...[/]");

            var gifPath = Path.Combine(outputDir, "markers_timeline.gif");
            CompositeGif(frameData, gifPath, longEdge, scheme, bounds, gifDelay);

            var gifSize = new FileInfo(gifPath).Length;
            AnsiConsole.MarkupLine(
                $"  [green]GIF:[/] {frameData.Count} frames → [cyan]{Markup.Escape(Path.GetFileName(gifPath))}[/] ({gifSize / 1024:N0} KB)");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Done:[/] {rendered} images rendered, {skipped} skipped (no markers)");

        await Task.CompletedTask;
    }

    private static async Task RunEsmAsync(string esmPath, string? outputDir, int longEdge, string schemeName,
        CancellationToken cancellationToken)
    {
        var scheme = ParseScheme(schemeName);
        outputDir ??= Path.Combine(Path.GetDirectoryName(esmPath)!, "maps");
        Directory.CreateDirectory(outputDir);

        var stem = Path.GetFileNameWithoutExtension(esmPath);
        AnsiConsole.MarkupLine($"[blue]Rendering map markers from ESM:[/] {Markup.Escape(Path.GetFileName(esmPath))}");
        AnsiConsole.MarkupLine($"  Output: [cyan]{Markup.Escape(outputDir)}[/]");
        AnsiConsole.MarkupLine($"  Size: [cyan]{longEdge}px[/] long edge");
        AnsiConsole.MarkupLine($"  Scheme: [cyan]{scheme.Name}[/]");
        AnsiConsole.WriteLine();

        try
        {
            using var loaded = await SemanticFileLoader.LoadAsync(
                esmPath,
                new SemanticFileLoadOptions { FileType = AnalysisFileType.EsmFile },
                cancellationToken);

            var markers = loaded.Records.MapMarkers.ToList();

            if (markers.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No map markers found in ESM.[/]");
                return;
            }

            AnsiConsole.MarkupLine($"  Found [cyan]{markers.Count}[/] map markers");

            var bounds = ComputeWorldBounds(markers);
            var outputPath = Path.Combine(outputDir, $"{stem}.markers.png");
            var title = $"{stem}\n{markers.Count} markers from ESM";
            RenderMarkerMap(markers, outputPath, longEdge, scheme, bounds, title);

            AnsiConsole.MarkupLine($"  [green]Rendered:[/] [cyan]{Markup.Escape(Path.GetFileName(outputPath))}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] {Markup.Escape(ex.Message)}");
        }

        await Task.CompletedTask;
    }

    private static string FormatFrameTitle(FrameData frame)
    {
        if (frame.ModuleTimestamp.HasValue)
        {
            return $"{frame.Stem}\n{frame.ModuleTimestamp.Value:yyyy-MM-dd HH:mm} UTC";
        }

        return frame.Stem;
    }

    /// <summary>
    ///     Scan Fallout3.esm for map marker REFR FormIDs.
    ///     These are excluded from rendered maps to avoid FO3 data polluting FNV maps.
    /// </summary>
    private static HashSet<uint> LoadFo3MarkerFormIds(string? fo3EsmPath)
    {
        if (string.IsNullOrEmpty(fo3EsmPath) || !File.Exists(fo3EsmPath))
        {
            return [];
        }

        try
        {
            using var loaded = SemanticFileLoader.LoadAsync(
                fo3EsmPath,
                new SemanticFileLoadOptions { FileType = AnalysisFileType.EsmFile }).GetAwaiter().GetResult();

            return loaded.Records.MapMarkers.Select(m => m.FormId).ToHashSet();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [yellow]Warning: could not parse FO3 ESM: {Markup.Escape(ex.Message)}[/]");
            return [];
        }
    }

    // ========================================================================
    // World Bounds
    // ========================================================================

    private sealed record WorldBounds(float MinX, float MaxX, float MinY, float MaxY, float WorldW, float WorldH);

    private static WorldBounds ComputeWorldBounds(List<PlacedReference> allMarkers)
    {
        var minX = allMarkers.Min(m => m.X);
        var maxX = allMarkers.Max(m => m.X);
        var minY = allMarkers.Min(m => m.Y);
        var maxY = allMarkers.Max(m => m.Y);

        var worldW = maxX - minX;
        var worldH = maxY - minY;

        if (worldW < 1f)
        {
            worldW = 4096f;
        }

        if (worldH < 1f)
        {
            worldH = 4096f;
        }

        var padX = worldW * PaddingFraction;
        var padY = worldH * PaddingFraction;
        minX -= padX;
        maxX += padX;
        minY -= padY;
        maxY += padY;

        return new WorldBounds(minX, maxX, minY, maxY, maxX - minX, maxY - minY);
    }

    // ========================================================================
    // Single Frame Rendering
    // ========================================================================

    private static void RenderMarkerMap(List<PlacedReference> markers, string outputPath, int longEdge,
        SchemeColor scheme, WorldBounds bounds, string frameTitle)
    {
        var (imageW, imageH, pixelsPerUnit) = MapExportLayoutEngine.ComputeImageSize(
            bounds.WorldW, bounds.WorldH, longEdge);
        var sizing = MapExportLayoutEngine.ComputeSizing(longEdge);

        using var image = CreateBackgroundImage(imageW, imageH, scheme);
        DrawMarkersAndLabels(image, markers, bounds, pixelsPerUnit, imageW, imageH, sizing, scheme, frameTitle);
        image.Write(outputPath, MagickFormat.Png);
    }

    private static void DrawMarkersAndLabels(MagickImage image, List<PlacedReference> markers,
        WorldBounds bounds, float pixelsPerUnit, int imageW, int imageH,
        MapExportSizing sizing, SchemeColor scheme, string frameTitle)
    {
        // Project markers to engine input (append world coordinates as second label line)
        var inputs = markers
            .Select(m =>
            {
                var name = m.MarkerName;
                if (!string.IsNullOrEmpty(name))
                {
                    var x = (int)MathF.Round(m.X);
                    var y = (int)MathF.Round(m.Y);
                    name = $"{name}\n({x}, {y})";
                }

                return new MapMarkerInput(m.X, m.Y, m.MarkerType, name);
            })
            .ToList();

        // Compute layout via shared engine
        var layout = MapExportLayoutEngine.ComputeLayout(
            inputs, imageW, imageH,
            bounds.MinX, bounds.MaxX, bounds.MinY, bounds.MaxY,
            pixelsPerUnit, sizing,
            (text, fontSize) =>
            {
                var lines = text.Split('\n');
                var maxWidth = 0f;
                foreach (var line in lines)
                    maxWidth = MathF.Max(maxWidth, line.Length * fontSize * 0.55f);
                return (maxWidth, lines.Length * fontSize * 1.4f);
            });

        // Draw grid lines
        var gridColor = MagickColor.FromRgba(255, 255, 255, 40);
        var gridWidth = Math.Max(0.5, (double)sizing.OutlineWidth * 0.5);

        foreach (var line in layout.GridLines)
        {
            new Drawables()
                .StrokeColor(gridColor)
                .StrokeWidth(gridWidth)
                .FillColor(MagickColors.Transparent)
                .Line(line.X1, line.Y1, line.X2, line.Y2)
                .Draw(image);
        }

        // Draw cell coordinate labels at each cell's top-left corner
        const float cellWorldSize = 4096f;
        var cellLabelFontSize = (double)(sizing.LabelFontSize * 0.6);
        var cellPixelSize = cellWorldSize * pixelsPerUnit;

        if (cellPixelSize >= cellLabelFontSize * 4)
        {
            var cellLabelColor = MagickColor.FromRgba(255, 255, 255, 100);
            var minCellX = (int)MathF.Floor(bounds.MinX / cellWorldSize);
            var maxCellX = (int)MathF.Ceiling(bounds.MaxX / cellWorldSize);
            var minCellY = (int)MathF.Floor(bounds.MinY / cellWorldSize);
            var maxCellY = (int)MathF.Ceiling(bounds.MaxY / cellWorldSize);

            for (var cy = minCellY; cy <= maxCellY; cy++)
            {
                for (var cx = minCellX; cx <= maxCellX; cx++)
                {
                    // Top-left corner: left edge = cx * 4096, top edge = (cy+1) * 4096
                    var (px, py) = MapExportLayoutEngine.WorldToPixel(
                        cx * cellWorldSize, (cy + 1) * cellWorldSize,
                        bounds.MinX, bounds.MaxY, pixelsPerUnit);

                    var inset = cellLabelFontSize * 0.3;
                    var drawX = (double)px + inset;
                    var drawY = (double)py + inset + cellLabelFontSize;

                    if (drawX < 0 || drawX > imageW || drawY < 0 || drawY > imageH)
                        continue;

                    new Drawables()
                        .Font("Consolas")
                        .FontPointSize(cellLabelFontSize)
                        .FillColor(cellLabelColor)
                        .StrokeColor(MagickColors.Transparent)
                        .TextAlignment(TextAlignment.Left)
                        .Text(drawX, drawY, $"{cx},{cy}")
                        .Draw(image);
                }
            }
        }

        // Draw marker icons tinted to scheme color (fallback: colored circle + glyph)
        var iconSize = (uint)(sizing.MarkerRadius * 2);
        var glyphFontSize = (double)sizing.MarkerRadius * 1.2;
        var tintColor = MagickColor.FromRgb(scheme.R, scheme.G, scheme.B);

        foreach (var m in layout.Markers)
        {
            var iconPng = m.Type.HasValue ? MapMarkerIconProvider.GetIconPng(m.Type.Value) : null;

            if (iconPng != null)
            {
                using var icon = new MagickImage(iconPng);
                icon.Resize(iconSize, iconSize);

                // Tint white icon to scheme color (multiply RGB channels)
                icon.Evaluate(Channels.Red, EvaluateOperator.Multiply, scheme.R / 255.0);
                icon.Evaluate(Channels.Green, EvaluateOperator.Multiply, scheme.G / 255.0);
                icon.Evaluate(Channels.Blue, EvaluateOperator.Multiply, scheme.B / 255.0);

                var x = (int)(m.PixelX - iconSize / 2.0);
                var y = (int)(m.PixelY - iconSize / 2.0);
                image.Composite(icon, x, y, CompositeOperator.Over);
            }
            else
            {
                // Fallback: colored circle + glyph for unmapped marker types
                new Drawables()
                    .StrokeColor(MagickColors.Transparent)
                    .FillColor(MagickColor.FromRgba(m.ColorR, m.ColorG, m.ColorB, 200))
                    .Circle(m.PixelX, m.PixelY, m.PixelX + sizing.MarkerRadius, m.PixelY)
                    .Draw(image);

                new Drawables()
                    .StrokeColor(MagickColors.White)
                    .StrokeWidth(sizing.OutlineWidth)
                    .FillColor(MagickColors.Transparent)
                    .Circle(m.PixelX, m.PixelY, m.PixelX + sizing.MarkerRadius, m.PixelY)
                    .Draw(image);

                new Drawables()
                    .Font("Segoe MDL2 Assets")
                    .FontPointSize(glyphFontSize)
                    .FillColor(MagickColors.White)
                    .StrokeColor(MagickColors.Transparent)
                    .TextAlignment(TextAlignment.Center)
                    .Gravity(Gravity.Undefined)
                    .Text(m.PixelX, m.PixelY + glyphFontSize * 0.35, m.Glyph)
                    .Draw(image);
            }
        }

        // Draw leader lines (behind labels)
        var leaderWidth = Math.Max(1.0, (double)sizing.MarkerRadius * 0.1);

        foreach (var lp in layout.Labels)
        {
            if (!lp.NeedsLeader)
            {
                continue;
            }

            var labelCenterX = (double)lp.LabelX + lp.PillWidth / 2;
            var labelCenterY = (double)lp.LabelY + lp.PillHeight / 2;
            var dx = labelCenterX - lp.MarkerPixelX;
            var dy = labelCenterY - lp.MarkerPixelY;
            var len = Math.Sqrt(dx * dx + dy * dy);

            if (len > 0)
            {
                var startX = lp.MarkerPixelX + dx / len * (sizing.MarkerRadius + 1);
                var startY = lp.MarkerPixelY + dy / len * (sizing.MarkerRadius + 1);

                new Drawables()
                    .StrokeColor(MagickColor.FromRgba(255, 255, 255, 150))
                    .StrokeWidth(leaderWidth)
                    .FillColor(MagickColors.Transparent)
                    .Line(startX, startY, labelCenterX, labelCenterY)
                    .Draw(image);
            }
        }

        // Draw label pills + multi-line text
        foreach (var lp in layout.Labels)
        {
            new Drawables()
                .StrokeColor(MagickColor.FromRgba(255, 255, 255, 80))
                .StrokeWidth(0.5)
                .FillColor(MagickColor.FromRgba(0, 0, 0, 220))
                .RoundRectangle(lp.LabelX, lp.LabelY,
                    lp.LabelX + lp.PillWidth, lp.LabelY + lp.PillHeight, 3, 3)
                .Draw(image);

            var lines = lp.Text.Split('\n');
            var lineHeight = sizing.LabelFontSize * 1.4f;
            for (var i = 0; i < lines.Length; i++)
            {
                new Drawables()
                    .Font("Segoe UI")
                    .FontPointSize(sizing.LabelFontSize)
                    .FillColor(MagickColors.White)
                    .StrokeColor(MagickColors.Transparent)
                    .TextAlignment(TextAlignment.Center)
                    .Text(lp.LabelX + lp.PillWidth / 2,
                        lp.LabelY + lp.PadV + lineHeight * i + lineHeight * 0.8,
                        lines[i])
                    .Draw(image);
            }
        }

        // Frame title at top (CLI-only) — supports multi-line titles (name + timestamp)
        var titleFontSize = sizing.LabelFontSize * 1.5;
        var titleColor = MagickColor.FromRgb(scheme.R, scheme.G, scheme.B);
        var titleLines = frameTitle.Split('\n');
        var titleX = (double)sizing.LabelFontSize;
        var titleY = titleFontSize * 1.3;

        // First line: DMP name + marker count
        new Drawables()
            .Font("Segoe UI")
            .FontPointSize(titleFontSize)
            .FillColor(titleColor)
            .StrokeColor(MagickColors.Transparent)
            .TextAlignment(TextAlignment.Left)
            .Text(titleX, titleY, $"{titleLines[0]}  —  {markers.Count} markers")
            .Draw(image);

        // Second line: timestamp (smaller, dimmer)
        if (titleLines.Length > 1)
        {
            var subFontSize = titleFontSize * 0.7;
            var dimColor = MagickColor.FromRgba(scheme.R, scheme.G, scheme.B, 160);
            new Drawables()
                .Font("Segoe UI")
                .FontPointSize(subFontSize)
                .FillColor(dimColor)
                .StrokeColor(MagickColors.Transparent)
                .TextAlignment(TextAlignment.Left)
                .Text(titleX, titleY + titleFontSize * 1.2, titleLines[1])
                .Draw(image);
        }
    }

    // ========================================================================
    // Animated GIF Compositing
    // ========================================================================

    private static void CompositeGif(
        List<FrameData> frames,
        string gifPath, int longEdge, SchemeColor scheme, WorldBounds bounds, int gifDelay)
    {
        // Use smaller dimensions for GIF to keep file size manageable
        var gifLongEdge = Math.Min(longEdge, 1024);
        var (imageW, imageH, pixelsPerUnit) = MapExportLayoutEngine.ComputeImageSize(
            bounds.WorldW, bounds.WorldH, gifLongEdge);
        var sizing = MapExportLayoutEngine.ComputeSizing(gifLongEdge);

        using var collection = new MagickImageCollection();

        foreach (var fd in frames)
        {
            var title = FormatFrameTitle(fd);
            var frame = CreateBackgroundImage(imageW, imageH, scheme);
            DrawMarkersAndLabels(frame, fd.Markers, bounds, pixelsPerUnit, imageW, imageH, sizing, scheme, title);

            frame.AnimationDelay = (uint)gifDelay;
            frame.GifDisposeMethod = GifDisposeMethod.Background;
            collection.Add(frame);
        }

        // Optimize and write
        collection.Optimize();
        collection.Write(gifPath);
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static MagickImage CreateBackgroundImage(int imageW, int imageH, SchemeColor scheme)
    {
        var bgR = (byte)(20 + scheme.R * 0.03);
        var bgG = (byte)(20 + scheme.G * 0.03);
        var bgB = (byte)(25 + scheme.B * 0.03);
        return new MagickImage(MagickColor.FromRgb(bgR, bgG, bgB), (uint)imageW, (uint)imageH);
    }

    private static MapMarkerType? ToMarkerType(ushort? value) =>
        value.HasValue && Enum.IsDefined(typeof(MapMarkerType), value.Value)
            ? (MapMarkerType)value.Value
            : null;

    private sealed record SchemeColor(string Name, byte R, byte G, byte B);

    private static SchemeColor ParseScheme(string schemeName) =>
        schemeName.ToLowerInvariant() switch
        {
            "green" => new SchemeColor("Green", 26, 255, 128),
            "white" => new SchemeColor("White", 255, 255, 255),
            "blue" => new SchemeColor("Blue", 100, 180, 255),
            "red" => new SchemeColor("Red", 255, 67, 42),
            _ => new SchemeColor("Amber", 255, 182, 66)
        };
}

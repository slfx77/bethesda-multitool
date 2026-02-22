using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
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

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var dir = parseResult.GetValue(dirArg)!;
            var output = parseResult.GetValue(outputOption);
            var size = parseResult.GetValue(sizeOption);
            var scheme = parseResult.GetValue(schemeOption)!;
            var gifDelay = parseResult.GetValue(gifDelayOption);
            await RunAsync(dir, output, size, scheme, gifDelay, cancellationToken);
        });

        return command;
    }

    private static async Task RunAsync(string dirPath, string? outputDir, int longEdge, string schemeName,
        int gifDelay, CancellationToken cancellationToken)
    {
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

        AnsiConsole.MarkupLine($"[blue]Rendering map markers from {dmpFiles.Count} DMP files...[/]");
        AnsiConsole.MarkupLine($"  Output: [cyan]{Markup.Escape(outputDir)}[/]");
        AnsiConsole.MarkupLine($"  Size: [cyan]{longEdge}px[/] long edge");
        AnsiConsole.MarkupLine($"  Scheme: [cyan]{scheme.Name}[/]");
        AnsiConsole.WriteLine();

        // Phase 1: Process all DMPs and collect markers
        var frameData = new List<(string FileName, string Stem, List<ExtractedRefrRecord> Markers)>();
        var skipped = 0;

        foreach (var dmpFile in dmpFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(dmpFile);
            try
            {
                var result = DmpDiagCommands.ProcessDmp(dmpFile);
                var markers = result.Markers
                    .Where(m => m.Position != null)
                    .ToList();

                if (markers.Count == 0)
                {
                    AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(fileName)}: no markers, skipping[/]");
                    skipped++;
                    continue;
                }

                var stem = Path.GetFileNameWithoutExtension(fileName);
                frameData.Add((fileName, stem, markers));
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

        // Phase 2: Compute shared world bounds across ALL frames for consistent coordinates
        var allMarkers = frameData.SelectMany(f => f.Markers).ToList();
        var bounds = ComputeWorldBounds(allMarkers);

        // Phase 3: Render individual PNGs
        AnsiConsole.WriteLine();
        var rendered = 0;

        foreach (var (fileName, stem, markers) in frameData)
        {
            var outputPath = Path.Combine(outputDir, $"{stem}.markers.png");
            RenderMarkerMap(markers, outputPath, longEdge, scheme, bounds, stem);
            rendered++;

            AnsiConsole.MarkupLine(
                $"  [green]{Markup.Escape(fileName)}[/]: {markers.Count} markers → [cyan]{Markup.Escape(Path.GetFileName(outputPath))}[/]");
        }

        // Phase 4: Composite animated GIF from all frames
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

    // ========================================================================
    // World Bounds
    // ========================================================================

    private record WorldBounds(float MinX, float MaxX, float MinY, float MaxY, float WorldW, float WorldH);

    private static WorldBounds ComputeWorldBounds(List<ExtractedRefrRecord> allMarkers)
    {
        var minX = allMarkers.Min(m => m.Position!.X);
        var maxX = allMarkers.Max(m => m.Position!.X);
        var minY = allMarkers.Min(m => m.Position!.Y);
        var maxY = allMarkers.Max(m => m.Position!.Y);

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

    private static void RenderMarkerMap(List<ExtractedRefrRecord> markers, string outputPath, int longEdge,
        SchemeColor scheme, WorldBounds bounds, string frameTitle)
    {
        var (imageW, imageH, pixelsPerUnit) = MapExportLayoutEngine.ComputeImageSize(
            bounds.WorldW, bounds.WorldH, longEdge);
        var sizing = MapExportLayoutEngine.ComputeSizing(longEdge);

        using var image = CreateBackgroundImage(imageW, imageH, scheme);
        DrawMarkersAndLabels(image, markers, bounds, pixelsPerUnit, imageW, imageH, sizing, scheme, frameTitle);
        image.Write(outputPath, MagickFormat.Png);
    }

    private static void DrawMarkersAndLabels(MagickImage image, List<ExtractedRefrRecord> markers,
        WorldBounds bounds, float pixelsPerUnit, int imageW, int imageH,
        MapExportSizing sizing, SchemeColor scheme, string frameTitle)
    {
        // Project markers to engine input
        var inputs = markers
            .Select(m => new MapMarkerInput(m.Position!.X, m.Position.Y,
                ToMarkerType(m.MarkerType), m.MarkerName))
            .ToList();

        // Compute layout via shared engine
        var layout = MapExportLayoutEngine.ComputeLayout(
            inputs, imageW, imageH,
            bounds.MinX, bounds.MaxX, bounds.MinY, bounds.MaxY,
            pixelsPerUnit, sizing,
            (text, fontSize) => (text.Length * fontSize * 0.55f, fontSize * 1.4f));

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

        // Draw marker circles + glyphs
        var glyphFontSize = (double)sizing.MarkerRadius * 1.2;

        foreach (var m in layout.Markers)
        {
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

        // Draw label pills + text
        foreach (var lp in layout.Labels)
        {
            new Drawables()
                .StrokeColor(MagickColor.FromRgba(255, 255, 255, 80))
                .StrokeWidth(0.5)
                .FillColor(MagickColor.FromRgba(0, 0, 0, 220))
                .RoundRectangle(lp.LabelX, lp.LabelY,
                    lp.LabelX + lp.PillWidth, lp.LabelY + lp.PillHeight, 3, 3)
                .Draw(image);

            new Drawables()
                .Font("Segoe UI")
                .FontPointSize(sizing.LabelFontSize)
                .FillColor(MagickColors.White)
                .StrokeColor(MagickColors.Transparent)
                .TextAlignment(TextAlignment.Left)
                .Text(lp.LabelX + lp.PadH,
                    lp.LabelY + lp.PadV + lp.TextHeight * 0.8, lp.Text)
                .Draw(image);
        }

        // Frame title at top (CLI-only)
        var title = $"{frameTitle}  —  {markers.Count} markers";
        new Drawables()
            .Font("Segoe UI")
            .FontPointSize(sizing.LabelFontSize * 1.5)
            .FillColor(MagickColor.FromRgb(scheme.R, scheme.G, scheme.B))
            .StrokeColor(MagickColors.Transparent)
            .TextAlignment(TextAlignment.Left)
            .Text(sizing.LabelFontSize, sizing.LabelFontSize * 2, title)
            .Draw(image);
    }

    // ========================================================================
    // Animated GIF Compositing
    // ========================================================================

    private static void CompositeGif(
        List<(string FileName, string Stem, List<ExtractedRefrRecord> Markers)> frameData,
        string gifPath, int longEdge, SchemeColor scheme, WorldBounds bounds, int gifDelay)
    {
        // Use smaller dimensions for GIF to keep file size manageable
        var gifLongEdge = Math.Min(longEdge, 1024);
        var (imageW, imageH, pixelsPerUnit) = MapExportLayoutEngine.ComputeImageSize(
            bounds.WorldW, bounds.WorldH, gifLongEdge);
        var sizing = MapExportLayoutEngine.ComputeSizing(gifLongEdge);

        using var collection = new MagickImageCollection();

        foreach (var (_, stem, markers) in frameData)
        {
            var frame = CreateBackgroundImage(imageW, imageH, scheme);
            DrawMarkersAndLabels(frame, markers, bounds, pixelsPerUnit, imageW, imageH, sizing, scheme, stem);

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

    private record SchemeColor(string Name, byte R, byte G, byte B);

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

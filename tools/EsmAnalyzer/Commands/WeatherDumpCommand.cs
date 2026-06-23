using System.CommandLine;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Diagnostic harness for the viewer atmosphere (plan item 3F). Dumps every WTHR record's NAM0
///     color categories at all four time bands (Sunrise/Day/Sunset/Night) labelled by
///     <see cref="WeatherColorType" />, then runs the production <see cref="AtmosphereState.Resolve" />
///     across the clock so we can see exactly which sky/ambient/sun color the renderer derives at each
///     hour — the ground truth for diagnosing "horizon black at noon / white at night" style inversions.
/// </summary>
internal static class WeatherDumpCommand
{
    internal static Command Create()
    {
        var command = new Command("weather-dump",
            "Dump WTHR NAM0 color bands per category plus the resolved sky/ambient/sun atmosphere at each hour of the day");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var filterOpt = new Option<string?>("--filter", "-f")
        {
            Description = "Only dump weathers whose EditorID contains this substring (case-insensitive)"
        };

        command.Arguments.Add(fileArg);
        command.Options.Add(filterOpt);
        command.SetAction(parseResult => Execute(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(filterOpt)));

        return command;
    }

    private static int Execute(string filePath, string? filter)
    {
        using var result = UnifiedAnalyzer.AnalyzeAsync(filePath).GetAwaiter().GetResult();
        var weathers = result.Records.Weather;
        var climates = result.Records.Climate;

        var selected = string.IsNullOrEmpty(filter)
            ? weathers
            : weathers.Where(w => (w.EditorId ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        AnsiConsole.MarkupLine($"[cyan]File:[/] {Path.GetFileName(filePath)}  " +
                               $"[cyan]WTHR:[/] {weathers.Count}  [cyan]CLMT:[/] {climates.Count}  " +
                               $"[cyan]matched:[/] {selected.Count}");
        AnsiConsole.WriteLine();

        foreach (var w in selected)
        {
            DumpWeather(w, climates);
            AnsiConsole.WriteLine();
        }

        return 0;
    }

    private static void DumpWeather(WeatherRecord w, IReadOnlyList<ClimateRecord> climates)
    {
        AnsiConsole.MarkupLine($"[green]WTHR 0x{w.FormId:X8}[/] {Markup.Escape(w.EditorId ?? "(no EDID)")}  " +
                               $"colors={w.Colors.Count} fog={w.FogDistances.Count} clouds={w.CloudLayerTextures.Count}");

        if (w.Colors.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Idx")
                .AddColumn("Category")
                .AddColumn("Sunrise")
                .AddColumn("Day")
                .AddColumn("Sunset")
                .AddColumn("Night");

            for (var i = 0; i < w.Colors.Count; i++)
            {
                var c = w.Colors[i];
                var name = Enum.IsDefined(typeof(WeatherColorType), i) ? ((WeatherColorType)i).ToString() : "?";
                _ = table.AddRow(
                    i.ToString(),
                    name,
                    Cell(c.Sunrise),
                    Cell(c.Day),
                    Cell(c.Sunset),
                    Cell(c.Night));
            }

            AnsiConsole.Write(table);
        }

        // Resolve the atmosphere across the clock using the climate timing that references this weather
        // (so the sunrise/sunset windows match what the viewer would use), else the engine default day.
        var timing = ResolveTiming(w, climates);
        AnsiConsole.MarkupLine($"[cyan]Resolved atmosphere[/] (timing srB={timing.SunriseBeginHour:0.0} " +
                               $"srE={timing.SunriseEndHour:0.0} ssB={timing.SunsetBeginHour:0.0} ssE={timing.SunsetEndHour:0.0}):");
        var atmo = new Table().Border(TableBorder.Rounded)
            .AddColumn("Hour")
            .AddColumn("SkyTop")
            .AddColumn("SkyHorizon")
            .AddColumn("Ambient")
            .AddColumn("Sun")
            .AddColumn("SunI")
            .AddColumn("Fog");

        foreach (var hour in new[] { 0f, 3f, 6f, 9f, 12f, 15f, 18f, 21f })
        {
            var r = AtmosphereState.Resolve(hour, w, timing);
            _ = atmo.AddRow(
                $"{hour:00}:00",
                Vec(r.SkyTopColor),
                Vec(r.SkyHorizonColor),
                Vec(r.AmbientColor),
                Vec(r.SunColor),
                $"{r.SunIntensity:0.00}",
                Vec(r.FogColor));
        }

        AnsiConsole.Write(atmo);
    }

    private static AtmosphereState.ClimateTiming ResolveTiming(WeatherRecord w, IReadOnlyList<ClimateRecord> climates)
    {
        var owner = climates.FirstOrDefault(c => c.WeatherTypes.Any(e => e.WeatherFormId == w.FormId));
        return owner?.Timing is { } t
            ? AtmosphereState.ClimateTiming.FromClimateData(t)
            : AtmosphereState.ClimateTiming.Default;
    }

    // "RRGGBB/AA lum" — luminance lets the inversion jump out (dark Day vs bright Night = swapped/
    // miscategorised); the alpha byte exposes slice/stride misalignment (a "color-like" alpha = the
    // next band's R bleeding in = wrong entry stride).
    private static string Cell(WeatherRgba c)
    {
        var lum = (0.299f * c.R + 0.587f * c.G + 0.114f * c.B) / 255f;
        return $"{c.R:X2}{c.G:X2}{c.B:X2}/{c.A:X2} {lum:0.00}";
    }

    private static string Vec(System.Numerics.Vector3 v)
    {
        var lum = 0.299f * v.X + 0.587f * v.Y + 0.114f * v.Z;
        return $"{v.X:0.00},{v.Y:0.00},{v.Z:0.00} ({lum:0.00})";
    }
}

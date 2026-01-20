using System.CommandLine;
using System.Text.Json;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Commands for exporting ESM data (LAND records, etc.)
/// </summary>
public static partial class ExportCommands
{
    // Grid size for LAND vertex data (33x33 vertices per cell)
    private const int CellGridSize = 33;

    // Cached JSON options to avoid repeated allocations
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    // Known worldspace FormIDs for Fallout: New Vegas
    private static readonly Dictionary<string, uint> KnownWorldspaces = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WastelandNV"] = 0x000DA726,
        ["Wasteland"] = 0x000DA726, // Alias
        ["FreesideWorld"] = 0x00108E2D,
        ["Freeside"] = 0x00108E2D, // Alias
        ["Strip01"] = 0x00108E2E,
        ["Strip02"] = 0x00108E2F,
        ["DeadMoneyWorld"] = 0x01000DA3,
        ["HonestHeartsWorld"] = 0x02000800,
        ["OWBWorld"] = 0x03000DED,
        ["LonesomeRoadWorld"] = 0x04000A1E
    };

    public static Command CreateWorldmapCommand()
    {
        var command = new Command("worldmap",
            "Generate a worldspace heightmap by stitching all CELL LAND records together");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var worldspaceOption = new Option<string?>("-w", "--worldspace")
            { Description = "Worldspace name or FormID (default: WastelandNV)" };
        var outputOption = new Option<string>("-o", "--output")
            { Description = "Output directory", DefaultValueFactory = _ => "worldmap_export" };
        var scaleOption = new Option<int>("-s", "--scale")
        {
            Description = "Scale factor for output images (1=native 33px/cell, 2=66px/cell, etc.)",
            DefaultValueFactory = _ => 1
        };
        var rawOption = new Option<bool>("-r", "--raw")
            { Description = "Output raw 16-bit heightmap (for terrain editing tools)" };

        command.Arguments.Add(fileArg);
        command.Options.Add(worldspaceOption);
        command.Options.Add(outputOption);
        command.Options.Add(scaleOption);
        command.Options.Add(rawOption);

        command.SetAction(parseResult => GenerateWorldmap(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(worldspaceOption),
            parseResult.GetValue(outputOption)!,
            parseResult.GetValue(scaleOption),
            parseResult.GetValue(rawOption)));

        return command;
    }

    public static Command CreateExportLandCommand()
    {
        var command = new Command("export-land", "Export LAND records as images and JSON");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var formIdOption = new Option<string?>("-f", "--formid")
            { Description = "Specific FormID to export (hex, e.g., 0x00123456)" };
        var allOption = new Option<bool>("-a", "--all")
            { Description = "Export all LAND records (use --limit to control count)" };
        var limitOption = new Option<int>("-l", "--limit")
            { Description = "Maximum number of LAND records to export", DefaultValueFactory = _ => 100 };
        var outputOption = new Option<string>("-o", "--output")
            { Description = "Output directory", DefaultValueFactory = _ => "land_export" };

        command.Arguments.Add(fileArg);
        command.Options.Add(formIdOption);
        command.Options.Add(allOption);
        command.Options.Add(limitOption);
        command.Options.Add(outputOption);

        command.SetAction(parseResult => ExportLand(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(formIdOption),
            parseResult.GetValue(allOption),
            parseResult.GetValue(limitOption),
            parseResult.GetValue(outputOption)!));

        return command;
    }
}
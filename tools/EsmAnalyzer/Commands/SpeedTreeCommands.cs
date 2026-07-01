using System.CommandLine;
using System.Globalization;
using BethesdaMultitool.Core.Formats.SpeedTree;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Diagnostic commands for SpeedTree <c>.spt</c> ("__IdvSpt_02_") tree files.
/// </summary>
public static class SpeedTreeCommands
{
    public static Command CreateSptCommand()
    {
        var command = new Command("spt", "Inspect SpeedTree .spt tree files");
        command.Subcommands.Add(CreateDumpCommand());
        command.Subcommands.Add(SpeedTreeExtractDumpCommand.CreateExtractDumpCommand());
        command.Subcommands.Add(SpeedTreeRenderCommands.CreateRenderCommand());
        command.Subcommands.Add(SpeedTreeRenderCommands.CreateRenderAllCommand());
        command.Subcommands.Add(SpeedTreeRenderCommands.CreateSurveyCommand());
        command.Subcommands.Add(SpeedTreeLeafDebugCommand.Create());
        return command;
    }

    private static Command CreateDumpCommand()
    {
        var command = new Command("dump", "Parse a .spt file and dump its general params, branch splines, and leaf cards");
        var fileArg = new Argument<string>("file") { Description = "Path to the .spt file (or BSA-internal path with --bsa)" };
        var splinesOption = new Option<bool>("--splines")
        {
            Description = "Print every branch spline's control points (verbose)",
        };
        var bsaOption = new Option<string?>("--bsa")
        {
            Description = "Read <file> from this BSA archive instead of disk (e.g. an Oblivion Meshes BSA)",
        };
        command.Arguments.Add(fileArg);
        command.Options.Add(splinesOption);
        command.Options.Add(bsaOption);
        command.SetAction(parseResult => Dump(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(splinesOption),
            parseResult.GetValue(bsaOption)));
        return command;
    }

    private static int Dump(string path, bool printSplines, string? bsa)
    {
        var bytes = SpeedTreeSptIo.LoadSptBytes(path, bsa);
        if (bytes is null)
        {
            Console.Error.WriteLine($"File not found: {path}");
            return 1;
        }

        SptModel model;
        try
        {
            model = SptFile.Parse(bytes);
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"Not a valid .spt file: {ex.Message}");
            return 1;
        }

        var ci = CultureInfo.InvariantCulture;
        Console.WriteLine($"=== {Path.GetFileName(path)} ===");
        Console.WriteLine("[General]");
        Console.WriteLine($"  BarkTexture : {model.General.BarkTexturePath ?? "(none)"}");
        Console.WriteLine(string.Create(ci,
            $"  Floats      : 2001={model.General.Float2001} 2003={model.General.Float2003} 2006={model.General.Float2006} 2007={model.General.Float2007}"));
        Console.WriteLine(string.Create(ci, $"  Byte2002    : {model.General.Byte2002}   Token2005: {model.General.Token2005}"));
        Console.WriteLine(string.Create(ci, $"  LeafSize    : {model.LeafSize}"));
        if (model.LeafTable is { } lt)
        {
            Console.WriteLine(string.Create(ci,
                $"  LeafTable   : 3007(spacing)={lt.Float3007} 3008(mode)={lt.UInt3008} 3002={lt.Float3002} 3001={lt.UInt3001}"));
        }

        Console.WriteLine($"[Branches] count={model.Branches.Count}");
        for (var i = 0; i < model.Branches.Count; i++)
        {
            var b = model.Branches[i];
            Console.WriteLine(string.Create(ci,
                $"  Branch {i}: u6008={b.UInt6008} u6009={b.UInt6009} f={b.Float6010},{b.Float6011},{b.Float6012},{b.Float6013},{b.Float6014} bools={b.Bool6015},{b.Bool6016}"));
            for (var s = 0; s < b.Splines.Count; s++)
            {
                var sp = b.Splines[s];
                if (sp is null)
                {
                    Console.WriteLine($"    spline[{s}] = (none)");
                    continue;
                }

                Console.WriteLine(string.Create(ci,
                    $"    spline[{s}] header=({sp.Header.X},{sp.Header.Y},{sp.Header.Z}) points={sp.ControlPoints.Count}"));
                if (printSplines)
                {
                    foreach (var cp in sp.ControlPoints)
                    {
                        Console.WriteLine(string.Create(ci,
                            $"        p={cp.Param}  [{cp.A}, {cp.B}, {cp.C}, {cp.D}]"));
                    }
                }
            }
        }

        Console.WriteLine($"[Leaves] count={model.Leaves.Count}  LeafTextureCoords(10002)={model.LeafTextureCoords.Count}");
        for (var i = 0; i < model.Leaves.Count; i++)
        {
            var l = model.Leaves[i];
            Console.WriteLine(string.Create(ci,
                $"  Leaf {i}: type={l.Type} size={l.Size} pos=({l.Position.X},{l.Position.Y},{l.Position.Z}) mat={l.Material ?? "(none)"}"));
            Console.WriteLine(string.Create(ci,
                $"        c0=({l.Corner0.X},{l.Corner0.Y},{l.Corner0.Z}) c1=({l.Corner1.X},{l.Corner1.Y},{l.Corner1.Z}) c2=({l.Corner2.X},{l.Corner2.Y},{l.Corner2.Z}) f4007={l.Float4007}"));
        }

        for (var i = 0; i < model.LeafTextureCoords.Count; i++)
        {
            var uv = model.LeafTextureCoords[i];
            Console.WriteLine(string.Create(ci,
                $"  UV {i}: ({uv.Corner0.X},{uv.Corner0.Y}) ({uv.Corner1.X},{uv.Corner1.Y}) ({uv.Corner2.X},{uv.Corner2.Y}) ({uv.Corner3.X},{uv.Corner3.Y})"));
        }

        if (model.Wind is { } wind)
        {
            Console.WriteLine(string.Create(ci, $"[Wind] 5005={wind.Float5005} 5006={wind.Byte5006}"));
        }

        if (model.Lod is { } lod)
        {
            Console.WriteLine(string.Create(ci,
                $"[LOD] numBranchLods={lod.NumBranchLods} branchNear(LOD0)={lod.BranchNearFraction} branchFar={lod.BranchFarFraction}"));
        }
        else
        {
            Console.WriteLine("[LOD] (no section — engine defaults: 6 levels, near 1.0 = keep all)");
        }

        return 0;
    }
}

using System.CommandLine;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;
using Spectre.Console;
using ProdNifParser = BethesdaMultitool.Core.Formats.Nif.Parser.NifParser;

namespace NifAnalyzer.Commands;

/// <summary>
///     Skinning-path diagnostic: runs the production skin classification + the animation detector's
///     skinned-vs-static decision (the exact <c>skipSkinning</c> input the D3D12 mesh viewer uses)
///     and dumps per-shape skin health from <see cref="NifSkinningDiagnostics" /> — bone counts,
///     influence coverage, partition expansion, and the overall-transform flag.
/// </summary>
internal static class SkinDiagCommands
{
    internal static Command Create()
    {
        var command = new Command("skindiag",
            "Diagnose the skinning path: viewer skinned/static decision + per-shape skin health");
        var fileArg = new Argument<string>("file") { Description = "NIF file path" };
        command.Arguments.Add(fileArg);
        command.SetAction(parseResult => Execute(parseResult.GetValue(fileArg)!));
        return command;
    }

    private static int Execute(string path)
    {
        var data = File.ReadAllBytes(path);
        var nif = ProdNifParser.Parse(data);
        if (nif is null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] failed to parse NIF.");
            return 1;
        }

        var signature = NifAnimationDetector.Detect(data, nif);
        AnsiConsole.WriteLine($"File: {Path.GetFileName(path)}  Version: 0x{nif.BinaryVersion:X8}");
        Console.WriteLine(
            $"AnimationSignature: HasInternalSkin={signature.HasInternalSkin} " +
            $"NodeKeyframes={signature.HasNodeKeyframeTracks} " +
            $"Sequences={signature.HasControllerSequenceTracks}");
        Console.WriteLine(
            $"Viewer decision: skipSkinning={!signature.HasInternalSkin} " +
            "(ReferenceMeshDecoder12 passes skipSkinning: !HasInternalSkin)");
        AnsiConsole.WriteLine();

        var report = NifSkinningDiagnostics.Inspect(data, nif);
        AnsiConsole.WriteLine($"Skinned shapes: {report.Shapes.Count}");
        foreach (var shape in report.Shapes)
        {
            Console.WriteLine(
                $"  #{shape.ShapeIndex} '{shape.ShapeName}': verts={shape.VertexCount} " +
                $"bones={shape.BoneRefCount} " +
                $"weightsFromNiSkinData={shape.UsesNiSkinDataVertexWeights} " +
                $"partitions={shape.PartitionCount} " +
                $"(expanded {shape.PartitionsWithExpandedData}/missing {shape.PartitionsMissingExpandedData}) " +
                $"influenced={shape.VerticesWithInfluences}/{shape.VertexCount} " +
                $"multi={shape.VerticesWithMultipleInfluences} maxPerVert={shape.MaxInfluencesPerVertex} " +
                $"nonIdentityOverallTransform={shape.HasNonIdentityOverallTransform}");
        }

        return 0;
    }
}

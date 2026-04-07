using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;
using Spectre.Console;

namespace NifAnalyzer.Commands;

internal static class MeshDiagCommands
{
    public static Command CreateMeshDiagCommand()
    {
        var command = new Command("meshdiag", "Diagnose extracted render meshes, winding, and alpha state for a NIF");
        var fileArg = new Argument<string>("file") { Description = "NIF file path" };
        var shapeOption = new Option<string?>("-s", "--shape")
        {
            Description = "Optional shape-name filter (e.g. NoHat)"
        };
        var texturesBsaOption = new Option<string[]>("-t", "--textures-bsa")
        {
            Description = "Texture BSA paths used for alpha inspection",
            AllowMultipleArgumentsPerToken = true
        };

        command.Arguments.Add(fileArg);
        command.Options.Add(shapeOption);
        command.Options.Add(texturesBsaOption);
        command.SetAction(parseResult => MeshDiag(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(shapeOption),
            parseResult.GetValue(texturesBsaOption) ?? []));
        return command;
    }

    private static void MeshDiag(string path, string? shapeFilter, string[] texturesBsaPaths)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", Markup.Escape(path));
            return;
        }

        foreach (var archivePath in texturesBsaPaths)
        {
            if (!File.Exists(archivePath))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Texture BSA not found: {0}", Markup.Escape(archivePath));
                return;
            }
        }

        var data = File.ReadAllBytes(path);
        var nif = FalloutXbox360Utils.Core.Formats.Nif.NifParser.Parse(data);
        if (nif == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to parse NIF header: {0}", Markup.Escape(path));
            return;
        }

        if (nif.IsBigEndian)
        {
            var converted = NifConverter.Convert(data);
            if (!converted.Success || converted.OutputData == null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Failed to convert Xbox NIF: {0}", Markup.Escape(path));
                return;
            }

            data = converted.OutputData;
            nif = FalloutXbox360Utils.Core.Formats.Nif.NifParser.Parse(data);
            if (nif == null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Failed to parse converted NIF: {0}", Markup.Escape(path));
                return;
            }
        }

        using var textureResolver = texturesBsaPaths.Length > 0
            ? new NifTextureResolver(texturesBsaPaths)
            : null;

        var model = NifGeometryExtractor.Extract(data, nif, textureResolver, filterShapeName: shapeFilter);
        if (model == null || !model.HasGeometry)
        {
            AnsiConsole.MarkupLine("[yellow]No renderable geometry found.[/]");
            return;
        }

        var diagnostics = model.Submeshes
            .Select(submesh => AnalyzeSubmesh(submesh, textureResolver))
            .OrderByDescending(item => item.Winding.FlippedCount)
            .ThenByDescending(item => item.Submesh.TriangleCount)
            .ToList();

        RenderSummary(path, nif, model, shapeFilter, texturesBsaPaths.Length, diagnostics);
        RenderTable(diagnostics);
        RenderFindings(diagnostics);
    }

    private static SubmeshDiagnostic AnalyzeSubmesh(RenderableSubmesh submesh, NifTextureResolver? textureResolver)
    {
        var diffuseTexture = textureResolver != null && !string.IsNullOrWhiteSpace(submesh.DiffuseTexturePath)
            ? textureResolver.GetTexture(submesh.DiffuseTexturePath)
            : null;

        var sourceAlpha = NifAlphaClassifier.Classify(submesh, diffuseTexture);
        var exportAlpha = NpcGlbAlphaTexturePacker.Prepare(submesh, diffuseTexture);
        var winding = MeshWindingDiagnostic.Analyze(submesh);
        var alphaTexture = AnalyzeTextureAlpha(submesh.DiffuseTexturePath, diffuseTexture);

        return new SubmeshDiagnostic(submesh, winding, sourceAlpha, exportAlpha, alphaTexture);
    }

    private static void RenderSummary(
        string path,
        FalloutXbox360Utils.Core.Formats.Nif.NifInfo nif,
        NifRenderableModel model,
        string? shapeFilter,
        int textureArchiveCount,
        IReadOnlyCollection<SubmeshDiagnostic> diagnostics)
    {
        var summary = new Table().Border(TableBorder.Rounded);
        summary.AddColumn("Property");
        summary.AddColumn("Value");
        summary.AddRow("File", Markup.Escape(path));
        summary.AddRow("Endian", nif.IsBigEndian ? "[yellow]Big (Xbox 360)[/]" : "[green]Little (PC)[/]");
        summary.AddRow("Blocks", nif.BlockCount.ToString());
        summary.AddRow("Shape filter", string.IsNullOrWhiteSpace(shapeFilter) ? "[dim](none)[/]" : Markup.Escape(shapeFilter));
        summary.AddRow("Texture archives", textureArchiveCount.ToString());
        summary.AddRow("Submeshes", diagnostics.Count.ToString());
        summary.AddRow("Bounds", FormattableString.Invariant($"{model.Width:F2} x {model.Height:F2} x {model.Depth:F2}"));
        summary.AddRow("Skinned", model.WasSkinned ? "[green]yes[/]" : "[dim]no[/]");
        AnsiConsole.Write(summary);
        AnsiConsole.WriteLine();
    }

    private static void RenderTable(IReadOnlyCollection<SubmeshDiagnostic> diagnostics)
    {
        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("Shape");
        table.AddColumn(new TableColumn("Verts").RightAligned());
        table.AddColumn(new TableColumn("Tris").RightAligned());
        table.AddColumn("Winding");
        table.AddColumn("Source Alpha");
        table.AddColumn("Export Alpha");
        table.AddColumn("Double");
        table.AddColumn("Tex Alpha");
        table.AddColumn("Diffuse");

        foreach (var diagnostic in diagnostics)
        {
            table.AddRow(
                Markup.Escape(diagnostic.Submesh.ShapeName ?? "(unnamed)"),
                diagnostic.Submesh.VertexCount.ToString(),
                diagnostic.Submesh.TriangleCount.ToString(),
                FormatWinding(diagnostic.Winding),
                FormatAlphaMode(diagnostic.SourceAlpha.RenderMode),
                FormatAlphaMode(diagnostic.ExportAlpha.RenderMode),
                diagnostic.Submesh.IsDoubleSided ? "[green]yes[/]" : "[dim]no[/]",
                FormatTextureAlpha(diagnostic.TextureAlpha),
                Markup.Escape(ShortenPath(diagnostic.Submesh.DiffuseTexturePath)));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static void RenderFindings(IReadOnlyCollection<SubmeshDiagnostic> diagnostics)
    {
        var findings = new List<string>();

        foreach (var diagnostic in diagnostics)
        {
            var shapeName = Markup.Escape(diagnostic.Submesh.ShapeName ?? "(unnamed)");
            if (diagnostic.Winding.FlippedCount > 0)
            {
                findings.Add(FormattableString.Invariant(
                    $"[red]{shapeName}[/]: {diagnostic.Winding.FlippedCount}/{diagnostic.Winding.TotalTriangles} triangles disagree with stored normals."));
            }

            if (diagnostic.SourceAlpha.RenderMode != diagnostic.ExportAlpha.RenderMode)
            {
                findings.Add(FormattableString.Invariant(
                    $"[yellow]{shapeName}[/]: export alpha changes {diagnostic.SourceAlpha.RenderMode} -> {diagnostic.ExportAlpha.RenderMode}."));
            }

            if (diagnostic.TextureAlpha is { Resolved: false, HasTexturePath: true })
            {
                findings.Add(FormattableString.Invariant(
                    $"[yellow]{shapeName}[/]: diffuse texture missing from supplied archives: {Markup.Escape(diagnostic.Submesh.DiffuseTexturePath!)}"));
            }

            if (diagnostic.TextureAlpha is { Resolved: true, MostlyBinaryAlpha: true } textureAlpha &&
                diagnostic.ExportAlpha.RenderMode == NifAlphaRenderMode.Blend)
            {
                findings.Add(FormattableString.Invariant(
                    $"[red]{shapeName}[/]: binary-style alpha ({textureAlpha.TransitionalPercent}% transitional) still exports as Blend."));
            }
        }

        if (findings.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No obvious winding or alpha-state problems detected.[/]");
            return;
        }

        AnsiConsole.Write(new Rule("[bold]Findings[/]").LeftJustified());
        foreach (var finding in findings)
        {
            AnsiConsole.MarkupLine(finding);
        }
    }

    private static TextureAlphaDiagnostic AnalyzeTextureAlpha(string? texturePath, DecodedTexture? texture)
    {
        if (string.IsNullOrWhiteSpace(texturePath))
        {
            return TextureAlphaDiagnostic.None;
        }

        if (texture == null)
        {
            return TextureAlphaDiagnostic.Missing;
        }

        var pixels = texture.Pixels;
        var total = 0;
        var transparent = 0;
        var transitional = 0;
        var opaque = 0;
        var step = Math.Max(1, pixels.Length / (4 * 4096));
        for (var index = 3; index < pixels.Length; index += 4 * step)
        {
            var alpha = pixels[index];
            total++;
            if (alpha <= 16)
            {
                transparent++;
            }
            else if (alpha >= 239)
            {
                opaque++;
            }
            else
            {
                transitional++;
            }
        }

        var mostlyBinary = total > 0 && transitional <= Math.Max(1, total / 20);
        var significantAlpha = total > 0 && (transparent + transitional) > total / 10;
        return new TextureAlphaDiagnostic(
            HasTexturePath: true,
            Resolved: true,
            HasSignificantAlpha: significantAlpha,
            MostlyBinaryAlpha: mostlyBinary,
            TransparentPercent: Percent(transparent, total),
            TransitionalPercent: Percent(transitional, total),
            OpaquePercent: Percent(opaque, total));
    }

    private static string FormatWinding(SubmeshWindingAnalysis analysis)
    {
        if (analysis.TotalTriangles == 0)
        {
            return "[dim]n/a[/]";
        }

        return analysis.FlippedCount == 0
            ? FormattableString.Invariant($"[green]0/{analysis.TotalTriangles}[/]")
            : FormattableString.Invariant($"[red]{analysis.FlippedCount}/{analysis.TotalTriangles}[/]");
    }

    private static string FormatAlphaMode(NifAlphaRenderMode mode)
    {
        return mode switch
        {
            NifAlphaRenderMode.Opaque => "[green]Opaque[/]",
            NifAlphaRenderMode.Cutout => "[yellow]Cutout[/]",
            _ => "[red]Blend[/]"
        };
    }

    private static string FormatTextureAlpha(TextureAlphaDiagnostic alpha)
    {
        if (!alpha.HasTexturePath)
        {
            return "[dim]n/a[/]";
        }

        if (!alpha.Resolved)
        {
            return "[yellow]missing[/]";
        }

        if (!alpha.HasSignificantAlpha)
        {
            return "[green]opaque[/]";
        }

        return alpha.MostlyBinaryAlpha
            ? FormattableString.Invariant($"[yellow]binary[/] [dim]{alpha.TransitionalPercent}% mid[/]")
            : FormattableString.Invariant($"[red]mixed[/] [dim]{alpha.TransitionalPercent}% mid[/]");
    }

    private static string ShortenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "(none)";
        }

        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2)
        {
            return path;
        }

        return string.Join('/', parts[^2], parts[^1]);
    }

    private static int Percent(int value, int total)
    {
        return total == 0 ? 0 : (int)Math.Round(value * 100.0 / total);
    }

    private readonly record struct SubmeshDiagnostic(
        RenderableSubmesh Submesh,
        SubmeshWindingAnalysis Winding,
        NifAlphaRenderState SourceAlpha,
        NpcGlbAlphaTexturePacker.PreparedAlphaTexture ExportAlpha,
        TextureAlphaDiagnostic TextureAlpha);

    private readonly record struct TextureAlphaDiagnostic(
        bool HasTexturePath,
        bool Resolved,
        bool HasSignificantAlpha,
        bool MostlyBinaryAlpha,
        int TransparentPercent,
        int TransitionalPercent,
        int OpaquePercent)
    {
        internal static TextureAlphaDiagnostic None => new(false, false, false, false, 0, 0, 0);
        internal static TextureAlphaDiagnostic Missing => new(true, false, false, false, 0, 0, 0);
    }
}
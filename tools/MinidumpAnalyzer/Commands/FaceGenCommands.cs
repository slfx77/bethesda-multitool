using System.CommandLine;
using System.Globalization;
using System.Text;
using Spectre.Console;

namespace MinidumpAnalyzer.Commands;

/// <summary>
///     Commands for FaceGen CTL file processing.
/// </summary>
public static class FaceGenCommands
{
    /// <summary>
    ///     Creates the 'gen-facegen' command.
    /// </summary>
    public static Command CreateGenFaceGenCommand()
    {
        var inputArg = new Argument<string>("ctl-file") { Description = "Path to the FaceGen si.ctl file" };
        var outputOpt = new Option<string?>("-o", "--output") { Description = "Output path for generated C# file" };
        var summaryOpt = new Option<bool>("--summary") { Description = "Print summary of CTL contents only" };

        var command = new Command("gen-facegen", "Parse FaceGen si.ctl and generate C# code");
        command.Arguments.Add(inputArg);
        command.Options.Add(outputOpt);
        command.Options.Add(summaryOpt);

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputOpt);
            var summaryOnly = parseResult.GetValue(summaryOpt);
            GenerateFaceGen(input, output, summaryOnly);
        });

        return command;
    }

    private static void GenerateFaceGen(string ctlPath, string? outputPath, bool summaryOnly)
    {
        if (!File.Exists(ctlPath))
        {
            AnsiConsole.MarkupLine($"[red]Error: CTL file not found: {ctlPath}[/]");
            Environment.Exit(1);
            return;
        }

        var data = File.ReadAllBytes(ctlPath);

        if (data.Length < 32 || Encoding.ASCII.GetString(data, 0, 8) != "FRCTL001")
        {
            AnsiConsole.MarkupLine("[red]Error: Not a valid FaceGen CTL file (expected FRCTL001 magic)[/]");
            Environment.Exit(1);
            return;
        }

        var ctlData = ParseCtl(data);

        PrintSummary(ctlData);

        if (summaryOnly)
        {
            return;
        }

        if (string.IsNullOrEmpty(outputPath))
        {
            outputPath = Path.Combine(
                Path.GetDirectoryName(ctlPath) ?? ".",
                "FaceGenControls.cs");
        }

        var csharp = GenerateCSharp(ctlData);
        File.WriteAllText(outputPath, csharp, Encoding.UTF8);

        AnsiConsole.MarkupLine($"\n[green]Generated:[/] {outputPath}");
        AnsiConsole.MarkupLine($"  File size: {csharp.Length:N0} bytes");
    }

    private static CtlData ParseCtl(byte[] data)
    {
        var offset = 8;
        var geoVer = BitConverter.ToUInt32(data, offset); offset += 4;
        var texVer = BitConverter.ToUInt32(data, offset); offset += 4;
        var gsSize = BitConverter.ToUInt32(data, offset); offset += 4;
        var gaSize = BitConverter.ToUInt32(data, offset); offset += 4;
        var tsSize = BitConverter.ToUInt32(data, offset); offset += 4;
        _ = BitConverter.ToUInt32(data, offset); offset += 4; // taSize

        var (newOffset, gsControls) = ReadSection(data, offset, (int)gsSize);
        offset = newOffset;
        var (newOffset2, gaControls) = ReadSection(data, offset, (int)gaSize);
        offset = newOffset2;
        var (newOffset3, tsControls) = ReadSection(data, offset, (int)tsSize);

        return new CtlData
        {
            GeoVersion = geoVer,
            TexVersion = texVer,
            GeometrySymmetric = new CtlSection { BasisSize = (int)gsSize, Controls = gsControls },
            GeometryAsymmetric = new CtlSection { BasisSize = (int)gaSize, Controls = gaControls },
            TextureSymmetric = new CtlSection { BasisSize = (int)tsSize, Controls = tsControls }
        };
    }

    private static (int NewOffset, List<CtlControl> Controls) ReadSection(byte[] data, int offset, int basisSize)
    {
        var count = BitConverter.ToUInt32(data, offset);
        offset += 4;

        var controls = new List<CtlControl>();
        for (var i = 0; i < count; i++)
        {
            var coeffs = new float[basisSize];
            for (var j = 0; j < basisSize; j++)
            {
                coeffs[j] = BitConverter.ToSingle(data, offset);
                offset += 4;
            }

            var strLen = BitConverter.ToUInt32(data, offset);
            offset += 4;
            var label = Encoding.ASCII.GetString(data, offset, (int)strLen);
            offset += (int)strLen;

            controls.Add(new CtlControl { Name = label, Coefficients = coeffs });
        }

        return (offset, controls);
    }

    private static void PrintSummary(CtlData ctl)
    {
        AnsiConsole.MarkupLine("[cyan]FaceGen si.ctl Summary[/]");
        AnsiConsole.MarkupLine($"  Geometry Basis Version: {ctl.GeoVersion}");
        AnsiConsole.MarkupLine($"  Texture Basis Version: {ctl.TexVersion}");
        AnsiConsole.WriteLine();

        PrintSectionSummary("Geometry-Symmetric", ctl.GeometrySymmetric);
        PrintSectionSummary("Geometry-Asymmetric", ctl.GeometryAsymmetric);
        PrintSectionSummary("Texture-Symmetric", ctl.TextureSymmetric);
    }

    private static void PrintSectionSummary(string name, CtlSection section)
    {
        AnsiConsole.MarkupLine($"  [yellow]{name}:[/] {section.Controls.Count} controls x {section.BasisSize} basis");
        for (var i = 0; i < section.Controls.Count; i++)
        {
            AnsiConsole.MarkupLine($"    [{i,2}] {section.Controls[i].Name}");
        }

        AnsiConsole.WriteLine();
    }

    private static string GenerateCSharp(CtlData ctl)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated by MinidumpAnalyzer gen-facegen from si.ctl");
        sb.AppendLine("// FaceGen control definitions for NPC face morph reporting");
        sb.AppendLine();
        sb.AppendLine("namespace FalloutXbox360Utils.Core.Formats.Esm;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// FaceGen linear control definitions parsed from si.ctl.");
        sb.AppendLine("/// Each control defines a named direction in face space.");
        sb.AppendLine("/// The GECK's Face Advanced slider values are projections of");
        sb.AppendLine("/// NPC face coordinates onto these control directions.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class FaceGenControls");
        sb.AppendLine("{");

        AppendNameArray(sb, "GeometrySymmetricNames", "Geometry-Symmetric",
            ctl.GeometrySymmetric.BasisSize, "FGGS", ctl.GeometrySymmetric);
        AppendCoeffMatrix(sb, "GeometrySymmetricCoeffs", "Geometry-Symmetric",
            ctl.GeometrySymmetric.BasisSize, "FGGS", ctl.GeometrySymmetric);
        AppendNameArray(sb, "GeometryAsymmetricNames", "Geometry-Asymmetric",
            ctl.GeometryAsymmetric.BasisSize, "FGGA", ctl.GeometryAsymmetric);
        AppendCoeffMatrix(sb, "GeometryAsymmetricCoeffs", "Geometry-Asymmetric",
            ctl.GeometryAsymmetric.BasisSize, "FGGA", ctl.GeometryAsymmetric);
        AppendNameArray(sb, "TextureSymmetricNames", "Texture-Symmetric",
            ctl.TextureSymmetric.BasisSize, "FGTS", ctl.TextureSymmetric);
        AppendCoeffMatrix(sb, "TextureSymmetricCoeffs", "Texture-Symmetric",
            ctl.TextureSymmetric.BasisSize, "FGTS", ctl.TextureSymmetric);

        AppendComputeMethod(sb, "ComputeGeometrySymmetric", "fggs",
            ctl.GeometrySymmetric.BasisSize, "GeometrySymmetric");
        AppendComputeMethod(sb, "ComputeGeometryAsymmetric", "fgga",
            ctl.GeometryAsymmetric.BasisSize, "GeometryAsymmetric");
        AppendComputeMethod(sb, "ComputeTextureSymmetric", "fgts",
            ctl.TextureSymmetric.BasisSize, "TextureSymmetric");

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendNameArray(StringBuilder sb, string fieldName, string sectionName,
        int basisSize, string subrecord, CtlSection section)
    {
        sb.AppendLine("    /// <summary>");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"    /// {sectionName} control names ({section.Controls.Count} controls).");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"    /// These project the {basisSize} {subrecord} basis coefficients onto named sliders.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"    public static readonly string[] {fieldName} =");
        sb.AppendLine("    {");

        for (var i = 0; i < section.Controls.Count; i++)
        {
            var comma = i < section.Controls.Count - 1 ? "," : "";
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"        \"{section.Controls[i].Name}\"{comma} // [{i}]");
        }

        sb.AppendLine("    };");
        sb.AppendLine();
    }

    private static void AppendCoeffMatrix(StringBuilder sb, string fieldName, string sectionName,
        int basisSize, string subrecord, CtlSection section)
    {
        sb.AppendLine("    /// <summary>");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"    /// {sectionName} coefficient matrix ({section.Controls.Count}x{basisSize}).");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"    /// Each row is a unit-length direction vector in {basisSize}-dimensional {subrecord} space.");
        sb.AppendLine("    /// Slider value = dot(row[j], fggs_values).");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"    public static readonly float[][] {fieldName} = new float[{section.Controls.Count}][]");
        sb.AppendLine("    {");

        for (var i = 0; i < section.Controls.Count; i++)
        {
            var comma = i < section.Controls.Count - 1 ? "," : "";
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"        // [{i}] {section.Controls[i].Name}");
            sb.AppendLine("        new float[] {");
            sb.AppendLine(FormatFloatArray(section.Controls[i].Coefficients, 12));
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"        }}{comma}");
        }

        sb.AppendLine("    };");
        sb.AppendLine();
    }

    private static void AppendComputeMethod(StringBuilder sb, string methodName, string paramName,
        int basisSize, string prefix)
    {
        sb.AppendLine("    /// <summary>");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"    /// Compute control projection values from {prefix.ToUpperInvariant()} basis coefficients.");
        sb.AppendLine("    /// Returns an array of (name, value) pairs for each control.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"    public static (string Name, float Value)[] {methodName}(float[] {paramName})");
        sb.AppendLine("    {");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"        if ({paramName}.Length != {basisSize}) return Array.Empty<(string, float)>();");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"        var results = new (string Name, float Value)[{prefix}Names.Length];");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"        for (int j = 0; j < {prefix}Names.Length; j++)");
        sb.AppendLine("        {");
        sb.AppendLine("            float dot = 0;");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"            for (int i = 0; i < {basisSize}; i++)");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"                dot += {prefix}Coeffs[j][i] * {paramName}[i];");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"            results[j] = ({prefix}Names[j], dot);");
        sb.AppendLine("        }");
        sb.AppendLine("        return results;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static string FormatFloatArray(float[] floats, int indent)
    {
        var prefix = new string(' ', indent);
        var sb = new StringBuilder();
        for (var i = 0; i < floats.Length; i += 10)
        {
            var count = Math.Min(10, floats.Length - i);
            var values = new string[count];
            for (var j = 0; j < count; j++)
            {
                values[j] = floats[i + j].ToString(" 0.00000000", CultureInfo.InvariantCulture) + "f";
            }

            if (i > 0)
            {
                sb.AppendLine(",");
            }

            sb.Append(prefix);
            sb.Append(string.Join(", ", values));
        }

        return sb.ToString();
    }

    private sealed class CtlData
    {
        public uint GeoVersion { get; init; }
        public uint TexVersion { get; init; }
        public required CtlSection GeometrySymmetric { get; init; }
        public required CtlSection GeometryAsymmetric { get; init; }
        public required CtlSection TextureSymmetric { get; init; }
    }

    private sealed class CtlSection
    {
        public int BasisSize { get; init; }
        public required List<CtlControl> Controls { get; init; }
    }

    private sealed class CtlControl
    {
        public required string Name { get; init; }
        public required float[] Coefficients { get; init; }
    }
}

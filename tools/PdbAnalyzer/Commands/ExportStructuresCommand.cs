using static PdbAnalyzer.PdbAnalyzerHelpers;

namespace PdbAnalyzer.Commands;

/// <summary>
///     Export structures with Endian() methods as C# code for the ESM converter.
/// </summary>
internal static class ExportStructuresCommand
{
    internal static async Task<int> ExecuteAsync(string cvdumpPath, string outputPath)
    {
        if (!File.Exists(cvdumpPath))
        {
            Console.WriteLine($"File not found: {cvdumpPath}");
            return 1;
        }

        Console.WriteLine($"Parsing {cvdumpPath}...");
        var parser = new CvdumpParser();
        await parser.ParseAsync(cvdumpPath);

        var endianStructs = parser.Structures
            .Where(s => s.HasEndianMethod && s.Size > 0 && s.Fields.Count > 0)
            .OrderBy(s => CategorizeStructure(s.Name))
            .ThenBy(s => s.Name)
            .ToList();

        Console.WriteLine($"Exporting {endianStructs.Count} structures to {outputPath}...");

        using var writer = new StreamWriter(outputPath);

        await writer.WriteLineAsync("// Auto-generated from Fallout.pdb using PdbAnalyzer");
        await writer.WriteLineAsync(
            "// These structures have Endian() methods and need byte-swapping for Xbox 360 conversion.");
        await writer.WriteLineAsync($"// Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("using System.Runtime.InteropServices;");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("namespace Xbox360MemoryCarver.Core.Formats.Esm;");
        await writer.WriteLineAsync();

        string? currentCategory = null;
        foreach (var s in endianStructs)
        {
            var category = CategorizeStructure(s.Name);
            if (category != currentCategory)
            {
                await writer.WriteLineAsync();
                await writer.WriteLineAsync("// ============================================");
                await writer.WriteLineAsync($"// {category}");
                await writer.WriteLineAsync("// ============================================");
                currentCategory = category;
            }

            await writer.WriteLineAsync();
            await writer.WriteLineAsync("/// <summary>");
            await writer.WriteLineAsync($"/// {s.Name} structure ({s.Size} bytes)");
            await writer.WriteLineAsync("/// </summary>");
            await writer.WriteLineAsync($"[StructLayout(LayoutKind.Explicit, Size = {s.Size})]");
            await writer.WriteLineAsync($"public struct {SanitizeName(s.Name)}");
            await writer.WriteLineAsync("{");

            foreach (var f in s.Fields.OrderBy(f => f.Offset))
            {
                var csharpType = ConvertType(f.TypeName);
                await writer.WriteLineAsync(
                    $"    [FieldOffset({f.Offset})] public {csharpType} {SanitizeFieldName(f.Name)};");
            }

            await writer.WriteLineAsync("}");
        }

        Console.WriteLine($"Done! Exported {endianStructs.Count} structures.");
        return 0;
    }
}

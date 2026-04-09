using System.Text.RegularExpressions;
using static PdbAnalyzer.PdbAnalyzerHelpers;

namespace PdbAnalyzer.Commands;

/// <summary>
///     Find all ESM-related structures.
/// </summary>
internal static class FindEsmStructuresCommand
{
    internal static async Task<int> ExecuteAsync(string cvdumpPath)
    {
        if (!File.Exists(cvdumpPath))
        {
            Console.WriteLine($"File not found: {cvdumpPath}");
            return 1;
        }

        Console.WriteLine($"Parsing {cvdumpPath}...");
        var parser = new CvdumpParser();
        await parser.ParseAsync(cvdumpPath);

        // ESM-related patterns
        string[] patterns =
        [
            "^FORM$", "^CHUNK$", "^FILE_HEADER$", "^TESFile$",
            "^OBJ_", "^NPC_", "^CELL_", "^LAND_", "^NAVM_",
            "^TESObject", "^TESNPC", "^TESCell",
            "^BGS", "^TES4", "^GRUP"
        ];

        var combinedRegex = new Regex(string.Join("|", patterns), RegexOptions.IgnoreCase);

        var esmStructs = parser.Structures
            .Where(s => combinedRegex.IsMatch(s.Name) && s.Size > 0)
            .OrderBy(s => CategorizeStructure(s.Name))
            .ThenBy(s => s.Name)
            .ToList();

        Console.WriteLine();
        Console.WriteLine("=== ESM-Related Structures ===");
        Console.WriteLine();

        string? currentCategory = null;
        foreach (var s in esmStructs)
        {
            var category = CategorizeStructure(s.Name);
            if (category != currentCategory)
            {
                Console.WriteLine();
                Console.WriteLine($"--- {category} ---");
                currentCategory = category;
            }

            var endian = s.HasEndianMethod ? " [Endian]" : "";
            Console.WriteLine($"  {s.Name,-45} {s.Size,6} bytes{endian}");
        }

        Console.WriteLine();
        Console.WriteLine($"Total: {esmStructs.Count} ESM-related structures");
        Console.WriteLine($"With Endian(): {esmStructs.Count(s => s.HasEndianMethod)}");

        return 0;
    }
}

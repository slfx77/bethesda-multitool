using static PdbAnalyzer.PdbAnalyzerHelpers;

namespace PdbAnalyzer.Commands;

/// <summary>
///     Find all structures that have an Endian() method - these need byte-swapping for Xbox 360 conversion.
/// </summary>
internal static class FindEndianStructuresCommand
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

        Console.WriteLine();
        Console.WriteLine("=== Structures with Endian() methods ===");
        Console.WriteLine(
            "These structures contain multi-byte fields that need byte-swapping for Xbox 360 conversion.");
        Console.WriteLine();

        var endianStructs = parser.Structures
            .Where(s => s.HasEndianMethod && s.Size > 0)
            .OrderBy(s => s.Name)
            .ToList();

        Console.WriteLine($"{"Structure",-45} {"Size",-8} {"Fields",-8} Category");
        Console.WriteLine(new string('-', 80));

        foreach (var s in endianStructs)
        {
            var category = CategorizeStructure(s.Name);
            Console.WriteLine($"{s.Name,-45} {s.Size,-8} {s.Fields.Count,-8} {category}");
        }

        Console.WriteLine();
        Console.WriteLine($"Total: {endianStructs.Count} structures with Endian() methods");

        // Group by category
        Console.WriteLine();
        Console.WriteLine("=== By Category ===");
        var byCategory = endianStructs.GroupBy(s => CategorizeStructure(s.Name)).OrderBy(g => g.Key);
        foreach (var group in byCategory) Console.WriteLine($"  {group.Key}: {group.Count()} structures");

        return 0;
    }
}

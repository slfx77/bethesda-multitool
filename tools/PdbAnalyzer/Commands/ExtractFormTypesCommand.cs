using static PdbAnalyzer.PdbAnalyzerHelpers;

namespace PdbAnalyzer.Commands;

/// <summary>
///     Extract ENUM_FORM_ID from PDB and cross-reference with C++ struct sizes.
///     Shows the complete mapping: byte value -> enum name -> 4-letter code -> C++ class -> size.
/// </summary>
internal static class ExtractFormTypesCommand
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

        // Find ENUM_FORM_ID
        if (!parser.Enums.TryGetValue("ENUM_FORM_ID", out var formIdEnum))
        {
            Console.WriteLine("ERROR: ENUM_FORM_ID not found in PDB type dump.");
            Console.WriteLine("Available enums containing 'FORM':");
            foreach (var e in parser.Enums.Keys.Where(k =>
                         k.Contains("FORM", StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine($"  {e}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"=== ENUM_FORM_ID ({formIdEnum.Members.Count} members) ===");
        Console.WriteLine("Source: PDB LF_ENUM (compile-time C++ enum, sequential values 0-N)");
        Console.WriteLine();

        // Build struct lookup by name (case-sensitive)
        var structsByName = parser.Structures
            .Where(s => s.Size > 0)
            .GroupBy(s => s.Name)
            .ToDictionary(g => g.Key, g => g.First());

        Console.WriteLine(
            $"{"Value",-7} {"Enum Name",-22} {"Record",-6} {"C++ Class",-30} {"Size",-6} {"Notes"}");
        Console.WriteLine(new string('-', 100));

        var matched = 0;
        var unmatched = 0;

        foreach (var member in formIdEnum.Members.OrderBy(m => m.Value))
        {
            if (member.Name == "FORM_ID_COUNT")
            {
                Console.WriteLine(
                    $"0x{member.Value:X2}    {"FORM_ID_COUNT",-22} {"---",-6} {"(sentinel)",-30} {"---",-6}");
                continue;
            }

            // Derive 4-letter record code by stripping _ID suffix
            var recordCode = member.Name.EndsWith("_ID")
                ? member.Name[..^3]
                : member.Name;

            // Look up C++ class name
            var className = GetClassNameForRecord(recordCode);
            var size = "";
            var notes = "";

            if (className != null && structsByName.TryGetValue(className, out var structInfo))
            {
                size = structInfo.Size.ToString();
                matched++;
            }
            else if (className != null)
            {
                notes = $"(class '{className}' not found in PDB)";
                unmatched++;
            }
            else
            {
                notes = "(no class mapping defined)";
                unmatched++;
            }

            Console.WriteLine(
                $"0x{member.Value:X2}    {member.Name,-22} {recordCode,-6} {className ?? "?",-30} {size,-6} {notes}");
        }

        Console.WriteLine();
        Console.WriteLine($"Total: {formIdEnum.Members.Count} enum values");
        Console.WriteLine($"Matched to PDB structs: {matched}");
        Console.WriteLine($"Unmatched: {unmatched}");

        return 0;
    }
}

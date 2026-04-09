namespace PdbAnalyzer.Commands;

/// <summary>
///     Get detailed field layout for a specific structure.
/// </summary>
internal static class GetStructureDetailsCommand
{
    internal static async Task<int> ExecuteAsync(string cvdumpPath, string structName)
    {
        if (!File.Exists(cvdumpPath))
        {
            Console.WriteLine($"File not found: {cvdumpPath}");
            return 1;
        }

        var parser = new CvdumpParser();
        await parser.ParseAsync(cvdumpPath);

        var matches = parser.Structures
            .Where(s => s.Name.Contains(structName, StringComparison.OrdinalIgnoreCase) && s.Size > 0)
            .ToList();

        if (matches.Count == 0)
        {
            Console.WriteLine($"No structures found matching '{structName}'");
            return 1;
        }

        foreach (var s in matches)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {s.Name} ({s.Size} bytes) ===");
            Console.WriteLine($"Type Index: 0x{s.TypeIndex:X}");
            Console.WriteLine($"Has Endian(): {s.HasEndianMethod}");
            Console.WriteLine();

            if (s.Fields.Count > 0)
            {
                Console.WriteLine($"{"Offset",-8} {"Type",-20} {"Name",-30}");
                Console.WriteLine(new string('-', 60));
                foreach (var f in s.Fields.OrderBy(f => f.Offset))
                    Console.WriteLine($"{f.Offset,-8} {f.TypeName,-20} {f.Name,-30}");
            }
            else
            {
                Console.WriteLine("(No field information available - may need to look up field list)");
            }
        }

        return 0;
    }
}

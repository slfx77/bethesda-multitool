using static PdbAnalyzer.PdbAnalyzerHelpers;

namespace PdbAnalyzer.Commands;

/// <summary>
///     Export flattened struct layouts for all FormType classes as JSON.
///     Recursively resolves base class fields to produce complete field maps.
/// </summary>
internal static class ExportLayoutsCommand
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

        // Find ENUM_FORM_ID
        if (!parser.Enums.TryGetValue("ENUM_FORM_ID", out var formIdEnum))
        {
            Console.WriteLine("ERROR: ENUM_FORM_ID not found in PDB type dump.");
            return 1;
        }

        // Build struct lookup by name
        var structsByName = parser.Structures
            .Where(s => s.Size > 0)
            .GroupBy(s => s.Name)
            .ToDictionary(g => g.Key, g => g.First());

        Console.WriteLine();
        Console.WriteLine("=== Flattening struct layouts ===");
        Console.WriteLine();

        var types = new Dictionary<string, object>();
        var matched = 0;
        var totalFields = 0;

        foreach (var member in formIdEnum.Members.OrderBy(m => m.Value))
        {
            if (member.Name == "FORM_ID_COUNT")
                continue;

            var recordCode = member.Name.EndsWith("_ID")
                ? member.Name[..^3]
                : member.Name;

            var className = GetClassNameForRecord(recordCode);
            if (className == null || !structsByName.TryGetValue(className, out var structInfo))
                continue;

            var flatFields = parser.FlattenFields(structInfo);
            matched++;
            totalFields += flatFields.Count;

            var key = $"0x{member.Value:X2}";
            types[key] = new
            {
                formType = member.Value,
                recordCode,
                className,
                structSize = structInfo.Size,
                fields = flatFields.Select(f => new
                {
                    name = f.Name,
                    offset = f.Offset,
                    size = f.Size,
                    kind = f.Kind,
                    owner = f.OwnerClass,
                    typeDetail = f.TypeDetail
                }).ToArray()
            };

            var fieldKinds = flatFields.GroupBy(f => f.Kind).OrderByDescending(g => g.Count());
            var kindSummary = string.Join(", ", fieldKinds.Select(g => $"{g.Count()} {g.Key}"));
            Console.WriteLine(
                $"  0x{member.Value:X2} {recordCode,-6} {className,-30} {structInfo.Size,5}B  {flatFields.Count,3} fields ({kindSummary})");
        }

        Console.WriteLine();
        Console.WriteLine($"Total: {matched} types, {totalFields} fields");

        // Write JSON
        var json = new
        {
            source = "Fallout_Release_MemDebug.pdb",
            tesFormSize = 40,
            generatedAt = DateTime.UtcNow.ToString("O"),
            types
        };

        var options = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        var jsonText = System.Text.Json.JsonSerializer.Serialize(json, options);
        await File.WriteAllTextAsync(outputPath, jsonText);

        Console.WriteLine($"Written to {outputPath} ({jsonText.Length:N0} bytes)");
        return 0;
    }
}

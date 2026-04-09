using System.Text.RegularExpressions;

namespace PdbAnalyzer.Commands;

/// <summary>
///     Search for structures matching a pattern.
/// </summary>
internal static class SearchStructuresCommand
{
    internal static async Task<int> ExecuteAsync(string cvdumpPath, string pattern)
    {
        if (!File.Exists(cvdumpPath))
        {
            Console.WriteLine($"File not found: {cvdumpPath}");
            return 1;
        }

        var parser = new CvdumpParser();
        await parser.ParseAsync(cvdumpPath);

        var regex = new Regex(pattern, RegexOptions.IgnoreCase);
        var matches = parser.Structures
            .Where(s => regex.IsMatch(s.Name) && s.Size > 0)
            .OrderBy(s => s.Name)
            .ToList();

        Console.WriteLine($"Found {matches.Count} structures matching '{pattern}':");
        Console.WriteLine();
        Console.WriteLine($"{"Structure",-50} {"Size",-8} {"Endian"}");
        Console.WriteLine(new string('-', 70));

        foreach (var s in matches) Console.WriteLine($"{s.Name,-50} {s.Size,-8} {(s.HasEndianMethod ? "Yes" : "")}");

        return 0;
    }
}

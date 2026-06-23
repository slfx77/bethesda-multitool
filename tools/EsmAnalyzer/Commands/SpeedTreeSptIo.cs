using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Parsing;
using BethesdaMultitool.Core.Formats.Bsa;

namespace EsmAnalyzer.Commands;

/// <summary>Shared I/O for loading SpeedTree <c>.spt</c> bytes from disk or a BSA archive.</summary>
internal static class SpeedTreeSptIo
{
    /// <summary>Load a <c>.spt</c> from disk, or from a BSA archive when <paramref name="bsa" /> is set.</summary>
    public static byte[]? LoadSptBytes(string path, string? bsa)
    {
        if (string.IsNullOrEmpty(bsa))
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        if (!File.Exists(bsa))
        {
            Console.Error.WriteLine($"BSA not found: {bsa}");
            return null;
        }

        var archive = BsaParser.Parse(bsa);
        using var extractor = new BsaExtractor(bsa);
        var norm = path.Replace('/', '\\');
        var rec = archive.AllFiles.FirstOrDefault(f =>
            string.Equals(f.FullPath?.Replace('/', '\\'), norm, StringComparison.OrdinalIgnoreCase));
        if (rec is null)
        {
            Console.Error.WriteLine($"Entry not found in BSA: {path}");
            return null;
        }

        return extractor.ExtractFile(rec);
    }
}

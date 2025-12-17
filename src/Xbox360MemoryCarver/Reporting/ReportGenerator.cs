using System.Text;
using Xbox360MemoryCarver.Models;
using Xbox360MemoryCarver.Utils;

namespace Xbox360MemoryCarver.Reporting;

/// <summary>
/// Generate extraction reports summarizing carved files and coverage statistics.
/// </summary>
public class ReportGenerator
{
    private readonly string _outputDir;
    private readonly ExtractionReport _report = new();
    private List<CarveEntry>? _manifestEntries;

    /// <summary>
    /// Human-readable names for file types.
    /// </summary>
    private static readonly Dictionary<string, string> TypeNames = new()
    {
        ["dds"] = "DDS Textures",
        ["ddx_3xdo"] = "Xbox 360 Textures (3XDO)",
        ["ddx_3xdr"] = "Xbox 360 Textures (3XDR)",
        ["xma"] = "XMA Audio",
        ["nif"] = "NIF Models",
        ["kf"] = "KF Animations",
        ["egm"] = "EGM Morph Data",
        ["egt"] = "EGT Texture Data",
        ["lip"] = "LIP Lip-Sync",
        ["bik"] = "Bink Video",
        ["script_scn"] = "Scripts (SCN)",
        ["script_sn"] = "Scripts (SN)",
        ["png"] = "PNG Images",
        ["wav"] = "WAV Audio",
        ["zlib_default"] = "Zlib Streams (Default)",
        ["zlib_best"] = "Zlib Streams (Best)",
        ["xex"] = "Xbox Executables",
        ["xdbf"] = "Xbox Data Files",
        ["xuis"] = "XUI Skins",
        ["xuib"] = "XUI Binary",
        ["pirs"] = "PIRS Packages",
        ["con"] = "CON Packages",
        ["esm"] = "ESM Master Files",
        ["esp"] = "ESP Plugin Files"
    };

    public ReportGenerator(string outputDir)
    {
        _outputDir = outputDir;
    }

    public void SetDumpInfo(string dumpPath, long dumpSize)
    {
        _report.DumpFile = Path.GetFileName(dumpPath);
        _report.DumpSize = dumpSize;
        _report.ExtractionTime = DateTime.Now;
    }

    public void AddCarvedFiles(List<CarveEntry> manifestEntries)
    {
        _manifestEntries = manifestEntries;

        // Store detailed manifest for JSON output
        _report.Manifest = manifestEntries;

        foreach (var entry in manifestEntries)
        {
            string fileType = entry.FileType;
            long size = entry.SizeInDump;
            string filename = entry.Filename;

            if (!_report.FilesByType.ContainsKey(fileType))
            {
                _report.FilesByType[fileType] = new FileTypeStats();
            }

            var stats = _report.FilesByType[fileType];
            stats.Count++;
            stats.TotalBytes += size;
            stats.Files.Add(filename);

            _report.TotalFilesCarved++;
            _report.TotalBytesCarved += size;

            // Track partial files
            if (entry.IsPartial)
            {
                _report.PartialFilesCount++;
            }
        }
    }

    public void CalculateCoverage(List<CarveEntry>? manifestEntries = null)
    {
        if (_report.DumpSize <= 0) return;

        manifestEntries ??= _manifestEntries;

        if (manifestEntries != null && manifestEntries.Count > 0)
        {
            // Extract and merge overlapping ranges
            var ranges = manifestEntries
                .Where(e => e.Offset >= 0 && e.SizeInDump > 0)
                .Select(e => (Start: e.Offset, End: e.Offset + e.SizeInDump))
                .OrderBy(r => r.Start)
                .ToList();

            var merged = MergeOverlappingRanges(ranges);
            _report.IdentifiedBytes = merged.Sum(r => r.End - r.Start);
        }
        else
        {
            _report.IdentifiedBytes = _report.TotalBytesCarved;
        }

        _report.UnknownBytes = _report.DumpSize - _report.IdentifiedBytes;
        _report.CoveragePercent = (double)_report.IdentifiedBytes / _report.DumpSize * 100;
    }

    private static List<(long Start, long End)> MergeOverlappingRanges(List<(long Start, long End)> ranges)
    {
        if (ranges.Count == 0) return new List<(long, long)>();

        var merged = new List<(long Start, long End)> { ranges[0] };

        foreach (var (start, end) in ranges.Skip(1))
        {
            var last = merged[^1];
            if (start <= last.End)
            {
                merged[^1] = (last.Start, Math.Max(last.End, end));
            }
            else
            {
                merged.Add((start, end));
            }
        }

        return merged;
    }

    public string GenerateTextReport()
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine(new string('=', 70));
        sb.AppendLine("XBOX 360 MEMORY DUMP EXTRACTION REPORT");
        sb.AppendLine(new string('=', 70));
        sb.AppendLine();
        sb.AppendLine($"Dump File:    {_report.DumpFile}");
        sb.AppendLine($"Dump Size:    {BinaryUtils.FormatSize(_report.DumpSize)}");
        sb.AppendLine($"Extracted:    {_report.ExtractionTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Version:      {_report.Version}");
        sb.AppendLine();

        // Carved Files Summary
        sb.AppendLine(new string('-', 70));
        sb.AppendLine("CARVED FILES");
        sb.AppendLine(new string('-', 70));
        sb.AppendLine($"Total Files:  {_report.TotalFilesCarved:N0}");
        sb.AppendLine($"Total Size:   {BinaryUtils.FormatSize(_report.TotalBytesCarved)}");
        sb.AppendLine();

        if (_report.FilesByType.Count > 0)
        {
            sb.AppendLine($"{"Type",-25} {"Count",10} {"Size",15}");
            sb.AppendLine(new string('-', 52));

            foreach (var (type, stats) in _report.FilesByType.OrderByDescending(x => x.Value.Count))
            {
                string typeName = TypeNames.TryGetValue(type, out var name) ? name : type;
                sb.AppendLine($"{typeName,-25} {stats.Count,10:N0} {BinaryUtils.FormatSize(stats.TotalBytes),15}");
            }
        }

        sb.AppendLine();

        // Coverage Statistics
        sb.AppendLine(new string('-', 70));
        sb.AppendLine("COVERAGE STATISTICS");
        sb.AppendLine(new string('-', 70));
        sb.AppendLine($"Identified:   {BinaryUtils.FormatSize(_report.IdentifiedBytes)} ({_report.CoveragePercent:F2}%)");
        sb.AppendLine($"Unknown:      {BinaryUtils.FormatSize(_report.UnknownBytes)} ({100 - _report.CoveragePercent:F2}%)");
        sb.AppendLine();

        // Progress bar
        sb.Append("Coverage: [");
        int barWidth = 50;
        int filled = (int)(_report.CoveragePercent / 100 * barWidth);
        sb.Append(new string('#', filled));
        sb.Append(new string('-', barWidth - filled));
        sb.AppendLine($"] {_report.CoveragePercent:F1}%");

        sb.AppendLine();
        sb.AppendLine(new string('=', 70));

        return sb.ToString();
    }

    public string GenerateJsonReport()
    {
        return System.Text.Json.JsonSerializer.Serialize(_report, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    public async Task SaveReportAsync(string? filename = null)
    {
        filename ??= "extraction_report.txt";
        var path = Path.Combine(_outputDir, filename);
        await File.WriteAllTextAsync(path, GenerateTextReport());

        var jsonPath = Path.ChangeExtension(path, ".json");
        await File.WriteAllTextAsync(jsonPath, GenerateJsonReport());
    }
}

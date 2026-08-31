using System.Collections.Concurrent;
using BethesdaMultitool.Core.Formats;

namespace BethesdaMultitool.Core.Carving;

/// <summary>
///     Handles file writing, conversion, and repair operations for carved files.
/// </summary>
internal sealed class CarveWriter(
    Dictionary<string, IFileConverter> converters,
    bool enableConversion,
    bool saveAtlas,
    Action<CarveEntry> addToManifest)
{
    private readonly Action<CarveEntry> _addToManifest = addToManifest;
    private readonly Dictionary<string, IFileConverter> _converters = converters;
    private readonly bool _enableConversion = enableConversion;
    private readonly ConcurrentBag<long> _failedConversionOffsets = [];
    private readonly bool _saveAtlas = saveAtlas;

    /// <summary>
    ///     Offsets of files that failed conversion (DDX -> DDS, XMA -> WAV, etc.).
    /// </summary>
    public IReadOnlyCollection<long> FailedConversionOffsets => _failedConversionOffsets;

    /// <summary>
    ///     Writes a carved file to disk, converting (e.g. DDX-&gt;DDS) or repairing it first when a handler applies, and
    ///     records a manifest entry.
    /// </summary>
    public async Task WriteFileAsync(WriteFileParams p)
    {
        var format = FormatRegistry.GetBySignatureId(p.SignatureId);

        // Try conversion if available for this format
        if (_enableConversion && format != null && _converters.TryGetValue(format.FormatId, out var converter) &&
            converter.CanConvert(p.SignatureId, p.Metadata))
        {
            var convertResult = await TryConvertAsync(converter, p);
            if (convertResult)
            {
                return;
            }
        }

        // Repair files if needed using IFileRepairer interface
        var outputData = p.Data;
        var isRepaired = false;
        if (format is IFileRepairer repairer && repairer.NeedsRepair(p.Metadata))
        {
            outputData = repairer.Repair(p.Data, p.Metadata);
            isRepaired = outputData != p.Data;
        }

        var writtenPath = await WriteFileWithRetryAsync(p.OutputFile, outputData);
        var entry = new CarveEntry
        {
            FileType = p.SignatureId,
            Offset = p.Offset,
            SizeInDump = p.FileSize,
            SizeOutput = outputData.Length,
            Filename = Path.GetFileName(writtenPath),
            OriginalPath = p.OriginalPath,
            IsPartial = p.Coverage.IsPartial,
            Notes = BuildNotes(p.Coverage, isRepaired, p.Metadata),
            Metadata = p.Metadata
        };

        ApplyResidency(entry, p, format, p.Data);
        _addToManifest(entry);
    }

    /// <summary>
    ///     Stamp the residency detail onto a manifest entry and, when the format can tell, whether a
    ///     hole landed somewhere structural. Applied to both write paths so a converted file is not
    ///     reported as complete when its source was gapped.
    /// </summary>
    private static void ApplyResidency(CarveEntry entry, WriteFileParams p, IFileFormat? format, byte[] sourceData)
    {
        var residency = p.Coverage;
        entry.Coverage = residency.Coverage;
        entry.TailTruncated = residency.TailTruncated;
        entry.Holes = residency.Holes.Count > 0 ? [.. residency.Holes] : null;

        if (residency.Holes.Count > 0 && format is IGapAssessor assessor)
        {
            entry.CriticalRangeHit = assessor.AssessGaps(sourceData, residency.Holes, p.Metadata);
            if (entry.CriticalRangeHit != null)
            {
                entry.Notes = AppendNote(entry.Notes, $"Gap hit {entry.CriticalRangeHit}");
            }
        }
    }

    private async Task<bool> TryConvertAsync(IFileConverter converter, WriteFileParams p)
    {
        var result = await converter.ConvertAsync(p.Data, p.Metadata);
        if (!result.Success || result.OutputData == null)
        {
            // Track that this file failed conversion
            _failedConversionOffsets.Add(p.Offset);
            return false;
        }

        var format = FormatRegistry.GetBySignatureId(p.SignatureId);
        var originalFolder = format?.OutputFolder ?? p.SignatureId;
        var targetFolder = converter.TargetFolder;

        var convertedOutputFile = Path.ChangeExtension(p.OutputFile.Replace(
            Path.DirectorySeparatorChar + originalFolder + Path.DirectorySeparatorChar,
            Path.DirectorySeparatorChar + targetFolder + Path.DirectorySeparatorChar), converter.TargetExtension);
        Directory.CreateDirectory(Path.GetDirectoryName(convertedOutputFile)!);

        var writtenPath = await WriteFileWithRetryAsync(convertedOutputFile, result.OutputData);

        // Save atlas if available
        if (result.AtlasData != null && _saveAtlas)
        {
            await WriteFileWithRetryAsync(convertedOutputFile.Replace(".dds", "_full_atlas.dds"), result.AtlasData);
        }

        // Residency must survive conversion. This branch used to build its entry purely from
        // `result.*` and return before the coverage-bearing path ran, so a DDX/XMA/NIF that was
        // only partly resident in the dump was recorded as IsPartial=false / "converted" /
        // Notes=null — indistinguishable from a fully captured file. DDX is converted by default
        // and is the corpus's dominant carved format, so that mislabelled the common case.
        var isPartial = result.IsPartial || p.Coverage.IsPartial;
        var entry = new CarveEntry
        {
            FileType = p.SignatureId,
            Offset = p.Offset,
            SizeInDump = p.FileSize,
            SizeOutput = result.OutputData.Length,
            Filename = Path.GetFileName(writtenPath),
            OriginalPath = p.OriginalPath,
            IsCompressed = true,
            ContentType = isPartial ? "converted_partial" : "converted",
            IsPartial = isPartial,
            Notes = AppendNote(
                AppendNote(result.Notes, BuildCoverageNote(p.Coverage)),
                BuildBoundaryFallbackNote(p.Metadata)),
            Metadata = p.Metadata
        };

        // Assess the gaps against the SOURCE bytes and the SOURCE format: the converter's output is
        // a different format whose offsets say nothing about where the dump's holes fell.
        ApplyResidency(entry, p, format, p.Data);
        _addToManifest(entry);

        return true;
    }

    private static string? BuildNotes(CarveResidency residency, bool isRepaired,
        IReadOnlyDictionary<string, object>? metadata)
    {
        // Not else-if: a file can be both incompletely resident AND repaired, and losing the
        // "Repaired" note in that case hid the repair.
        var baseNote = AppendNote(
            BuildCoverageNote(residency),
            isRepaired ? "Repaired" : null);

        return AppendNote(baseNote, BuildBoundaryFallbackNote(metadata));
    }

    /// <summary>
    ///     Note describing how much of a carved file was actually resident in the dump, and whether
    ///     what is missing is the tail or a hole in the middle.
    ///     <para>
    ///         P2 rather than P0: 99.6%-resident used to render as "Memory coverage: 100 %" beside
    ///         an IsPartial=true entry, which reads as a contradiction.
    ///     </para>
    /// </summary>
    private static string? BuildCoverageNote(CarveResidency residency)
    {
        if (!residency.IsPartial)
        {
            return null;
        }

        var note = $"Memory coverage: {residency.Coverage:P2}";
        if (residency.InteriorHoleCount > 0)
        {
            note += $" ({residency.InteriorHoleCount} interior hole" +
                    (residency.InteriorHoleCount == 1 ? ")" : "s)");
        }
        else if (residency.TailTruncated)
        {
            note += " (tail truncated)";
        }

        return note;
    }

    /// <summary>
    ///     Builds the "size estimated" note when a format's Parse flagged that no real
    ///     boundary was found and the size is a fallback estimate.
    /// </summary>
    private static string? BuildBoundaryFallbackNote(IReadOnlyDictionary<string, object>? metadata)
    {
        if (metadata == null ||
            !metadata.TryGetValue("boundaryFallback", out var flag) ||
            !IsTruthy(flag))
        {
            return null;
        }

        var note = "Size estimated (boundary fallback)";
        if (metadata.TryGetValue("boundaryFallbackReason", out var reasonObj) &&
            reasonObj is string { Length: > 0 } reason)
        {
            note = $"{note}: {reason}";
        }

        return note;
    }

    private static bool IsTruthy(object? value)
    {
        return value switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var parsed) && parsed,
            _ => false
        };
    }

    private static string? AppendNote(string? baseNote, string? extra)
    {
        if (extra == null)
        {
            return baseNote;
        }

        return baseNote == null ? extra : $"{baseNote}; {extra}";
    }

    /// <summary>
    ///     Write file with retry logic for handling concurrent access to same filename.
    ///     <para>
    ///         Returns the path actually written, which is NOT always the requested one: a
    ///         collision falls back to a GUID-suffixed name. Callers must record the returned path
    ///         in the manifest — recording the requested path instead is how manifest entries came
    ///         to name files that do not exist on disk.
    ///     </para>
    /// </summary>
    private static async Task<string> WriteFileWithRetryAsync(string outputFile, byte[] data, int maxRetries = 3)
    {
        var currentPath = outputFile;
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                await File.WriteAllBytesAsync(currentPath, data);
                return currentPath;
            }
            catch (IOException) when (attempt < maxRetries - 1)
            {
                var dir = Path.GetDirectoryName(outputFile)!;
                var nameWithoutExt = Path.GetFileNameWithoutExtension(outputFile);
                var ext = Path.GetExtension(outputFile);
                var suffix = Guid.NewGuid().ToString("N")[..8];
                currentPath = Path.Combine(dir, $"{nameWithoutExt}_{suffix}{ext}");
            }
        }

        return currentPath;
    }
}

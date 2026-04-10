using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Tests.Helpers;

/// <summary>
///     Loads a snippet binary + JSON manifest produced by <see cref="DmpSnippetExtractor" />
///     and provides a <see cref="SparseMemoryAccessor" /> and reconstructed metadata for fast tests.
/// </summary>
internal sealed class DmpSnippetReader
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<DmpSnippetReader>>> SnippetCache = new();

    private DmpSnippetReader(
        SparseMemoryAccessor accessor,
        MinidumpInfo minidumpInfo,
        long fileSize,
        DmpSnippetManifest manifest)
    {
        Accessor = accessor;
        MinidumpInfo = minidumpInfo;
        FileSize = fileSize;
        RuntimeEditorIds = manifest.RuntimeEditorIds;
        RuntimeRefrFormEntries = manifest.RuntimeRefrFormEntries;
        FormIdMap = manifest.FormIdMap;
    }

    public SparseMemoryAccessor Accessor { get; }
    public MinidumpInfo MinidumpInfo { get; }
    public long FileSize { get; }
    public List<RuntimeEditorIdEntry> RuntimeEditorIds { get; }
    public List<RuntimeEditorIdEntry> RuntimeRefrFormEntries { get; }
    public Dictionary<uint, string> FormIdMap { get; }

    /// <summary>
    ///     Full ESM record scan result, populated externally when available.
    ///     Needed by tests that use <see cref="RecordParser" />.
    /// </summary>
    public EsmRecordScanResult? ScanResult { get; set; }

    /// <summary>
    ///     Load a snippet from disk, caching the result across all tests in the run.
    ///     Safe for concurrent access — each snippet is loaded exactly once.
    /// </summary>
    public static Task<DmpSnippetReader> LoadCachedAsync(string snippetDir, string snippetName)
    {
        return SnippetCache.GetOrAdd(
            snippetName,
            name => new Lazy<Task<DmpSnippetReader>>(() => LoadAsync(snippetDir, name))
        ).Value;
    }

    /// <summary>
    ///     Load a snippet from disk (binary + JSON manifest).
    /// </summary>
    /// <param name="snippetDir">Directory containing the .bin and .json files.</param>
    /// <param name="snippetName">Base filename (e.g., "debug_dump").</param>
    public static async Task<DmpSnippetReader> LoadAsync(string snippetDir, string snippetName)
    {
        var gzPath = Path.Combine(snippetDir, $"{snippetName}.json.gz");
        var binPath = Path.Combine(snippetDir, $"{snippetName}.bin");

        await using var gzFs = File.OpenRead(gzPath);
        await using var gzStream = new GZipStream(gzFs, CompressionMode.Decompress);
        var manifest = await JsonSerializer.DeserializeAsync<DmpSnippetManifest>(gzStream)
                       ?? throw new InvalidOperationException($"Failed to deserialize manifest: {gzPath}");

        var accessor = new SparseMemoryAccessor();
        var binData = await File.ReadAllBytesAsync(binPath);

        // Populate sparse accessor from concatenated range data
        var pos = 0;
        foreach (var range in manifest.Ranges)
        {
            var chunk = new byte[range.Length];
            Array.Copy(binData, pos, chunk, 0, range.Length);
            accessor.AddRange(range.Offset, chunk);
            pos += range.Length;
        }

        var minidumpInfo = manifest.MinidumpInfo.ToMinidumpInfo();
        return new DmpSnippetReader(accessor, minidumpInfo, manifest.SourceFileSize, manifest);
    }

    /// <summary>
    ///     Create a <see cref="RuntimeStructReader" /> with auto-detection, replicating
    ///     what slow tests do with real DMP files.
    /// </summary>
    public RuntimeStructReader CreateStructReader()
    {
        var refrEntries = RuntimeEditorIds
            .Where(e => e.FormType is >= 0x3A and <= 0x3C)
            .ToList();
        var npcEntries = RuntimeEditorIds
            .Where(e => e.FormType == 0x2A)
            .ToList();
        var worldEntries = RuntimeEditorIds
            .Where(e => e.FormType == 0x41)
            .ToList();
        var cellEntries = RuntimeEditorIds
            .Where(e => e.FormType == 0x39)
            .ToList();

        return RuntimeStructReader.CreateWithAutoDetect(
            Accessor,
            FileSize,
            MinidumpInfo,
            refrEntries,
            npcEntries,
            worldEntries,
            cellEntries,
            RuntimeEditorIds);
    }

    /// <summary>
    ///     Create a <see cref="RuntimeStructReader" /> without probing (default offsets only),
    ///     for comparison tests like RuntimeProbeConsistencyTests.
    /// </summary>
    public RuntimeStructReader CreateStructReaderWithoutProbing()
    {
        var refrEntries = RuntimeEditorIds
            .Where(e => e.FormType is >= 0x3A and <= 0x3C)
            .ToList();
        var npcEntries = RuntimeEditorIds
            .Where(e => e.FormType == 0x2A)
            .ToList();
        var worldEntries = RuntimeEditorIds
            .Where(e => e.FormType == 0x41)
            .ToList();
        var cellEntries = RuntimeEditorIds
            .Where(e => e.FormType == 0x39)
            .ToList();

        return RuntimeStructReader.CreateWithAutoDetect(
            Accessor,
            FileSize,
            MinidumpInfo,
            refrEntries,
            npcEntries,
            worldEntries,
            cellEntries);
    }

    /// <summary>
    ///     Create a <see cref="RuntimeMemoryContext" /> for probe-level tests
    ///     that need direct context access.
    /// </summary>
    public RuntimeMemoryContext CreateMemoryContext()
    {
        return new RuntimeMemoryContext(Accessor, FileSize, MinidumpInfo);
    }

    /// <summary>
    ///     Create a <see cref="RecordParser" /> using the sparse accessor and scan result.
    ///     Requires the .scan.json file to have been saved during extraction.
    /// </summary>
    public RecordParser CreateRecordParser()
    {
        if (ScanResult == null)
        {
            throw new InvalidOperationException(
                "ScanResult not available. Ensure the .scan.json file exists alongside the snippet.");
        }

        return new RecordParser(
            ScanResult,
            FormIdMap,
            Accessor,
            FileSize,
            MinidumpInfo);
    }
}
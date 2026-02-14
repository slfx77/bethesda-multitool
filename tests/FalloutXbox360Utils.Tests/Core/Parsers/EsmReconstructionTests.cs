using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Parsers;

/// <summary>
///     Regression tests for ESM semantic reconstruction using real Xbox 360 ESM files.
///     Tests validate that compressed records are correctly decompressed and parsed.
///     Skipped automatically when sample files are not available.
/// </summary>
public class EsmReconstructionTests(ITestOutputHelper output, SampleFileFixture samples)
{
    private readonly ITestOutputHelper _output = output;

    /// <summary>
    ///     Builds an EsmRecordScanResult from the fast structured parser output.
    ///     Only MainRecords are needed — the accessor-based reconstruction reads subrecords directly.
    /// </summary>
    private static EsmRecordScanResult BuildScanResultFromParser(byte[] fileData)
    {
        var isBigEndian = EsmParser.IsBigEndian(fileData);
        var (records, _) = EsmParser.EnumerateRecordsWithGrups(fileData);

        var mainRecords = records.Select(r => new DetectedMainRecord(
            r.Header.Signature,
            r.Header.DataSize,
            r.Header.Flags,
            r.Header.FormId,
            r.Offset,
            isBigEndian)).ToList();

        return new EsmRecordScanResult { MainRecords = mainRecords };
    }

    [Fact]
    public void NpcReconstruction_CompressedRecord_ShouldParseAllFields()
    {
        Assert.SkipWhen(samples.Xbox360ProtoEsm is null, "Xbox 360 proto ESM not available");

        var fileData = File.ReadAllBytes(samples.Xbox360ProtoEsm!);
        _output.WriteLine($"File size: {fileData.Length:N0} bytes");

        var scanResult = BuildScanResultFromParser(fileData);
        _output.WriteLine($"Parsed {scanResult.MainRecords.Count:N0} records");

        using var mmf = MemoryMappedFile.CreateFromFile(samples.Xbox360ProtoEsm!, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileData.Length, MemoryMappedFileAccess.Read);

        var reconstructor = new RecordParser(scanResult, accessor: accessor, fileSize: fileData.Length);
        var npcs = reconstructor.ReconstructNpcs();
        _output.WriteLine($"Reconstructed {npcs.Count:N0} NPCs");

        // Boone: FormID 0x00092BD2, offset 0x00ADFDFC — a compressed NPC_ record
        var boone = npcs.FirstOrDefault(n => n.FormId == 0x00092BD2);
        Assert.NotNull(boone);
        Assert.Equal("CraigBoone", boone.EditorId);
        Assert.NotNull(boone.FullName);
        Assert.NotNull(boone.Stats);
        Assert.NotNull(boone.Race);

        _output.WriteLine($"Boone: EditorId={boone.EditorId}, FullName={boone.FullName}, " +
                          $"Race=0x{boone.Race:X8}, Factions={boone.Factions?.Count ?? 0}");
    }

    [Fact]
    public void CreatureReconstruction_ShouldParseSubrecords()
    {
        Assert.SkipWhen(samples.Xbox360ProtoEsm is null, "Xbox 360 proto ESM not available");

        var fileData = File.ReadAllBytes(samples.Xbox360ProtoEsm!);
        var scanResult = BuildScanResultFromParser(fileData);

        using var mmf = MemoryMappedFile.CreateFromFile(samples.Xbox360ProtoEsm!, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileData.Length, MemoryMappedFileAccess.Read);

        var reconstructor = new RecordParser(scanResult, accessor: accessor, fileSize: fileData.Length);
        var creatures = reconstructor.ReconstructCreatures();
        _output.WriteLine($"Reconstructed {creatures.Count:N0} creatures");

        // Verify at least some creatures have full data
        var withEditorId = creatures.Count(c => !string.IsNullOrEmpty(c.EditorId));
        var withFullName = creatures.Count(c => !string.IsNullOrEmpty(c.FullName));
        var withStats = creatures.Count(c => c.Stats != null);

        _output.WriteLine($"With EditorId: {withEditorId}, FullName: {withFullName}, Stats: {withStats}");

        Assert.True(withEditorId > 0, "Expected at least some creatures with EditorId");
        Assert.True(withFullName > 0, "Expected at least some creatures with FullName");
        Assert.True(withStats > 0, "Expected at least some creatures with Stats");
    }
}

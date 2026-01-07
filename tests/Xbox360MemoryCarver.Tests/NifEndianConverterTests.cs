using Xbox360MemoryCarver.Core.Formats.Nif;
using Xunit;
using Xunit.Abstractions;

namespace Xbox360MemoryCarver.Tests;

public class NifEndianConverterTests
{
    private readonly ITestOutputHelper _output;

    public NifEndianConverterTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ConvertPistolNif_RemovesBSPackedAdditionalGeometryData()
    {
        var inputPath = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? ".",
            @"source\repos\Xbox360MemoryCarver\Sample\meshes_360_final\meshes\weapons\1handpistol\10mmpistol.nif");

        if (!File.Exists(inputPath))
        {
            _output.WriteLine($"Test file not found: {inputPath}");
            return;
        }

        var data = File.ReadAllBytes(inputPath);
        _output.WriteLine($"Original file size: {data.Length} bytes");

        var converter = new NifEndianConverter(verbose: true);
        var converted = converter.ConvertToLittleEndian(data);

        Assert.NotNull(converted);
        _output.WriteLine($"Converted file size: {converted.Length} bytes");
        _output.WriteLine($"Size difference: {data.Length - converted.Length} bytes removed");

        // The converted file should be smaller (BSPackedAdditionalGeometryData blocks removed)
        Assert.True(converted.Length < data.Length, "Converted file should be smaller with stripped blocks");

        // Save for manual inspection
        var outputPath = Path.Combine(Path.GetDirectoryName(inputPath)!, "..", "..", "..", "..", "TestOutput", "10mmpistol_converted.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, converted);
        _output.WriteLine($"Saved to: {outputPath}");
    }
}

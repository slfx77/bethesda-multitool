using System.Collections.Concurrent;
using System.Text.Json;
using DDXConv;
using FalloutXbox360Utils.Core.Carving;
using FalloutXbox360Utils.Core.Extraction;
using FalloutXbox360Utils.Core.Minidump;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;
using Xunit.Sdk;

namespace FalloutXbox360Utils.Tests.Core.Formats.Ddx;

public sealed class DdxDmpMipRegressionTests
{
    public enum ComparisonMode
    {
        ExactAlignedRgb,
        CountAndAlignedDimensions
    }

    public enum DdxKind
    {
        Xdo,
        Xdr
    }

    private static readonly ConcurrentDictionary<string, Lazy<Task<CarvedDumpContext>>> CarvedDumpCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions MetricsJsonOptions = new() { WriteIndented = true };

    public static TheoryData<CarvedRegressionCase> RepresentativeCases =>
    [
        new CarvedRegressionCase(
            "debug_nv_reflectron_rm",
            @"Sample\MemoryDump\Fallout_Debug.xex.dmp",
            "nv_reflectron_rm.ddx",
            @"textures\terminals\nv_reflectron_rm.ddx",
            DdxKind.Xdo,
            9,
            0,
            ComparisonMode.ExactAlignedRgb,
            0),
        new CarvedRegressionCase(
            "debug_rugsmall01_tail",
            @"Sample\MemoryDump\Fallout_Debug.xex.dmp",
            "rugsmall01.ddx",
            @"textures\clutter\rugs\rugsmall01.ddx",
            DdxKind.Xdo,
            10,
            0,
            ComparisonMode.ExactAlignedRgb,
            1),
        new CarvedRegressionCase(
            "debug_anesthesiamachine01_lod",
            @"Sample\MemoryDump\Fallout_Debug.xex.dmp",
            "anesthesiamachine01.ddx",
            @"textures\clutter\hospital\anesthesiamachine01.ddx",
            DdxKind.Xdo,
            7,
            1,
            ComparisonMode.CountAndAlignedDimensions,
            0),
        new CarvedRegressionCase(
            "debug_impactdecalglass01_n_lod",
            @"Sample\MemoryDump\Fallout_Debug.xex.dmp",
            "impactdecalglass01_n.ddx",
            @"textures\decals\impactdecalglass01_n.ddx",
            DdxKind.Xdo,
            9,
            1,
            ComparisonMode.CountAndAlignedDimensions,
            0),
        new CarvedRegressionCase(
            "debug_nv_reflectron_m_lod",
            @"Sample\MemoryDump\Fallout_Debug.xex.dmp",
            "nv_reflectron_m.ddx",
            @"textures\terminals\nv_reflectron_m.ddx",
            DdxKind.Xdo,
            6,
            1,
            ComparisonMode.CountAndAlignedDimensions,
            0),
        new CarvedRegressionCase(
            "debug_med_history_ok_btn_on_3xdr",
            @"Sample\MemoryDump\Fallout_Debug.xex.dmp",
            "med_history_ok_btn_on.ddx",
            @"textures\terminals\med_history_ok_btn_on.ddx",
            DdxKind.Xdr,
            1,
            0,
            ComparisonMode.ExactAlignedRgb,
            0),
        new CarvedRegressionCase(
            "release2_terminalscreen01",
            @"Sample\MemoryDump\Fallout_Release_Beta.xex2.dmp",
            "terminalscreen01.ddx",
            @"textures\terminals\terminalscreen01.ddx",
            DdxKind.Xdo,
            9,
            0,
            ComparisonMode.ExactAlignedRgb,
            0),
        new CarvedRegressionCase(
            "release2_offrmtrimglass02",
            @"Sample\MemoryDump\Fallout_Release_Beta.xex2.dmp",
            "offrmtrimglass02.ddx",
            @"textures\dungeons\office\offrmtrimglass02.ddx",
            DdxKind.Xdo,
            10,
            0,
            ComparisonMode.ExactAlignedRgb,
            0),
        new CarvedRegressionCase(
            "release2_offswitches01",
            @"Sample\MemoryDump\Fallout_Release_Beta.xex2.dmp",
            "offswitches01.ddx",
            @"textures\dungeons\office\offswitches01.ddx",
            DdxKind.Xdo,
            9,
            0,
            ComparisonMode.ExactAlignedRgb,
            0),
        new CarvedRegressionCase(
            "release2_hairwavy_lod1",
            @"Sample\MemoryDump\Fallout_Release_Beta.xex2.dmp",
            "hairwavy_2.ddx",
            @"textures\characters\hair\hairwavy.ddx",
            DdxKind.Xdo,
            7,
            1,
            ComparisonMode.CountAndAlignedDimensions,
            0),
        new CarvedRegressionCase(
            "release2_outfitweatheredm_n_lod2",
            @"Sample\MemoryDump\Fallout_Release_Beta.xex2.dmp",
            "outfitweatheredm_n.ddx",
            @"textures\armor\1950stylesuit\outfitweatheredm_n.ddx",
            DdxKind.Xdo,
            6,
            2,
            ComparisonMode.CountAndAlignedDimensions,
            0),
        new CarvedRegressionCase(
            "release2_handfemale_sk_3xdr",
            @"Sample\MemoryDump\Fallout_Release_Beta.xex2.dmp",
            "handfemale_sk.ddx",
            @"textures\characters\female\handfemale_sk.ddx",
            DdxKind.Xdr,
            1,
            0,
            ComparisonMode.CountAndAlignedDimensions,
            0)
    ];

    [Theory]
    [MemberData(nameof(RepresentativeCases))]
    public async Task RepresentativeCases_PreserveExpectedMipBehavior(CarvedRegressionCase regressionCase)
    {
        var dumpPath = SampleFileFixture.FindSamplePath(regressionCase.DumpRelativePath);
        Assert.SkipWhen(dumpPath is null, $"Missing sample dump: {regressionCase.DumpRelativePath}");

        var repoRoot = FindRepoRoot();
        var carvedDump = await EnsureCarvedDumpAsync(dumpPath!);

        var entry = carvedDump.Entries.SingleOrDefault(e =>
            string.Equals(e.Filename, regressionCase.CarvedFilename, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.OriginalPath, regressionCase.OriginalPath, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry);

        var ddxPath = Path.Combine(carvedDump.DdxDirectory, entry!.Filename);
        Assert.True(File.Exists(ddxPath), $"Missing carved DDX file: {ddxPath}");

        Assert.Equal(regressionCase.Kind, ReadDdxKind(ddxPath));

        var referenceDdsPath = Path.Combine(repoRoot, "Sample", "Unpacked_Builds", "PC_Final_Unpacked", "Data",
                regressionCase.OriginalPath)
            .Replace(".ddx", ".dds", StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(referenceDdsPath), $"Missing PC reference DDS: {referenceDdsPath}");

        var artifactRoot = Path.Combine(repoRoot, "TestOutput", "ddx_dmp_regression", regressionCase.Label);
        ResetDirectory(artifactRoot);

        var outputDds = Path.Combine(artifactRoot, Path.GetFileNameWithoutExtension(entry.Filename) + ".dds");
        var referenceCopy = Path.Combine(artifactRoot, "pc_" + Path.GetFileName(referenceDdsPath));
        File.Copy(referenceDdsPath, referenceCopy, true);

        var parser = new DdxParser();
        parser.ConvertDdxToDds(ddxPath, outputDds, new ConversionOptions { SaveAtlas = true });

        var xboxMipPngs = DdsPostProcessor.ExportMipImages(outputDds);
        var referenceMipPngs = DdsPostProcessor.ExportMipImages(referenceCopy);

        Assert.Equal(regressionCase.ExpectedMipCount, xboxMipPngs.Length);

        var actualReferenceOffset = FindReferenceOffsetByDimensions(xboxMipPngs, referenceMipPngs);
        Assert.Equal(regressionCase.ExpectedReferenceOffset, actualReferenceOffset);

        var metrics = new RegressionMetrics(
            regressionCase.Label,
            regressionCase.Kind.ToString(),
            regressionCase.Mode.ToString(),
            regressionCase.ExpectedMipCount,
            xboxMipPngs.Length,
            actualReferenceOffset,
            regressionCase.CompareStartActualMip,
            [],
            []);

        switch (regressionCase.Mode)
        {
            case ComparisonMode.ExactAlignedRgb:
                metrics = metrics with
                {
                    ComparedMipMeanAbsoluteRgbError = AssertExactAlignedPrefix(
                        xboxMipPngs,
                        referenceMipPngs,
                        actualReferenceOffset,
                        regressionCase.CompareStartActualMip)
                };
                break;

            case ComparisonMode.CountAndAlignedDimensions:
                metrics = metrics with
                {
                    ComparedMipDimensions = AssertAlignedDimensions(
                        xboxMipPngs,
                        referenceMipPngs,
                        actualReferenceOffset)
                };
                break;

            default:
                throw new InvalidOperationException($"Unexpected comparison mode: {regressionCase.Mode}");
        }

        await File.WriteAllTextAsync(
            Path.Combine(artifactRoot, "metrics.json"),
            JsonSerializer.Serialize(metrics, MetricsJsonOptions),
            TestContext.Current.CancellationToken);

        WriteComparisonSheet(
            regressionCase.Label,
            artifactRoot,
            xboxMipPngs,
            referenceMipPngs,
            actualReferenceOffset);
    }

    private static async Task<CarvedDumpContext> EnsureCarvedDumpAsync(string dumpPath)
    {
        return await CarvedDumpCache.GetOrAdd(
            Path.GetFullPath(dumpPath),
            path => new Lazy<Task<CarvedDumpContext>>(
                () => CarveDumpAsync(path),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static async Task<CarvedDumpContext> CarveDumpAsync(string dumpPath)
    {
        var repoRoot = FindRepoRoot();
        var cacheRoot = Path.Combine(repoRoot, "TestOutput", "ddx_dmp_regression", "cache");
        Directory.CreateDirectory(cacheRoot);

        var dumpName = Path.GetFileNameWithoutExtension(dumpPath);
        var extractRoot = Path.Combine(cacheRoot, dumpName);
        var manifestPath = Path.Combine(extractRoot, "manifest.json");
        var ddxDirectory = Path.Combine(extractRoot, "ddx");

        if (!File.Exists(manifestPath) || !Directory.Exists(ddxDirectory))
        {
            _ = await MinidumpExtractor.Extract(
                dumpPath,
                new ExtractionOptions
                {
                    OutputPath = cacheRoot,
                    ConvertDdx = false,
                    SaveAtlas = false,
                    FileTypes = ["ddx"],
                    GenerateEsmReports = false,
                    ExtractScripts = false,
                    Verbose = false,
                    MaxFilesPerType = 200
                },
                null);
        }

        var entries = await CarveManifest.LoadAsync(manifestPath);
        return new CarvedDumpContext(extractRoot, ddxDirectory, entries);
    }

    private static DdxKind ReadDdxKind(string ddxPath)
    {
        using var stream = File.OpenRead(ddxPath);
        using var reader = new BinaryReader(stream);
        return reader.ReadUInt32() switch
        {
            0x4F445833u => DdxKind.Xdo,
            0x52445833u => DdxKind.Xdr,
            var magic => throw new InvalidDataException(
                $"Unexpected DDX magic 0x{magic:X8} in {ddxPath}.")
        };
    }

    private static int FindReferenceOffsetByDimensions(
        string[] actualMipPngs,
        string[] referenceMipPngs)
    {
        using var actualMip0 = Image.Load<Rgba32>(actualMipPngs[0]);
        for (var offset = 0; offset < referenceMipPngs.Length; offset++)
        {
            using var referenceMip = Image.Load<Rgba32>(referenceMipPngs[offset]);
            if (referenceMip.Width == actualMip0.Width && referenceMip.Height == actualMip0.Height)
            {
                return offset;
            }
        }

        throw new XunitException(
            $"Could not align converted mip0 size {actualMip0.Width}x{actualMip0.Height} to any reference mip.");
    }

    private static List<double> AssertExactAlignedPrefix(
        string[] actualMipPngs,
        string[] referenceMipPngs,
        int referenceOffset,
        int compareStartActualMip)
    {
        var errors = new List<double>();
        var compareCount = Math.Min(actualMipPngs.Length, referenceMipPngs.Length - referenceOffset);

        for (var actualMipIndex = compareStartActualMip; actualMipIndex < compareCount; actualMipIndex++)
        {
            var referenceMipIndex = referenceOffset + actualMipIndex;
            using var actualMip = Image.Load<Rgba32>(actualMipPngs[actualMipIndex]);
            using var referenceMip = Image.Load<Rgba32>(referenceMipPngs[referenceMipIndex]);

            Assert.Equal(referenceMip.Width, actualMip.Width);
            Assert.Equal(referenceMip.Height, actualMip.Height);

            var error = ComputeMeanAbsoluteRgbError(actualMip, referenceMip);
            var maxAllowedError = Math.Max(actualMip.Width, actualMip.Height) <= 4 ? 4.0 : 1.0;
            Assert.InRange(error, 0.0, maxAllowedError);
            errors.Add(error);
        }

        return errors;
    }

    private static List<string> AssertAlignedDimensions(
        string[] actualMipPngs,
        string[] referenceMipPngs,
        int referenceOffset)
    {
        var dimensions = new List<string>(actualMipPngs.Length);
        var compareCount = Math.Min(actualMipPngs.Length, referenceMipPngs.Length - referenceOffset);

        Assert.Equal(actualMipPngs.Length, compareCount);

        for (var actualMipIndex = 0; actualMipIndex < compareCount; actualMipIndex++)
        {
            using var actualMip = Image.Load<Rgba32>(actualMipPngs[actualMipIndex]);
            using var referenceMip = Image.Load<Rgba32>(referenceMipPngs[referenceOffset + actualMipIndex]);

            Assert.Equal(referenceMip.Width, actualMip.Width);
            Assert.Equal(referenceMip.Height, actualMip.Height);

            dimensions.Add(
                $"{actualMipIndex}:{actualMip.Width}x{actualMip.Height}->pc{referenceOffset + actualMipIndex}");
        }

        return dimensions;
    }

    private static double ComputeMeanAbsoluteRgbError(Image<Rgba32> actual, Image<Rgba32> expected)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);

        double total = 0;
        var samples = actual.Width * actual.Height * 3;

        for (var y = 0; y < actual.Height; y++)
        {
            for (var x = 0; x < actual.Width; x++)
            {
                var actualPixel = actual[x, y];
                var expectedPixel = expected[x, y];
                total += Math.Abs(actualPixel.R - expectedPixel.R);
                total += Math.Abs(actualPixel.G - expectedPixel.G);
                total += Math.Abs(actualPixel.B - expectedPixel.B);
            }
        }

        return total / samples;
    }

    private static void WriteComparisonSheet(
        string label,
        string artifactRoot,
        string[] actualMipPngs,
        string[] referenceMipPngs,
        int referenceOffset)
    {
        var rowCount = Math.Min(Math.Min(actualMipPngs.Length, referenceMipPngs.Length - referenceOffset), 6);
        const int cellSize = 160;
        const int gap = 12;

        using var sheet = new Image<Rgba32>(
            cellSize * 2 + gap,
            rowCount * (cellSize + gap) - gap,
            new Rgba32(255, 255, 255, 255));

        for (var row = 0; row < rowCount; row++)
        {
            using var actualMip = Image.Load<Rgba32>(actualMipPngs[row]);
            using var referenceMip = Image.Load<Rgba32>(referenceMipPngs[referenceOffset + row]);
            using var scaledActual = ResizeForCell(actualMip, cellSize);
            using var scaledReference = ResizeForCell(referenceMip, cellSize);

            var y = row * (cellSize + gap);
            PasteCentered(sheet, scaledActual, 0, y, cellSize, cellSize);
            PasteCentered(sheet, scaledReference, cellSize + gap, y, cellSize, cellSize);
        }

        sheet.SaveAsPng(Path.Combine(artifactRoot, $"{label}_comparison.png"));
    }

    private static Image<Rgba32> ResizeForCell(Image<Rgba32> image, int cellSize)
    {
        var scale = Math.Min((double)cellSize / image.Width, (double)cellSize / image.Height);
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        return image.Clone(ctx => ctx.Resize(width, height, KnownResamplers.NearestNeighbor));
    }

    private static void PasteCentered(
        Image<Rgba32> canvas,
        Image<Rgba32> image,
        int cellX,
        int cellY,
        int cellWidth,
        int cellHeight)
    {
        var x = cellX + (cellWidth - image.Width) / 2;
        var y = cellY + (cellHeight - image.Height) / 2;
        canvas.Mutate(ctx => ctx.DrawImage(image, new Point(x, y), 1f));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Sample")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repo root containing the Sample directory.");
    }

    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }

        Directory.CreateDirectory(path);
    }

    public sealed record CarvedRegressionCase(
        string Label,
        string DumpRelativePath,
        string CarvedFilename,
        string OriginalPath,
        DdxKind Kind,
        int ExpectedMipCount,
        int ExpectedReferenceOffset,
        ComparisonMode Mode,
        int CompareStartActualMip);

    private sealed record CarvedDumpContext(
        string ExtractRoot,
        string DdxDirectory,
        IReadOnlyList<CarveEntry> Entries);

    private sealed record RegressionMetrics(
        string Label,
        string DdxKind,
        string Mode,
        int ExpectedMipCount,
        int ActualMipCount,
        int ReferenceOffset,
        int CompareStartActualMip,
        IReadOnlyList<double> ComparedMipMeanAbsoluteRgbError,
        IReadOnlyList<string> ComparedMipDimensions);
}
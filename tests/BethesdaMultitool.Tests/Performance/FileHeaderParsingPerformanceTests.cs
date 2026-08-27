using System.Buffers;
using System.Diagnostics;
using BethesdaMultitool.Core.Formats.Ddx;
using BethesdaMultitool.Core.Formats.Nif;
using Xunit;

namespace BethesdaMultitool.Tests.Performance;

/// <summary>
///     Covers the header classifiers used by the NIF/DDX converter file lists, both directly and
///     through the concurrent directory-scan path the GUI uses.
///     <para>
///         These tests previously called private <c>DetermineNifFormat</c>/<c>DetermineDdxFormat</c>
///         copies declared in this file under a "same logic as UI code" region. That made them
///         tautologies — they asserted the test file agreed with itself and could not fail when
///         production changed. The real rules now live in
///         <see cref="NifHeaderFormat" /> and <see cref="DdxHeaderFormat" /> (in <c>Core/</c>,
///         reachable from <c>net10.0</c>) and the GUI calls those, so these assertions finally
///         bind to shipping behaviour.
///     </para>
/// </summary>
public sealed class FileHeaderParsingPerformanceTests : IDisposable
{
    private const int MaxConcurrentReads = 8;

    /// <summary>Enough files to span several subdirectories and exceed <see cref="MaxConcurrentReads" />.</summary>
    private const int ScanFileCount = 20;

    private readonly string _tempDir;

    public FileHeaderParsingPerformanceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"HeaderParseTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    public static TheoryData<byte[], string, string> NifHeaderCases => new()
    {
        { CreateNifHeader(isXbox360: true), NifHeaderFormat.Xbox360, "endian byte 0 = big-endian" },
        { CreateNifHeader(isXbox360: false), NifHeaderFormat.Pc, "endian byte 1 = little-endian" },
        { CreateNifHeader(endianByte: 7), NifHeaderFormat.Unknown, "endian byte is neither 0 nor 1" },
        { new byte[NifHeaderFormat.RequiredHeaderBytes - 1], NifHeaderFormat.Invalid, "one byte short of a header" },
        { [], NifHeaderFormat.Invalid, "empty input" },
        { new byte[NifHeaderFormat.RequiredHeaderBytes], NifHeaderFormat.Invalid, "all zeroes: no newline terminator" },
        { CreateNifHeaderWithNewlineAt(0), NifHeaderFormat.Invalid, "newline at index 0 = empty version string" },
        {
            CreateNifHeaderWithNewlineAt(NifHeaderFormat.RequiredHeaderBytes - 3),
            NifHeaderFormat.Invalid, "newline too close to the end to carry an endian byte"
        }
    };

    public static TheoryData<byte[], string, string> DdxHeaderCases => new()
    {
        { "3XDO"u8.ToArray(), DdxHeaderFormat.Xdo, "linear Xbox 360 DDX" },
        { "3XDR"u8.ToArray(), DdxHeaderFormat.Xdr, "engine-tiled Xbox 360 DDX" },
        { "3XDZ"u8.ToArray(), DdxHeaderFormat.Invalid, "known prefix, unknown variant byte" },
        { "XXXX"u8.ToArray(), DdxHeaderFormat.Invalid, "not a DDX magic at all" },
        { "DDS "u8.ToArray(), DdxHeaderFormat.Invalid, "a PC DDS, not a DDX" },
        { "3XD"u8.ToArray(), DdxHeaderFormat.Invalid, "one byte short of the magic" },
        { [], DdxHeaderFormat.Invalid, "empty input" }
    };

    [Theory]
    [MemberData(nameof(NifHeaderCases))]
    public void Describe_NifHeader_ClassifiesEndianness(byte[] header, string expected, string because)
    {
        _ = because; // Surfaces the equivalence class in the test-case display name.

        Assert.Equal(expected, NifHeaderFormat.Describe(header));
    }

    [Theory]
    [MemberData(nameof(DdxHeaderCases))]
    public void Describe_DdxHeader_ClassifiesVariant(byte[] header, string expected, string because)
    {
        _ = because;

        Assert.Equal(expected, DdxHeaderFormat.Describe(header));
    }

    [Fact]
    public async Task ScanAndParseHeaders_NifFiles_ClassifiesEveryFileInEverySubdirectory()
    {
        CreateTestFiles(ScanFileCount, ".nif", CreateNifHeader(isXbox360: true));

        var results = await ScanAsync("*.nif", ReadNifHeaderAsync);

        Assert.Equal(ScanFileCount, results.Length);
        Assert.All(results, r => Assert.Equal(NifHeaderFormat.Xbox360, r.Format));
    }

    [Fact]
    public async Task ScanAndParseHeaders_DdxFiles_ClassifiesEveryFileInEverySubdirectory()
    {
        const int count = 10;
        CreateTestFiles(count, ".ddx", "3XDO"u8.ToArray());

        var results = await ScanAsync("*.ddx", ReadDdxHeaderAsync);

        Assert.Equal(count, results.Length);
        Assert.All(results, r => Assert.Equal(DdxHeaderFormat.Xdo, r.Format));
    }

    [Fact]
    public async Task ScanAndParseHeaders_MixedNifEndianness_SeparatesXboxFromPc()
    {
        const int perFormat = 10;
        await WriteNifFilesAsync("xbox", perFormat, isXbox360: true);
        await WriteNifFilesAsync("pc", perFormat, isXbox360: false);

        var results = await ScanAsync("*.nif", ReadNifHeaderAsync);

        Assert.Equal(perFormat * 2, results.Length);
        Assert.Equal(perFormat, results.Count(r => r.Format == NifHeaderFormat.Xbox360));
        Assert.Equal(perFormat, results.Count(r => r.Format == NifHeaderFormat.Pc));
    }

    [Fact]
    public async Task ReadNifHeaderAsync_UnreadableFile_ReportsErrorRatherThanThrowing()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist.nif");

        var (size, format) = await ReadNifHeaderAsync(missing);

        Assert.Equal(0, size);
        Assert.Equal(NifHeaderFormat.Error, format);
    }

    /// <summary>
    ///     Runs the concurrent scan the converter tabs use and returns every result. The stopwatch
    ///     is reported for manual profiling only — timing assertions are inherently flaky under
    ///     parallel test execution, since other CPU-heavy tests in the session can starve this one.
    /// </summary>
    private async Task<(string Path, long Size, string Format)[]> ScanAsync(
        string pattern,
        Func<string, Task<(long FileSize, string Format)>> readHeader)
    {
        var files = Directory.EnumerateFiles(_tempDir, pattern, SearchOption.AllDirectories).ToList();
        var results = new (string Path, long Size, string Format)[files.Count];

        var sw = Stopwatch.StartNew();
        using var semaphore = new SemaphoreSlim(MaxConcurrentReads);
        var tasks = files.Select((path, i) => Task.Run(async () =>
        {
            await semaphore.WaitAsync(TestContext.Current.CancellationToken);
            try
            {
                var (size, format) = await readHeader(path);
                results[i] = (path, size, format);
            }
            finally
            {
                semaphore.Release();
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        sw.Stop();
        _ = sw.ElapsedMilliseconds;

        return results;
    }

    private async Task WriteNifFilesAsync(string prefix, int count, bool isXbox360)
    {
        var header = CreateNifHeader(isXbox360);
        for (var i = 0; i < count; i++)
        {
            var filePath = Path.Combine(_tempDir, $"{prefix}_{i:D3}.nif");
            await File.WriteAllBytesAsync(filePath, header, TestContext.Current.CancellationToken);
        }
    }

    private void CreateTestFiles(int count, string extension, byte[] header)
    {
        for (var i = 0; i < count; i++)
        {
            var subDir = Path.Combine(_tempDir, $"subdir{i % 10}");
            Directory.CreateDirectory(subDir);

            var filePath = Path.Combine(subDir, $"file{i:D5}{extension}");
            File.WriteAllBytes(filePath, header);
        }
    }

    #region Header reading — mirrors the converter tabs' async read path

    private static async Task<(long FileSize, string Format)> ReadNifHeaderAsync(string filePath)
    {
        var headerBytes = ArrayPool<byte>.Shared.Rent(NifHeaderFormat.RequiredHeaderBytes);
        try
        {
            var fileSize = new FileInfo(filePath).Length;

            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytesRead = await fs.ReadAsync(
                headerBytes.AsMemory(0, NifHeaderFormat.RequiredHeaderBytes),
                TestContext.Current.CancellationToken);

            return (fileSize, NifHeaderFormat.Describe(headerBytes.AsSpan(0, bytesRead)));
        }
        catch (IOException)
        {
            return (0, NifHeaderFormat.Error);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBytes);
        }
    }

    private static async Task<(long FileSize, string Format)> ReadDdxHeaderAsync(string filePath)
    {
        var headerBytes = ArrayPool<byte>.Shared.Rent(DdxHeaderFormat.RequiredHeaderBytes);
        try
        {
            var fileSize = new FileInfo(filePath).Length;

            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytesRead = await fs.ReadAsync(
                headerBytes.AsMemory(0, DdxHeaderFormat.RequiredHeaderBytes),
                TestContext.Current.CancellationToken);

            return (fileSize, DdxHeaderFormat.Describe(headerBytes.AsSpan(0, bytesRead)));
        }
        catch (IOException)
        {
            return (0, DdxHeaderFormat.Error);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBytes);
        }
    }

    #endregion

    #region Synthetic NIF headers

    private static byte[] CreateNifHeader(bool isXbox360)
    {
        return CreateNifHeader(endianByte: (byte)(isXbox360 ? 0 : 1));
    }

    /// <summary>
    ///     A minimal 50-byte NIF header: a newline-terminated version string, then the endian
    ///     byte five bytes past the terminator (the 4-byte binary version sits between them).
    /// </summary>
    private static byte[] CreateNifHeader(byte endianByte)
    {
        var header = new byte[NifHeaderFormat.RequiredHeaderBytes];
        "Gamebryo File Format, Version 20.2.0.7\n"u8.ToArray().CopyTo(header, 0);

        var newlinePos = Array.IndexOf(header, (byte)0x0A);
        header[newlinePos + 5] = endianByte;
        return header;
    }

    /// <summary>A 50-byte buffer whose only newline sits at <paramref name="index" />.</summary>
    private static byte[] CreateNifHeaderWithNewlineAt(int index)
    {
        var header = new byte[NifHeaderFormat.RequiredHeaderBytes];
        Array.Fill(header, (byte)'A');
        header[index] = 0x0A;
        return header;
    }

    #endregion
}

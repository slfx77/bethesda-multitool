using System.Buffers;
using System.Diagnostics;
using Xunit;

namespace Xbox360MemoryCarver.Tests.Performance;

/// <summary>
///     Performance tests for file scanning and header parsing logic.
///     These tests measure the time to scan directories and parse file headers.
/// </summary>
public sealed class FileHeaderParsingPerformanceTests : IDisposable
{
    private const int MaxConcurrentReads = 8;
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

    [Fact]
    public async Task ScanAndParseHeaders_100NifFiles_CompletesUnder500ms()
    {
        // Arrange - Create 100 test NIF files with valid Xbox 360 header
        var header = CreateNifHeader(true);
        CreateTestFiles(100, ".nif", header);

        var sw = Stopwatch.StartNew();

        // Act - Scan and parse all files
        var files = Directory.EnumerateFiles(_tempDir, "*.nif", SearchOption.AllDirectories).ToList();
        var results = new (string path, long size, string format)[files.Count];

        using var semaphore = new SemaphoreSlim(MaxConcurrentReads);
        var tasks = files.Select((path, i) => Task.Run(async () =>
        {
            await semaphore.WaitAsync();
            try
            {
                var (size, format) = await ReadNifHeaderAsync(path);
                results[i] = (path, size, format);
            }
            finally
            {
                semaphore.Release();
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        sw.Stop();

        // Assert
        Assert.Equal(100, results.Length);
        Assert.All(results, r => Assert.Equal("Xbox 360 (BE)", r.format));
        Assert.True(sw.ElapsedMilliseconds < 500, $"Expected < 500ms, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ScanAndParseHeaders_1000NifFiles_CompletesUnder2000ms()
    {
        // Arrange
        var header = CreateNifHeader(true);
        CreateTestFiles(1000, ".nif", header);

        var sw = Stopwatch.StartNew();

        // Act
        var files = Directory.EnumerateFiles(_tempDir, "*.nif", SearchOption.AllDirectories).ToList();
        var results = new (string path, long size, string format)[files.Count];

        using var semaphore = new SemaphoreSlim(MaxConcurrentReads);
        var tasks = files.Select((path, i) => Task.Run(async () =>
        {
            await semaphore.WaitAsync();
            try
            {
                var (size, format) = await ReadNifHeaderAsync(path);
                results[i] = (path, size, format);
            }
            finally
            {
                semaphore.Release();
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        sw.Stop();

        // Assert
        Assert.Equal(1000, results.Length);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Expected < 2000ms, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ScanAndParseHeaders_100DdxFiles_CompletesUnder500ms()
    {
        // Arrange
        var header = "3XDO"u8.ToArray();
        CreateTestFiles(100, ".ddx", header);

        var sw = Stopwatch.StartNew();

        // Act
        var files = Directory.EnumerateFiles(_tempDir, "*.ddx", SearchOption.AllDirectories).ToList();
        var results = new (string path, long size, string format)[files.Count];

        using var semaphore = new SemaphoreSlim(MaxConcurrentReads);
        var tasks = files.Select((path, i) => Task.Run(async () =>
        {
            await semaphore.WaitAsync();
            try
            {
                var (size, format) = await ReadDdxHeaderAsync(path);
                results[i] = (path, size, format);
            }
            finally
            {
                semaphore.Release();
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        sw.Stop();

        // Assert
        Assert.Equal(100, results.Length);
        Assert.All(results, r => Assert.Equal("3XDO", r.format));
        Assert.True(sw.ElapsedMilliseconds < 500, $"Expected < 500ms, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ScanAndParseHeaders_MixedFormats_CorrectlyParses()
    {
        // Arrange - Create mix of Xbox 360 and PC NIF files
        var xbox360Header = CreateNifHeader(true);
        var pcHeader = CreateNifHeader(false);

        for (var i = 0; i < 50; i++)
        {
            var filePath = Path.Combine(_tempDir, $"xbox_{i:D3}.nif");
            await File.WriteAllBytesAsync(filePath, xbox360Header);
        }

        for (var i = 0; i < 50; i++)
        {
            var filePath = Path.Combine(_tempDir, $"pc_{i:D3}.nif");
            await File.WriteAllBytesAsync(filePath, pcHeader);
        }

        // Act
        var files = Directory.EnumerateFiles(_tempDir, "*.nif", SearchOption.AllDirectories).ToList();
        var results = new (string path, long size, string format)[files.Count];

        using var semaphore = new SemaphoreSlim(MaxConcurrentReads);
        var tasks = files.Select((path, i) => Task.Run(async () =>
        {
            await semaphore.WaitAsync();
            try
            {
                var (size, format) = await ReadNifHeaderAsync(path);
                results[i] = (path, size, format);
            }
            finally
            {
                semaphore.Release();
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(100, results.Length);
        Assert.Equal(50, results.Count(r => r.format == "Xbox 360 (BE)"));
        Assert.Equal(50, results.Count(r => r.format == "PC (LE)"));
    }

    [Fact]
    public void ParseNifHeader_Xbox360Format_ReturnsCorrect()
    {
        var header = CreateNifHeader(true);
        var format = DetermineNifFormat(header, header.Length);

        Assert.Equal("Xbox 360 (BE)", format);
    }

    [Fact]
    public void ParseNifHeader_PCFormat_ReturnsCorrect()
    {
        var header = CreateNifHeader(false);
        var format = DetermineNifFormat(header, header.Length);

        Assert.Equal("PC (LE)", format);
    }

    [Fact]
    public void ParseDdxHeader_3XDO_ReturnsCorrect()
    {
        var header = "3XDO"u8.ToArray();
        var format = DetermineDdxFormat(header, header.Length);

        Assert.Equal("3XDO", format);
    }

    [Fact]
    public void ParseDdxHeader_3XDR_ReturnsCorrect()
    {
        var header = "3XDR"u8.ToArray();
        var format = DetermineDdxFormat(header, header.Length);

        Assert.Equal("3XDR", format);
    }

    [Fact]
    public void ParseDdxHeader_Invalid_ReturnsCorrect()
    {
        var header = "XXXX"u8.ToArray();
        var format = DetermineDdxFormat(header, header.Length);

        Assert.Equal("Invalid", format);
    }

    #region Header Reading Helpers (same logic as UI code)

    private static async Task<(long fileSize, string format)> ReadNifHeaderAsync(string filePath)
    {
        var headerBytes = ArrayPool<byte>.Shared.Rent(50);
        try
        {
            var fileInfo = new FileInfo(filePath);
            var fileSize = fileInfo.Length;

            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytesRead = await fs.ReadAsync(headerBytes.AsMemory(0, 50));

            var format = DetermineNifFormat(headerBytes, bytesRead);
            return (fileSize, format);
        }
        catch
        {
            return (0, "Error");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBytes);
        }
    }

    private static async Task<(long fileSize, string format)> ReadDdxHeaderAsync(string filePath)
    {
        var headerBytes = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            var fileInfo = new FileInfo(filePath);
            var fileSize = fileInfo.Length;

            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytesRead = await fs.ReadAsync(headerBytes.AsMemory(0, 4));

            var format = DetermineDdxFormat(headerBytes, bytesRead);
            return (fileSize, format);
        }
        catch
        {
            return (0, "Error");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBytes);
        }
    }

    private static string DetermineNifFormat(byte[] headerBytes, int bytesRead)
    {
        if (bytesRead < 50) return "Invalid";

        var newlinePos = Array.IndexOf(headerBytes, (byte)0x0A, 0, Math.Min(50, bytesRead));
        if (newlinePos <= 0 || newlinePos + 5 >= bytesRead) return "Invalid";

        return headerBytes[newlinePos + 5] switch
        {
            0 => "Xbox 360 (BE)",
            1 => "PC (LE)",
            _ => "Unknown"
        };
    }

    private static string DetermineDdxFormat(byte[] headerBytes, int bytesRead)
    {
        if (bytesRead < 4) return "Invalid";

        if (headerBytes[0] == '3' && headerBytes[1] == 'X' && headerBytes[2] == 'D')
            return headerBytes[3] switch
            {
                (byte)'O' => "3XDO",
                (byte)'R' => "3XDR",
                _ => "Invalid"
            };

        return "Invalid";
    }

    private static byte[] CreateNifHeader(bool isXbox360)
    {
        // Create a minimal valid NIF header
        var versionString = "Gamebryo File Format, Version 20.2.0.7\n"u8.ToArray();
        var header = new byte[50];
        versionString.CopyTo(header, 0);

        // Byte at position after newline + 5 determines endianness
        var newlinePos = Array.IndexOf(header, (byte)0x0A);
        if (newlinePos > 0 && newlinePos + 5 < header.Length) header[newlinePos + 5] = (byte)(isXbox360 ? 0 : 1);

        return header;
    }

    #endregion
}
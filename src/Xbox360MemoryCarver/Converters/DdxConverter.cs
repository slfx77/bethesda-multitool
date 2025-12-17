namespace Xbox360MemoryCarver.Converters;

/// <summary>
/// High-level DDX to DDS converter for CLI integration.
/// Wraps DdxSubprocessConverter to provide a consistent API.
/// </summary>
public class DdxConverter
{
    private readonly DdxSubprocessConverter _subprocess;

    public DdxConverter(bool verbose = false, ConversionOptions? options = null)
    {
        _subprocess = new DdxSubprocessConverter(verbose);
    }

    /// <summary>
    /// Convert a DDX file to DDS format.
    /// </summary>
    public async Task<bool> ConvertFileAsync(string inputPath, string outputPath)
    {
        return await _subprocess.ConvertFileAsync(inputPath, outputPath);
    }

    /// <summary>
    /// Convert a DDX file to DDS format synchronously.
    /// </summary>
    public bool ConvertFile(string inputPath, string outputPath)
    {
        return _subprocess.ConvertFile(inputPath, outputPath);
    }

    /// <summary>
    /// Convert DDX data from memory to DDS format.
    /// </summary>
    public byte[]? ConvertFromMemory(byte[] ddxData)
    {
        return _subprocess.ConvertFromMemory(ddxData);
    }

    /// <summary>
    /// Convert DDX data from memory to DDS format asynchronously.
    /// </summary>
    public async Task<byte[]?> ConvertFromMemoryAsync(byte[] ddxData)
    {
        return await _subprocess.ConvertFromMemoryAsync(ddxData);
    }

    /// <summary>
    /// Check if data appears to be a valid DDX file.
    /// </summary>
    public static bool IsDdxFile(ReadOnlySpan<byte> data) => DdxSubprocessConverter.IsDdxFile(data);

    /// <summary>
    /// Check if DDX data uses the 3XDR format.
    /// </summary>
    public static bool Is3XdrFormat(ReadOnlySpan<byte> data) => DdxSubprocessConverter.Is3XdrFormat(data);

    /// <summary>
    /// Print conversion statistics.
    /// </summary>
    public void PrintStats()
    {
        Console.WriteLine($"DDX conversion: {_subprocess.Succeeded} succeeded, {_subprocess.Failed} failed, {_subprocess.Processed} total");
    }

    public int Processed => _subprocess.Processed;
    public int Succeeded => _subprocess.Succeeded;
    public int Failed => _subprocess.Failed;
}


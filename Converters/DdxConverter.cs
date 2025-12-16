namespace Xbox360MemoryCarver.Converters;

/// <summary>
/// High-level DDX to DDS converter for CLI integration.
/// Provides file and memory-based conversion with statistics tracking.
/// </summary>
public class DdxConverter
{
    private int _processed;
    private int _succeeded;
    private int _failed;
    private readonly bool _verbose;
    private readonly ConversionOptions _options;

    public DdxConverter(bool verbose = false, ConversionOptions? options = null)
    {
        _verbose = verbose;
        _options = options ?? new ConversionOptions { Verbose = verbose };
    }

    /// <summary>
    /// Convert a DDX file to DDS format.
    /// </summary>
    public async Task<bool> ConvertFileAsync(string inputPath, string outputPath)
    {
        _processed++;
        try
        {
            await Task.Run(() =>
            {
                var parser = new DdxParser(_verbose);
                parser.ConvertDdxToDds(inputPath, outputPath, _options);
            });
            _succeeded++;
            return true;
        }
        catch (Exception ex)
        {
            _failed++;
            if (_verbose)
                Console.WriteLine($"Conversion failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Convert a DDX file to DDS format synchronously.
    /// </summary>
    public bool ConvertFile(string inputPath, string outputPath)
    {
        _processed++;
        try
        {
            var parser = new DdxParser(_verbose);
            parser.ConvertDdxToDds(inputPath, outputPath, _options);
            _succeeded++;
            return true;
        }
        catch (Exception ex)
        {
            _failed++;
            if (_verbose)
                Console.WriteLine($"Conversion failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Convert DDX data from memory to DDS format.
    /// </summary>
    public byte[]? ConvertFromMemory(byte[] ddxData)
    {
        _processed++;
        try
        {
            var parser = new DdxParser(_verbose);
            var result = parser.ConvertDdxToDdsMemory(ddxData, _options);

            if (result != null)
                _succeeded++;
            else
                _failed++;

            return result;
        }
        catch (Exception ex)
        {
            _failed++;
            if (_verbose)
                Console.WriteLine($"Conversion failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Convert DDX data from memory to DDS format asynchronously.
    /// </summary>
    public async Task<byte[]?> ConvertFromMemoryAsync(byte[] ddxData)
    {
        return await Task.Run(() => ConvertFromMemory(ddxData));
    }

    /// <summary>
    /// Check if data appears to be a valid DDX file.
    /// </summary>
    public static bool IsDdxFile(ReadOnlySpan<byte> data) => DdxParser.IsDdxFile(data);

    /// <summary>
    /// Check if DDX data uses the unsupported 3XDR format.
    /// </summary>
    public static bool Is3XdrFormat(ReadOnlySpan<byte> data) => DdxParser.Is3XdrFormat(data);

    /// <summary>
    /// Print conversion statistics.
    /// </summary>
    public void PrintStats()
    {
        Console.WriteLine($"DDX conversion: {_succeeded} succeeded, {_failed} failed, {_processed} total");
    }

    /// <summary>Number of successful conversions.</summary>
    public int SuccessCount => _succeeded;

    /// <summary>Number of failed conversions.</summary>
    public int FailedCount => _failed;

    /// <summary>Total number of processed files.</summary>
    public int ProcessedCount => _processed;
}

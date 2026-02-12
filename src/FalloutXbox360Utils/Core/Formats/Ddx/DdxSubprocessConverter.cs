using DDXConv;

namespace FalloutXbox360Utils.Core.Formats.Ddx;

/// <summary>
///     Converts DDX files using DDXConv as a compiled-in library.
/// </summary>
public class DdxSubprocessConverter(bool verbose = false, bool saveAtlas = false)
{
    /// <summary>
    ///     Callback for batch conversion progress updates.
    /// </summary>
    /// <param name="inputPath">The input file path that was converted.</param>
    /// <param name="status">Status: OK, FAIL, or UNSUPPORTED.</param>
    /// <param name="error">Error message if conversion failed.</param>
    public delegate void BatchProgressCallback(string inputPath, string status, string? error);

    private readonly bool _saveAtlas = saveAtlas;
    private readonly bool _verbose = verbose;

    public int Processed { get; private set; }
    public int Succeeded { get; private set; }
    public int Failed { get; private set; }


    public bool ConvertFile(string inputPath, string outputPath)
    {
        Processed++;
        try
        {
            var parser = new DdxParser(_verbose);
            parser.ConvertDdxToDds(inputPath, outputPath, new ConversionOptions { SaveAtlas = _saveAtlas });
            Succeeded++;
            return true;
        }
        catch (Exception ex)
        {
            if (_verbose)
            {
                Console.WriteLine(
                    $"[DdxConverter] Exception converting {inputPath}: {ex.GetType().Name}: {ex.Message}");
            }

            Failed++;
            return false;
        }
    }

    public ConversionResult ConvertFromMemoryWithResult(byte[] ddxData)
    {
        Processed++;
        try
        {
            var parser = new MemoryTextureParser(_verbose);
            var ddxResult = parser.ConvertFromMemory(ddxData, _saveAtlas);

            if (!ddxResult.Success)
            {
                Failed++;
                return ConversionResult.Failure(ddxResult.Error ?? "DDXConv conversion failed");
            }

            Succeeded++;
            return new ConversionResult
            {
                Success = true,
                OutputData = ddxResult.DdsData,
                AtlasData = ddxResult.AtlasData,
                Notes = ddxResult.Notes
            };
        }
        catch (Exception ex)
        {
            Failed++;
            return ConversionResult.Failure($"Exception: {ex.Message}");
        }
    }

    /// <summary>
    ///     Converts all DDX files in the input directory to DDS using parallel in-process conversion.
    /// </summary>
    /// <param name="inputDir">Input directory containing DDX files.</param>
    /// <param name="outputDir">Output directory for DDS files.</param>
    /// <param name="progressCallback">Callback invoked for each file as it completes.</param>
    /// <param name="cancellationToken">Cancellation token to stop the conversion.</param>
    /// <param name="pcFriendly">
    ///     If true, enables PC-friendly normal map conversion. This post-processes normal maps (_n.dds)
    ///     to merge with specular maps (_s.dds), converting Xbox 360 2-channel BC5 normals to
    ///     3-channel DXT5 format with specular in alpha for PC compatibility.
    /// </param>
    /// <returns>Batch conversion result with statistics.</returns>
#pragma warning disable CA1068 // CancellationToken parameter ordering - reordering would break existing callers
    public Task<BatchConversionResult> ConvertBatchAsync(
        string inputDir,
        string outputDir,
        BatchProgressCallback? progressCallback = null,
        CancellationToken cancellationToken = default,
        bool pcFriendly = false)
#pragma warning restore CA1068
    {
        return Task.Run(() =>
        {
            var result = new BatchConversionResult();
            Directory.CreateDirectory(outputDir);

            var ddxFiles = Directory.GetFiles(inputDir, "*.ddx", SearchOption.AllDirectories);
            result.TotalFiles = ddxFiles.Length;

            var converted = 0;
            var failed = 0;
            var unsupported = 0;

            Parallel.ForEach(ddxFiles, new ParallelOptions { CancellationToken = cancellationToken }, ddxFile =>
            {
                var relativePath = Path.GetRelativePath(inputDir, ddxFile);
                var outputPath = Path.Combine(outputDir, Path.ChangeExtension(relativePath, ".dds"));
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

                try
                {
                    var parser = new DdxParser(_verbose);
                    parser.ConvertDdxToDds(ddxFile, outputPath, new ConversionOptions { SaveAtlas = _saveAtlas });
                    Interlocked.Increment(ref converted);
                    progressCallback?.Invoke(ddxFile, "OK", null);
                }
                catch (NotSupportedException)
                {
                    Interlocked.Increment(ref unsupported);
                    progressCallback?.Invoke(ddxFile, "UNSUPPORTED", null);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    progressCallback?.Invoke(ddxFile, "FAIL", ex.Message);
                }
            });

            result.Converted = converted;
            result.Failed = failed;
            result.Unsupported = unsupported;

            if (pcFriendly)
            {
                var normalMaps = Directory.GetFiles(outputDir, "*_n.dds", SearchOption.AllDirectories);
                Parallel.ForEach(normalMaps, normalMap =>
                {
                    var specMap = normalMap.Replace("_n.dds", "_s.dds", StringComparison.Ordinal);
                    try
                    {
                        DdsPostProcessor.MergeNormalSpecularMaps(normalMap, File.Exists(specMap) ? specMap : null);
                    }
                    catch (Exception ex)
                    {
                        if (_verbose)
                        {
                            Console.WriteLine($"[DdxConverter] PC-friendly merge failed for {normalMap}: {ex.Message}");
                        }
                    }
                });
            }

            return result;
        }, cancellationToken);
    }

    public Task<ConversionResult> ConvertFromMemoryWithResultAsync(byte[] ddxData)
    {
        return Task.Run(() => ConvertFromMemoryWithResult(ddxData));
    }
}

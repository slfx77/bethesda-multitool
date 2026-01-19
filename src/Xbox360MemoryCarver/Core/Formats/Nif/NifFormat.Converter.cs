using Xbox360MemoryCarver.Core.Converters;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     NIF format module - IFileConverter implementation for Xbox 360 to PC conversion.
/// </summary>
public sealed partial class NifFormat
{
    #region IFileConverter Implementation

    /// <inheritdoc />
    public string TargetExtension => ".nif";

    /// <inheritdoc />
    public string TargetFolder => "models_converted";

    /// <inheritdoc />
    public bool IsInitialized => true;

    /// <inheritdoc />
    public int ConvertedCount { get; private set; }

    /// <inheritdoc />
    public int FailedCount { get; private set; }

    /// <inheritdoc />
    public bool Initialize(bool verbose = false, Dictionary<string, object>? options = null)
    {
        // No external dependencies needed for NIF conversion
        return true;
    }

    /// <inheritdoc />
    public bool CanConvert(string signatureId, IReadOnlyDictionary<string, object>? metadata)
    {
        // Only convert big-endian NIF files
        if (metadata?.TryGetValue("bigEndian", out var beValue) == true && beValue is bool isBigEndian)
        {
            return isBigEndian;
        }

        return false;
    }

    /// <inheritdoc />
    public Task<ConversionResult> ConvertAsync(byte[] data, IReadOnlyDictionary<string, object>? metadata = null)
    {
        try
        {
            var verbose = metadata?.TryGetValue("verbose", out var v) == true && v is true;
            var converter = new NifConverter(verbose);
            var nifResult = converter.Convert(data);

            if (nifResult.Success)
            {
                ConvertedCount++;
                // NifConversionResult inherits from ConversionResult, so we can return it directly
                // Just add the success notes if not already set
                if (string.IsNullOrEmpty(nifResult.Notes))
                {
                    return Task.FromResult<ConversionResult>(new NifConversionResult
                    {
                        Success = true,
                        OutputData = nifResult.OutputData,
                        SourceInfo = nifResult.SourceInfo,
                        OutputInfo = nifResult.OutputInfo,
                        ErrorMessage = "Successfully converted Xbox 360 NIF to PC format with geometry unpacking."
                    });
                }

                return Task.FromResult<ConversionResult>(nifResult);
            }

            FailedCount++;
            return Task.FromResult<ConversionResult>(nifResult);
        }
        catch (Exception ex)
        {
            FailedCount++;
            return Task.FromResult<ConversionResult>(new NifConversionResult
            {
                Success = false,
                ErrorMessage = $"NIF conversion error: {ex.Message}"
            });
        }
    }

    #endregion
}

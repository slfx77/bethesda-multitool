namespace FalloutXbox360Utils.Core.Formats;

/// <summary>
///     Optional interface for formats that support conversion to another format.
/// </summary>
public interface IFileConverter
{
    /// <summary>
    ///     Target format after conversion (e.g., ".dds" for DDX).
    /// </summary>
    string TargetExtension { get; }

    /// <summary>
    ///     Target folder name for converted files (e.g., "textures" for DDX -> DDS).
    /// </summary>
    string TargetFolder { get; }

    /// <summary>
    ///     Whether the converter is ready to use.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    ///     Conversion statistics.
    /// </summary>
    int ConvertedCount { get; }

    int FailedCount { get; }

    /// <summary>
    ///     Check if this file can be converted based on signature ID and metadata.
    /// </summary>
    /// <param name="signatureId">The matched signature ID.</param>
    /// <param name="metadata">Metadata from parsing.</param>
    /// <returns>True if conversion should be attempted.</returns>
    bool CanConvert(string signatureId, IReadOnlyDictionary<string, object>? metadata);

    /// <summary>
    ///     Convert the file data to the target format.
    /// </summary>
    /// <param name="data">Original file data.</param>
    /// <param name="metadata">Metadata from parsing.</param>
    /// <returns>Conversion result with data and status.</returns>
    Task<ConversionResult> ConvertAsync(byte[] data, IReadOnlyDictionary<string, object>? metadata = null);

    /// <summary>
    ///     Initialize the converter (e.g., find external tools).
    ///     Called once during carver initialization.
    /// </summary>
    /// <param name="verbose">Enable verbose output.</param>
    /// <param name="options">Additional options.</param>
    /// <returns>True if converter is ready.</returns>
    bool Initialize(bool verbose = false, Dictionary<string, object>? options = null);
}

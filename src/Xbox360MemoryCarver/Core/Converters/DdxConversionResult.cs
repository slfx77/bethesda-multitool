namespace Xbox360MemoryCarver.Core.Converters;

// This file is deprecated. Use ConversionResult.cs instead.
// Keeping for backwards compatibility during transition.

/// <summary>
///     Result of a file format conversion operation.
///     DEPRECATED: Use ConversionResult instead.
/// </summary>
[Obsolete("Use ConversionResult instead")]
public class DdxConversionResult : ConversionResult
{
    /// <summary>
    ///     Backwards compatibility alias for OutputData.
    /// </summary>
    public byte[]? DdsData
    {
        get => OutputData;
        init => OutputData = value;
    }

    public new static DdxConversionResult Failure(string notes)
    {
        return new DdxConversionResult { Success = false, Notes = notes };
    }
}

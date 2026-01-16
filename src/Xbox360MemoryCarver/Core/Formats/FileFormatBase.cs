namespace Xbox360MemoryCarver.Core.Formats;

/// <summary>
///     Base class for file format implementations.
///     Provides common functionality and default implementations.
/// </summary>
public abstract class FileFormatBase : IFileFormat
{
    public abstract string FormatId { get; }
    public abstract string DisplayName { get; }
    public abstract string Extension { get; }
    public abstract FileCategory Category { get; }
    public abstract string OutputFolder { get; }
    public abstract int MinSize { get; }
    public abstract int MaxSize { get; }
    public virtual bool ShowInFilterUI => true;
    public virtual bool EnableSignatureScanning => true;
    public abstract IReadOnlyList<FormatSignature> Signatures { get; }

    public abstract ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0);

    /// <summary>
    ///     Default implementation returns the signature description.
    /// </summary>
    public virtual string GetDisplayDescription(string signatureId,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        var sig = Signatures.FirstOrDefault(s => s.Id.Equals(signatureId, StringComparison.OrdinalIgnoreCase));
        return sig?.Description ?? DisplayName;
    }
}

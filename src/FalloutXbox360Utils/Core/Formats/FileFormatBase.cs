namespace FalloutXbox360Utils.Core.Formats;

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

    /// <summary>
    ///     Default group label derived from DisplayName and Category.
    ///     Override in derived classes for custom labels.
    /// </summary>
    public virtual string GroupLabel => $"{DisplayName} {GetCategorySuffix(Category)}";

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

    /// <summary>
    ///     Gets the plural suffix for a file category.
    /// </summary>
    private static string GetCategorySuffix(FileCategory category)
    {
        return category switch
        {
            FileCategory.Texture => "Textures",
            FileCategory.Image => "Images",
            FileCategory.Audio => "Audio",
            FileCategory.Video => "Videos",
            FileCategory.Model => "Models",
            FileCategory.Module => "Modules",
            FileCategory.Script => "Scripts",
            FileCategory.Xbox => "Files",
            FileCategory.Header => "Headers",
            FileCategory.SaveGame => "Files",
            _ => "Files"
        };
    }
}

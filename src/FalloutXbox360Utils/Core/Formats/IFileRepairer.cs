namespace FalloutXbox360Utils.Core.Formats;

/// <summary>
///     Optional interface for formats that support repair/enhancement.
/// </summary>
public interface IFileRepairer
{
    /// <summary>
    ///     Determine if the file needs repair based on metadata.
    /// </summary>
    bool NeedsRepair(IReadOnlyDictionary<string, object>? metadata);

    /// <summary>
    ///     Repair or enhance the file.
    /// </summary>
    /// <param name="data">Original file data.</param>
    /// <param name="metadata">Metadata from parsing.</param>
    /// <returns>Repaired data.</returns>
    byte[] Repair(byte[] data, IReadOnlyDictionary<string, object>? metadata);
}

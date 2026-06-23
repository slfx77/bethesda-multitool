namespace BethesdaMultitool.Core.Formats.Esm.Export.Support;

/// <summary>
///     Metadata for a single memory dump in a cross-dump comparison.
/// </summary>
internal record DumpSnapshot(
    string FileName,
    DateTime FileDate,
    string ShortName,
    bool IsDmp,
    bool IsBase = false,
    string DateSource = "");

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Metadata for a single memory dump in a cross-dump comparison.
/// </summary>
internal record DumpSnapshot(string FileName, DateTime FileDate, string ShortName, bool IsDmp, bool IsBase = false);

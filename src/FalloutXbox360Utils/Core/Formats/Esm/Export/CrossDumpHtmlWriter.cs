namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Canonical HTML writer for cross-dump comparison pages.
///     Uses the structured JSON-backed renderer.
/// </summary>
internal static class CrossDumpHtmlWriter
{
    internal static Dictionary<string, string> GenerateAll(CrossDumpRecordIndex index)
    {
        return CrossDumpJsonHtmlWriter.GenerateAll(index);
    }
}

using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Strings;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Aggregated data sources for report generation. Used by both CLI and GUI
///     to ensure identical report output regardless of entry point.
/// </summary>
public record ReportDataSources(
    RecordCollection Records,
    Dictionary<uint, string>? FormIdMap = null,
    List<DetectedAssetString>? AssetStrings = null,
    List<RuntimeEditorIdEntry>? RuntimeEditorIds = null,
    StringPoolSummary? StringPool = null);

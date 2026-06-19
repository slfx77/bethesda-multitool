using BethesdaMultitool.Core.Strings;

namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>Bundles the string-pool summary and ownership analysis used to build runtime-string reports.</summary>
internal sealed record RuntimeStringReportData(
    StringPoolSummary StringPool,
    RuntimeStringOwnershipAnalysis OwnershipAnalysis);

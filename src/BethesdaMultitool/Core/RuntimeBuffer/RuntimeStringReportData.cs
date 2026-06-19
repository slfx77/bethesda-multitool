using BethesdaMultitool.Core.Strings;

namespace BethesdaMultitool.Core.RuntimeBuffer;

internal sealed record RuntimeStringReportData(
    StringPoolSummary StringPool,
    RuntimeStringOwnershipAnalysis OwnershipAnalysis);

using FalloutXbox360Utils.Core.Strings;

namespace FalloutXbox360Utils.Core.RuntimeBuffer;

internal sealed record RuntimeStringReportData(
    StringPoolSummary StringPool,
    RuntimeStringOwnershipAnalysis OwnershipAnalysis);

using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Validation;

/// <summary>
///     Validates that a freshly emitted plugin ESP can be round-tripped through
///     <see cref="EsmParser" /> with the expected structural shape.
/// </summary>
public static class PluginRoundTripValidator
{
    /// <summary>
    ///     Re-parse the emitted bytes and verify:
    ///       1. The file starts with TES4 (PC little-endian).
    ///       2. <see cref="EsmParser.EnumerateRecords" /> reads the file without throwing.
    ///       3. The expected total override count is present.
    /// </summary>
    /// <returns>A short human-readable validation report.</returns>
    public static string Validate(byte[] espBytes, int expectedOverrideCount)
    {
        var sb = new StringBuilder();

        // TES4 (PC) starts with the literal 4 bytes 'T','E','S','4'.
        if (espBytes.Length < 24 || espBytes[0] != 'T' || espBytes[1] != 'E' || espBytes[2] != 'S' ||
            espBytes[3] != '4')
        {
            return "FAIL: ESP does not begin with TES4 record signature.";
        }

        sb.AppendLine("Output begins with TES4 — OK.");

        List<ParsedMainRecord> records;
        try
        {
            records = EsmParser.EnumerateRecords(espBytes);
        }
        catch (Exception ex)
        {
            return $"FAIL: Re-parsing emitted ESP threw {ex.GetType().Name}: {ex.Message}";
        }

        sb.AppendLine($"Re-parser read {records.Count:N0} record(s) without errors.");

        var nonTes4 = records.Count(r => r.Header.Signature != "TES4");
        if (nonTes4 != expectedOverrideCount)
        {
            sb.AppendLine(
                $"WARN: Expected {expectedOverrideCount} override records, re-parser found {nonTes4}.");
        }
        else
        {
            sb.AppendLine($"Override record count matches expectation: {expectedOverrideCount}.");
        }

        return sb.ToString().TrimEnd();
    }
}

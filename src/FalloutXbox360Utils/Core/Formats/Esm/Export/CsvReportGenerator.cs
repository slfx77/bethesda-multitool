namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates CSV reports from reconstructed ESM records.
/// </summary>
public static partial class CsvReportGenerator
{
    private static string E(string? value)
    {
        return Fmt.CsvEscape(value);
    }

    private static string Endian(bool isBigEndian)
    {
        return Fmt.Endian(isBigEndian);
    }

    private static string FId(uint formId)
    {
        return Fmt.FId(formId);
    }

    private static string FIdN(uint? formId)
    {
        return Fmt.FIdN(formId);
    }

    private static string Resolve(uint formId, Dictionary<uint, string> lookup)
    {
        return Fmt.Resolve(formId, lookup);
    }
}

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Shared formatting utilities used by report generator and CSV writer classes.
/// </summary>
internal static class Fmt
{
    /// <summary>CSV-escapes a value (quotes if it contains commas, quotes, or newlines).</summary>
    public static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    /// <summary>Formats a FormID as "0x{id:X8}", or "" if zero.</summary>
    public static string FId(uint formId)
    {
        return formId != 0 ? $"0x{formId:X8}" : "";
    }

    /// <summary>Formats a nullable FormID as "0x{id:X8}", or "" if null/zero.</summary>
    public static string FIdN(uint? formId)
    {
        return formId.HasValue && formId.Value != 0 ? $"0x{formId.Value:X8}" : "";
    }

    /// <summary>Formats a FormID as "0x{id:X8}" (always, even if zero).</summary>
    public static string FIdAlways(uint formId)
    {
        return $"0x{formId:X8}";
    }

    /// <summary>Formats a FormID with resolved name: "Name (0x{id:X8})".</summary>
    public static string FIdWithName(uint formId, Dictionary<uint, string> lookup)
    {
        if (lookup.TryGetValue(formId, out var name))
        {
            return $"{name} ({FIdAlways(formId)})";
        }

        return FIdAlways(formId);
    }

    /// <summary>Resolves a FormID to its editor name, or "" if not found/zero.</summary>
    public static string Resolve(uint formId, Dictionary<uint, string> lookup)
    {
        return formId != 0 && lookup.TryGetValue(formId, out var name) ? name : "";
    }

    /// <summary>Returns "BE" or "LE".</summary>
    public static string Endian(bool isBigEndian)
    {
        return isBigEndian ? "BE" : "LE";
    }
}

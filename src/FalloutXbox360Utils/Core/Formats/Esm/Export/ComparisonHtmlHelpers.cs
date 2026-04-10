using System.IO.Compression;
using System.Text;
using System.Web;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Shared HTML utilities for comparison page generation:
///     compression, escaping, and page structure.
/// </summary>
internal static class ComparisonHtmlHelpers
{
    /// <summary>Compress a string with Deflate and return as base64 (for browser DecompressionStream).</summary>
    internal static string CompressToBase64(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>HTML-encode a string.</summary>
    internal static string Esc(string text)
    {
        return HttpUtility.HtmlEncode(text);
    }

    /// <summary>Emit the HTML document header with CSS.</summary>
    internal static void AppendHtmlHeader(StringBuilder sb, string title)
    {
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\">");
        sb.AppendLine($"  <title>{Esc(title)}</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine(ComparisonCssStyles.Styles);
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
    }

    /// <summary>Emit the HTML document footer.</summary>
    internal static void AppendHtmlFooter(StringBuilder sb)
    {
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
    }
}

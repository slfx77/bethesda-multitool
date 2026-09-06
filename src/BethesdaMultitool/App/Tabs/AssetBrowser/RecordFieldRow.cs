using System.Globalization;

namespace BethesdaMultitool;

/// <summary>One name/value line in the Data Explorer's field list.</summary>
/// <remarks>
///     A plain immutable row rather than a bound view-model: these are rebuilt wholesale when the
///     selection changes, so there is nothing to notify about.
/// </remarks>
public sealed record RecordFieldRow(string Name, string Value)
{
    /// <summary>
    ///     Formats a synthesized record field for display.
    ///     <para>
    ///         Classic record fields are <c>object?</c> because each game's synthesizer decides what
    ///         it can say about a record. Invariant culture is used deliberately: these are decoded
    ///         file values, not user-facing quantities, and a comma decimal separator would make a
    ///         reported coordinate hard to match against a hex dump.
    ///     </para>
    /// </summary>
    public static RecordFieldRow From(string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(name);

        var text = value switch
        {
            null => "(none)",
            string s => s.Length == 0 ? "(empty)" : s,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "(none)"
        };

        return new RecordFieldRow(name, text);
    }
}

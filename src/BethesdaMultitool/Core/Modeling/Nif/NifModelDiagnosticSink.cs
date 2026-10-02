using System.Globalization;
using Slfx77.Multitool.Core.Documents;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Collects a sub-reader's document diagnostics under a per-code bound, so a pathological file cannot push the
///     document past <see cref="ModelDocument.MaximumDiagnostics" />. The first <see cref="MaximumPerCode" /> messages of
///     each code are kept in order; any excess is counted and reported by one summary row per code.
/// </summary>
internal sealed class NifModelDiagnosticSink
{
    /// <summary>The most messages kept per diagnostic code.</summary>
    public const int MaximumPerCode = 64;

    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly List<SceneDiagnostic> _kept = [];

    /// <summary>The total messages reported, kept or not.</summary>
    public int Reported { get; private set; }

    /// <summary>Reports one message; it is kept when fewer than <see cref="MaximumPerCode" /> of its code were.</summary>
    public void Add(string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(message);
        Reported++;
        var count = _counts.GetValueOrDefault(code);
        _counts[code] = count + 1;
        if (count < MaximumPerCode)
        {
            _kept.Add(new SceneDiagnostic(code, DocumentText.Raw(Bound(message))));
        }
    }

    /// <summary>The kept messages in report order, then one summary per code that exceeded the bound.</summary>
    public IReadOnlyList<SceneDiagnostic> ToList()
    {
        var result = new List<SceneDiagnostic>(_kept);
        foreach (var (code, count) in _counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (count > MaximumPerCode)
            {
                result.Add(new SceneDiagnostic(code, DocumentText.Raw(string.Create(CultureInfo.InvariantCulture,
                    $"{count - MaximumPerCode} more '{code}' diagnostic(s) were not listed; see the native state."))));
            }
        }

        return result;
    }

    private static string Bound(string message)
    {
        return message.Length <= 4096 ? message : message[..4096] + " [truncated]";
    }
}

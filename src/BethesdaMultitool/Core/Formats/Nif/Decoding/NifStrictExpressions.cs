using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using BethesdaMultitool.Core.Formats.Nif.Conditions;
using BethesdaMultitool.Core.Formats.Nif.Conversion;

namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     Process-wide cache of strict compiles of nif.xml attribute expressions, plus a strict parser for the
///     <c>since</c> / <c>until</c> version bounds. nif.xml strings are constants, so each distinct text is compiled
///     once; a rejected text caches its reason.
/// </summary>
/// <remarks>
///     The version-bound parser is not <c>NifSchemaConverter.ParseVersionString</c>: that helper returns 0 for any
///     text with fewer than four dotted parts and for the <c>V20_5_0_0</c> spelling, so <c>since="3.1"</c> reads as
///     "since 0" and <c>since="V20_5_0_0"</c> would read as always present. At 20.2.0.7 neither mistake changes a
///     decision for the dotted forms in the embedded nif.xml, but a strict decoder must not depend on that.
/// </remarks>
internal static class NifStrictExpressions
{
    private static readonly ConcurrentDictionary<(string Expression, NifStrictExpressionKind Kind),
        (NifStrictExpression? Compiled, string? Error)> ExpressionCache = new();

    private static readonly ConcurrentDictionary<string, (Func<NifVersionContext, bool>? Evaluator, string? Error)>
        VersionConditionCache = new(StringComparer.Ordinal);

    /// <summary>Compiles (or fetches) a <c>cond</c> expression strictly.</summary>
    public static bool TryGetCondition(
        string expression,
        [NotNullWhen(true)] out NifStrictExpression? compiled,
        [NotNullWhen(false)] out string? error)
    {
        return TryGet(expression, NifStrictExpressionKind.Condition, out compiled, out error);
    }

    /// <summary>Compiles (or fetches) a <c>length</c>, <c>width</c> or <c>arg</c> expression strictly.</summary>
    public static bool TryGetValue(
        string expression,
        [NotNullWhen(true)] out NifStrictExpression? compiled,
        [NotNullWhen(false)] out string? error)
    {
        return TryGet(expression, NifStrictExpressionKind.Value, out compiled, out error);
    }

    /// <summary>Compiles (or fetches) a <c>vercond</c> expression strictly.</summary>
    public static bool TryGetVersionCondition(
        string expression,
        [NotNullWhen(true)] out Func<NifVersionContext, bool>? evaluator,
        [NotNullWhen(false)] out string? error)
    {
        var entry = VersionConditionCache.GetOrAdd(expression, CompileVersionCondition);
        if (entry.Evaluator is { } compiled)
        {
            evaluator = compiled;
            error = null;
            return true;
        }

        evaluator = null;
        error = entry.Error ?? $"'{expression}' was rejected.";
        return false;
    }

    /// <summary>
    ///     Parses a nif.xml version bound: dotted (<c>20.2.0.7</c>, <c>3.1</c>, <c>10.1.0.106</c>; missing parts are
    ///     zero) or a version id (<c>V20_2_0_7</c>, <c>V3_3_0_13</c>; any <c>__</c> user suffix is ignored). Every part
    ///     must be a byte. <c>3.03</c> is nif.xml's <c>V3_03</c> (nif.xml:342), listed between 3.0 and 3.1; a
    ///     part-wise reading would put it at 3.3.0.0, above 3.1, so it is read as 0x03000300, which keeps nif.xml's own
    ///     order. No decision at 20.x depends on it.
    /// </summary>
    public static bool TryParseVersion(string text, out uint version)
    {
        version = 0;
        var trimmed = text.Trim();
        if (string.Equals(trimmed, "3.03", StringComparison.Ordinal))
        {
            version = 0x03000300;
            return true;
        }

        string[] parts;
        if (trimmed.StartsWith('V'))
        {
            var body = trimmed[1..];
            var userSuffix = body.IndexOf("__", StringComparison.Ordinal);
            if (userSuffix >= 0)
            {
                body = body[..userSuffix];
            }

            parts = body.Split('_');
        }
        else
        {
            parts = trimmed.Split('.');
        }

        if (parts.Length is < 1 or > 4)
        {
            return false;
        }

        uint result = 0;
        for (var i = 0; i < 4; i++)
        {
            byte part = 0;
            if (i < parts.Length &&
                !byte.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out part))
            {
                return false;
            }

            result = (result << 8) | part;
        }

        version = result;
        return true;
    }

    private static bool TryGet(
        string expression,
        NifStrictExpressionKind kind,
        [NotNullWhen(true)] out NifStrictExpression? compiled,
        [NotNullWhen(false)] out string? error)
    {
        var entry = ExpressionCache.GetOrAdd((expression, kind), CompileExpression);
        if (entry.Compiled is { } result)
        {
            compiled = result;
            error = null;
            return true;
        }

        compiled = null;
        error = entry.Error ?? $"'{expression}' was rejected.";
        return false;
    }

    private static (NifStrictExpression? Compiled, string? Error) CompileExpression(
        (string Expression, NifStrictExpressionKind Kind) key)
    {
        if (NifConditionExpr.TryCompileStrict(key.Expression, key.Kind, out var compiled, out var error))
        {
            return (compiled, null);
        }

        return (null, error);
    }

    private static (Func<NifVersionContext, bool>? Evaluator, string? Error) CompileVersionCondition(string text)
    {
        if (NifVersionExpr.TryCompileStrict(text, out var evaluator, out var error))
        {
            return (evaluator, null);
        }

        return (null, error);
    }
}

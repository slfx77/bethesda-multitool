using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;

/// <summary>
///     Label-resolution, FormID parsing/formatting, and condition-function helpers shared by the script diagnostics
///     analyzer. Pure functions over the analyzer's <see cref="EsmScriptFormIdInfo" /> index.
/// </summary>
internal static class EsmScriptDiagnosticsResolvers
{
    public static string ResolveLabel(IReadOnlyDictionary<uint, EsmScriptFormIdInfo> index, uint formId)
    {
        if (formId == 0 || !index.TryGetValue(formId, out var info))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(info.EditorId))
        {
            return info.EditorId;
        }

        return !string.IsNullOrWhiteSpace(info.FullName) ? info.FullName : info.RecordType;
    }

    public static string ResolveLabelSuffix(IReadOnlyDictionary<uint, EsmScriptFormIdInfo> index, uint formId)
    {
        var label = ResolveLabel(index, formId);
        return string.IsNullOrWhiteSpace(label) ? string.Empty : $" ({label})";
    }

    public static bool LabelMatches(EsmScriptFormIdInfo? info, string target)
    {
        if (info is null)
        {
            return false;
        }

        if (ContainsIgnoreCase(info.EditorId, target) || ContainsIgnoreCase(info.FullName, target))
        {
            return true;
        }

        var normalizedTarget = NormalizeSearchText(target);
        return normalizedTarget.Length > 0 &&
               (NormalizeSearchText(info.EditorId).Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                NormalizeSearchText(info.FullName).Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase));
    }

    public static bool ContainsIgnoreCase(string? value, string target)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains(target, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        var index = 0;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[index++] = char.ToLowerInvariant(ch);
            }
        }

        return new string(buffer[..index]);
    }

    public static bool TryParseFormId(string value, out uint formId)
    {
        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out formId);
    }

    public static string FormatFormIds(IEnumerable<uint> formIds)
    {
        return string.Join(' ', formIds.Where(id => id != 0).Select(id => $"0x{id:X8}"));
    }

    public static List<uint> ParseFormIds(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var result = new List<uint>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? token[2..]
                : token;
            if (uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var formId))
            {
                result.Add(formId);
            }
        }

        return result;
    }

    public static string FormatRawSubrecordBytes(ParsedMainRecord record, string signature)
    {
        return string.Join(' ', record.Subrecords
            .Where(s => s.Signature == signature)
            .Select(s => Convert.ToHexString(s.Data)));
    }

    public static string ResolveConditionFunctionName(BethesdaGame game, ushort conditionFunctionIndex)
    {
        var function = ConditionFunctionTable.For(game).Get(conditionFunctionIndex);
        return function?.Name ?? $"Func0x{conditionFunctionIndex:X4}";
    }

    public static string ResolveParameterLabel(
        IReadOnlyDictionary<uint, EsmScriptFormIdInfo> index,
        BethesdaGame game,
        ushort functionIndex,
        int parameterIndex,
        uint rawValue,
        byte conditionType,
        uint? runOn,
        uint? parameter1Value = null)
    {
        var table = ConditionFunctionTable.For(game);
        if (table.Get(functionIndex) is null)
        {
            return string.Empty;
        }

        // Preserve the established FO3/FNV diagnostic presentation (notably ActorValue and Sex
        // names), but only after the game-keyed raw-index table has admitted the function.
        if (game is BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas)
        {
            var resolved = PerkConditionParameterResolver.ResolveParameter(
                functionIndex, parameterIndex, rawValue);
            if (!string.IsNullOrWhiteSpace(resolved.Display))
            {
                return resolved.Display;
            }

            return resolved.FormId.HasValue
                ? ResolveLabel(index, resolved.FormId.Value)
                : string.Empty;
        }

        if (!table.TryClassifyParam(
                functionIndex,
                parameterIndex,
                conditionType,
                runOn,
                parameter1Value,
                out var kind))
        {
            return string.Empty;
        }

        return kind == ConditionParamKind.FormId
            ? ResolveLabel(index, rawValue)
            : rawValue.ToString(CultureInfo.InvariantCulture);
    }

    public static bool IsFormIdConditionParameter(
        BethesdaGame game,
        ushort functionIndex,
        int parameterIndex,
        byte conditionType,
        uint? runOn,
        uint? parameter1Value = null)
    {
        var table = ConditionFunctionTable.For(game);
        if (table.Get(functionIndex) is null)
        {
            return false;
        }

        // Preserve the richer classic-Fallout parameter resolver after the game-keyed membership
        // gate; it agrees with the shared classifier that ActorValue is numeric and also supplies
        // legacy enum distinctions used by the diagnostics display path.
        if (game is BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas)
        {
            return PerkConditionParameterResolver.IsFormParameter(functionIndex, parameterIndex);
        }

        return table.TryClassifyParam(
                   functionIndex,
                   parameterIndex,
                   conditionType,
                   runOn,
                   parameter1Value,
                   out var kind)
               && kind == ConditionParamKind.FormId;
    }

    public static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value[..Math.Max(0, maxLength - 3)] + "...";
    }
}

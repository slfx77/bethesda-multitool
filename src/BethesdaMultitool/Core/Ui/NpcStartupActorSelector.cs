using System.Globalization;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;

namespace BethesdaMultitool.Core.Ui;

/// <summary>
///     Resolves the GUI's unattended <c>--actor</c> value without depending on WinUI. Selection is
///     deliberately fail-closed: a textual selector must be an exact, unique Editor ID/full-name
///     match, and duplicate FormIDs are treated as ambiguous rather than picking list order.
/// </summary>
internal static class NpcStartupActorSelector
{
    internal static NpcStartupActorSelection Resolve(
        IReadOnlyList<NpcListItem> actors,
        string? selector)
    {
        ArgumentNullException.ThrowIfNull(actors);

        var value = selector?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return NpcStartupActorSelection.Failed(
                NpcStartupActorSelectionStatus.Empty,
                "The actor selector is empty.");
        }

        var numeric = ParseFormIdCandidates(value);
        if (numeric.Status == FormIdParseStatus.Invalid)
        {
            return NpcStartupActorSelection.Failed(
                NpcStartupActorSelectionStatus.InvalidFormId,
                $"'{value}' is not a valid 32-bit FormID.");
        }

        if (numeric.Status == FormIdParseStatus.Parsed)
        {
            var candidates = numeric.Candidates!;
            var matches = actors
                .Where(actor => candidates.Contains(actor.FormId))
                .ToArray();
            return ResolveUnique(
                matches,
                matches.Length == 0
                    ? $"No actor has the requested FormID '{value}'."
                    : $"FormID selector '{value}' matched {matches.Length} actor rows.");
        }

        var nameMatches = actors
            .Where(actor =>
                string.Equals(actor.EditorId, value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(actor.FullName, value, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return ResolveUnique(
            nameMatches,
            nameMatches.Length == 0
                ? $"No actor has the exact Editor ID or full name '{value}'."
                : $"Actor name selector '{value}' matched {nameMatches.Length} actor rows.");
    }

    private static NpcStartupActorSelection ResolveUnique(
        NpcListItem[] matches,
        string failureDiagnostic)
    {
        return matches.Length switch
        {
            1 => new NpcStartupActorSelection(
                NpcStartupActorSelectionStatus.Resolved,
                matches[0],
                1,
                $"Resolved 0x{matches[0].FormId:X8}."),
            0 => NpcStartupActorSelection.Failed(
                NpcStartupActorSelectionStatus.NotFound,
                failureDiagnostic),
            _ => new NpcStartupActorSelection(
                NpcStartupActorSelectionStatus.Ambiguous,
                null,
                matches.Length,
                failureDiagnostic)
        };
    }

    private static FormIdParseResult ParseFormIdCandidates(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(
                value.AsSpan(2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var prefixedHex)
                ? FormIdParseResult.Parsed(prefixedHex)
                : FormIdParseResult.Invalid;
        }

        if (value.All(char.IsAsciiDigit))
        {
            if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var decimalValue))
            {
                return FormIdParseResult.Invalid;
            }

            // Bethesda tools conventionally display zero-padded, eight-digit hexadecimal FormIDs.
            // Accept that copied form even without 0x, while retaining decimal as a candidate. If
            // both interpretations exist in the actor list, ResolveUnique fails closed as ambiguous.
            if (value.Length == 8 &&
                uint.TryParse(
                    value,
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out var paddedHex) &&
                paddedHex != decimalValue)
            {
                return FormIdParseResult.Parsed(decimalValue, paddedHex);
            }

            return FormIdParseResult.Parsed(decimalValue);
        }

        if (value.Length <= 8 && value.All(Uri.IsHexDigit))
        {
            return uint.TryParse(
                value,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var bareHex)
                ? FormIdParseResult.Parsed(bareHex)
                : FormIdParseResult.Invalid;
        }

        return FormIdParseResult.NotNumeric;
    }

    private enum FormIdParseStatus
    {
        NotNumeric,
        Parsed,
        Invalid
    }

    private readonly record struct FormIdParseResult(
        FormIdParseStatus Status,
        IReadOnlySet<uint>? Candidates)
    {
        internal static FormIdParseResult NotNumeric { get; } = new(FormIdParseStatus.NotNumeric, null);

        internal static FormIdParseResult Invalid { get; } = new(FormIdParseStatus.Invalid, null);

        internal static FormIdParseResult Parsed(params uint[] candidates)
        {
            return new FormIdParseResult(FormIdParseStatus.Parsed, candidates.ToHashSet());
        }
    }
}

internal enum NpcStartupActorSelectionStatus
{
    Resolved,
    Empty,
    InvalidFormId,
    NotFound,
    Ambiguous
}

internal sealed record NpcStartupActorSelection(
    NpcStartupActorSelectionStatus Status,
    NpcListItem? Actor,
    int MatchCount,
    string Diagnostic)
{
    internal bool IsResolved => Status == NpcStartupActorSelectionStatus.Resolved && Actor is not null;

    internal static NpcStartupActorSelection Failed(
        NpcStartupActorSelectionStatus status,
        string diagnostic)
    {
        return new NpcStartupActorSelection(status, null, 0, diagnostic);
    }
}

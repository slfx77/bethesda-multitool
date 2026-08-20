using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Planner;

/// <summary>
///     Lexical declaration kind carried by SCTX. SLSD stores only integer versus
///     float-or-reference, so SCTX is required to distinguish exact keywords.
/// </summary>
internal enum ScriptVariableDeclarationKind
{
    /// <summary>
    ///     An integer SLSD whose exact source keyword was not captured. It is never eligible
    ///     for retail-local identity matching, but a fresh append-only local may declare the
    ///     converter-owned slot as canonical <c>int</c> in the target script's own SCTX.
    /// </summary>
    Integer,

    /// <summary>
    ///     A type-zero SLSD whose exact float-versus-reference keyword was not captured.
    ///     Like <see cref="Integer" />, it is storage evidence for a fresh local only; the
    ///     converter-owned slot uses canonical <c>float</c> in emitted SCTX.
    /// </summary>
    FloatOrReference,
    Short,
    Long,
    Int,
    Float,
    Reference
}

/// <summary>
///     Conservative SCTX declaration reader. It recognizes only a single exact
///     declaration for the requested identifier anywhere in the same captured SCTX;
///     Bethesda scripts can declare block-local refs after Begin. It does not infer
///     identity from related names, usage, or source from another dump.
/// </summary>
internal static class ScriptVariableDeclarationParser
{
    internal static bool TryGetKind(
        string? sourceText,
        string variableName,
        out ScriptVariableDeclarationKind kind)
    {
        kind = default;
        if (string.IsNullOrEmpty(sourceText) || string.IsNullOrWhiteSpace(variableName))
        {
            return false;
        }

        ScriptVariableDeclarationKind? match = null;
        foreach (var rawLine in sourceText.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace('\r', '\n')
                     .Split('\n'))
        {
            var commentIndex = rawLine.IndexOf(';');
            var line = (commentIndex >= 0 ? rawLine[..commentIndex] : rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length != 2
                || !string.Equals(tokens[1], variableName, StringComparison.OrdinalIgnoreCase)
                || !TryParseKeyword(tokens[0], out var candidate))
            {
                continue;
            }

            // Duplicate declarations are malformed/ambiguous even when their keywords
            // agree. Fail closed instead of selecting one by order.
            if (match.HasValue)
            {
                return false;
            }

            match = candidate;
        }

        if (!match.HasValue)
        {
            return false;
        }

        kind = match.Value;
        return true;
    }

    private static bool TryParseKeyword(
        string keyword,
        out ScriptVariableDeclarationKind kind)
    {
        if (keyword.Equals("short", StringComparison.OrdinalIgnoreCase))
        {
            kind = ScriptVariableDeclarationKind.Short;
            return true;
        }

        if (keyword.Equals("long", StringComparison.OrdinalIgnoreCase))
        {
            kind = ScriptVariableDeclarationKind.Long;
            return true;
        }

        if (keyword.Equals("int", StringComparison.OrdinalIgnoreCase))
        {
            kind = ScriptVariableDeclarationKind.Int;
            return true;
        }

        if (keyword.Equals("float", StringComparison.OrdinalIgnoreCase))
        {
            kind = ScriptVariableDeclarationKind.Float;
            return true;
        }

        if (keyword.Equals("ref", StringComparison.OrdinalIgnoreCase))
        {
            kind = ScriptVariableDeclarationKind.Reference;
            return true;
        }

        kind = default;
        return false;
    }
}

/// <summary>
///     Shared exact-identity rules for recovered script locals. Integer SLSD storage is not
///     a lexical declaration: only a concrete keyword parsed from this script's SCTX can
///     participate in reuse or duplicate-candidate agreement. Fresh locals are distinct
///     converter-owned identities and receive canonical storage-compatible declarations.
/// </summary>
internal static class ScriptVariableDeclarationIdentity
{
    internal static bool IsConcrete(ScriptVariableDeclarationKind kind)
    {
        return kind is ScriptVariableDeclarationKind.Short
            or ScriptVariableDeclarationKind.Long
            or ScriptVariableDeclarationKind.Int
            or ScriptVariableDeclarationKind.Float
            or ScriptVariableDeclarationKind.Reference;
    }

    internal static bool KindsMatchExact(
        ScriptVariableDeclarationKind left,
        ScriptVariableDeclarationKind right)
    {
        return left == right && IsConcrete(left);
    }

    internal static ScriptVariableDeclarationKind FromStorage(ScriptVariableInfo variable)
    {
        return variable.Type switch
        {
            1 => ScriptVariableDeclarationKind.Integer,
            0 => ScriptVariableDeclarationKind.FloatOrReference,
            _ => throw new InvalidDataException(
                $"Script local '{variable.Name ?? "<unnamed>"}' has unsupported SLSD type "
                + $"{variable.Type}.")
        };
    }

    /// <summary>
    ///     Chooses the declaration written for a newly allocated local. This does not turn
    ///     storage evidence into an identity match: it only gives the converter-owned slot
    ///     a complete, deterministic SCTX declaration compatible with its serialized SLSD.
    /// </summary>
    internal static ScriptVariableDeclarationKind ForFreshLocal(
        ScriptVariableDeclarationKind sourceKind)
    {
        return sourceKind switch
        {
            ScriptVariableDeclarationKind.Integer => ScriptVariableDeclarationKind.Int,
            ScriptVariableDeclarationKind.FloatOrReference => ScriptVariableDeclarationKind.Float,
            _ when IsConcrete(sourceKind) => sourceKind,
            _ => throw new ArgumentOutOfRangeException(nameof(sourceKind), sourceKind, null)
        };
    }

    internal static bool IsStorageCompatible(
        ScriptVariableDeclarationKind kind,
        byte serializedType)
    {
        return kind switch
        {
            ScriptVariableDeclarationKind.Integer
                or ScriptVariableDeclarationKind.Short
                or ScriptVariableDeclarationKind.Long
                or ScriptVariableDeclarationKind.Int => serializedType == 1,
            ScriptVariableDeclarationKind.FloatOrReference
                or ScriptVariableDeclarationKind.Float
                or ScriptVariableDeclarationKind.Reference => serializedType == 0,
            _ => false
        };
    }

    /// <summary>
    ///     Compares the complete local table relevant to numeric-variable identity. Duplicate
    ///     raw SCPT candidates may differ in unrelated source statements, but every positional
    ///     SLSD/SCVR identity and every available SCTX declaration kind must agree. A concrete
    ///     declaration on only one side is a conflict; two unavailable declarations agree only
    ///     as "unknown" and will still be rejected later if recovery needs that variable.
    /// </summary>
    internal static bool TablesEquivalent(
        IReadOnlyList<ScriptVariableInfo> left,
        string? leftSourceText,
        IReadOnlyList<ScriptVariableInfo> right,
        string? rightSourceText)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var leftVariable = left[index];
            var rightVariable = right[index];
            if (leftVariable.Index != rightVariable.Index
                || leftVariable.Type != rightVariable.Type
                || !string.Equals(
                    leftVariable.Name,
                    rightVariable.Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(leftVariable.Name))
            {
                continue;
            }

            var leftHasKind = ScriptVariableDeclarationParser.TryGetKind(
                leftSourceText,
                leftVariable.Name,
                out var leftKind);
            var rightHasKind = ScriptVariableDeclarationParser.TryGetKind(
                rightSourceText,
                rightVariable.Name!,
                out var rightKind);
            if (leftHasKind != rightHasKind
                || (leftHasKind && leftKind != rightKind))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Returns SCTX only when standalone parsing accepted a complete compiled bundle.
    ///     When both retained and raw-runtime forms are present, their source and complete
    ///     local identity must also agree. Rejected and source-only text remains available
    ///     for diagnostics/recovery but is never declaration evidence for another record.
    /// </summary>
    internal static bool TryGetAcceptedDeclarationSourceText(
        ScriptRecord? acceptedStandaloneScript,
        RuntimeScriptData? runtimeScript,
        out string? sourceText)
    {
        sourceText = null;

        if (runtimeScript is null)
        {
            if (!IsAcceptedCompiledDeclarationSource(acceptedStandaloneScript))
            {
                return false;
            }

            sourceText = acceptedStandaloneScript!.SourceText;
            return true;
        }

        if (runtimeScript.SourceTextCorrespondenceStatus != ScriptSourceCorrespondenceStatus.Accepted
            || string.IsNullOrEmpty(runtimeScript.SourceText))
        {
            return false;
        }

        if (acceptedStandaloneScript is not null
            && (!IsAcceptedCompiledDeclarationSource(acceptedStandaloneScript)
                || !string.Equals(
                    acceptedStandaloneScript.SourceText,
                    runtimeScript.SourceText,
                    StringComparison.Ordinal)
                || !TablesEquivalent(
                    acceptedStandaloneScript.Variables,
                    acceptedStandaloneScript.SourceText,
                    runtimeScript.Variables,
                    runtimeScript.SourceText)))
        {
            return false;
        }

        sourceText = runtimeScript.SourceText;
        return true;
    }

    private static bool IsAcceptedCompiledDeclarationSource(ScriptRecord? script)
    {
        return script is
               {
                   SourceTextCorrespondenceStatus: ScriptSourceCorrespondenceStatus.Accepted,
                   IsIncompleteExecutableBundle: false,
                   CompiledData: { Length: > 0 }
               }
               && !string.IsNullOrEmpty(script.SourceText)
               && script.SourceTextOrigin != ScriptSourceTextOrigin.None;
    }
}

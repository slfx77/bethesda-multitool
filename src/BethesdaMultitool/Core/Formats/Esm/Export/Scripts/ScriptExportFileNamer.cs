using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Scripts;

/// <summary>
///     File names for exported scripts: the extension rule, the per-game default extension, and a
///     deterministic, platform-independent stem for every script.
///     <para>
///         A stem is the script's EditorID, or <c>0x{FormID:X8}</c> when it has none. The same rule runs
///         on every OS: the characters Windows forbids (<c>&lt;&gt;:"/\|?*</c>), control characters,
///         lone surrogates and <c>.</c> become <c>_</c>. Replacing the dot means no stem can end in
///         <see cref="DecompiledInfix" />, so an authored file and a decompiled companion can never share
///         a name. Trailing spaces are trimmed, Windows reserved device names (CON, PRN, AUX, NUL,
///         COM0-9, LPT0-9, any case) get a <c>_</c> prefix, and a stem is at most
///         <see cref="MaxStemLength" /> characters.
///     </para>
///     <para>
///         Stems are compared case-insensitively, because the default file systems on Windows and macOS
///         are case-insensitive. When two or more scripts share a stem, EVERY member of that group gets
///         <c>_0x{FormID:X8}</c>, so a name never depends on the order the scripts arrive in. Members that
///         also share the FormID (a memory dump can hold the same record twice) are ordered by record
///         offset and numbered <c>_1</c>, <c>_2</c>, and so on. Every change to a name is recorded as an
///         adjustment so the manifest can say why a file is not named after its EditorID.
///     </para>
/// </summary>
internal static class ScriptExportFileNamer
{
    /// <summary>The longest stem written, including any collision suffix.</summary>
    internal const int MaxStemLength = 120;

    /// <summary>Inserted between the stem and the extension of a decompiled companion file.</summary>
    internal const string DecompiledInfix = ".decompiled";

    /// <summary>Adjustment: the script has no EditorID, so the stem is its FormID.</summary>
    internal const string MissingEditorIdAdjustment = "missing-editor-id";

    /// <summary>Adjustment: characters a file name cannot hold were replaced, or trailing spaces trimmed.</summary>
    internal const string SanitizedAdjustment = "sanitized";

    /// <summary>Adjustment: the stem is a Windows reserved device name and got a <c>_</c> prefix.</summary>
    internal const string ReservedNameAdjustment = "reserved-device-name";

    /// <summary>Adjustment: the stem was cut to <see cref="MaxStemLength" /> characters.</summary>
    internal const string TruncatedAdjustment = "truncated";

    /// <summary>Adjustment: another script's stem matched case-insensitively, so a suffix was added.</summary>
    internal const string CollisionAdjustment = "collision";

    private const int MaxExtensionLength = 16;
    private const int MaxResidualCollisionPasses = 32;

    /// <summary>
    ///     Normalizes a user-supplied extension: <c>gek</c> and <c>.gek</c> both give <c>.gek</c>. The part
    ///     after the one leading dot must be 1 to 16 ASCII letters, digits, <c>_</c> or <c>-</c>, so an
    ///     extension can never carry a path separator, a second dot, whitespace or a character Windows
    ///     forbids. Case is kept as given.
    /// </summary>
    /// <param name="extension">The extension, with or without its leading dot.</param>
    /// <returns>The extension with exactly one leading dot.</returns>
    /// <exception cref="ArgumentException">The extension is empty or holds a character outside that set.</exception>
    internal static string NormalizeExtension(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        var body = extension.StartsWith('.') ? extension[1..] : extension;
        if (body.Length == 0 || body.Length > MaxExtensionLength)
        {
            throw new ArgumentException(
                $"A script file extension must have 1 to {MaxExtensionLength} characters after its dot: '{extension}'.",
                nameof(extension));
        }

        foreach (var c in body)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')
            {
                throw new ArgumentException(
                    "A script file extension may hold only ASCII letters, digits, '_' and '-' after its one " +
                    $"leading dot: '{extension}'.",
                    nameof(extension));
            }
        }

        return "." + body;
    }

    /// <summary>
    ///     The default extension for a game's individual script files: <c>.gek</c> for Fallout: New Vegas and
    ///     Fallout 3, which share the GECK script language (the community convention and the VS Code grammar
    ///     key on it), and <c>.txt</c> for every other game.
    /// </summary>
    internal static string DefaultExtension(BethesdaGame game)
    {
        return game is BethesdaGame.FalloutNewVegas or BethesdaGame.Fallout3 ? ".gek" : ".txt";
    }

    /// <summary>
    ///     Plans one unique stem per script (see the class remarks). The result is aligned with
    ///     <paramref name="scripts" /> by index, and the stem each script receives does not depend on the
    ///     order of the list.
    /// </summary>
    internal static IReadOnlyList<ScriptExportName> PlanStems(IReadOnlyList<ScriptRecord> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);

        var count = scripts.Count;
        var baseStems = new string[count];
        var adjustments = new List<string>[count];
        for (var i = 0; i < count; i++)
        {
            adjustments[i] = [];
            baseStems[i] = BuildBaseStem(scripts[i], adjustments[i]);
        }

        var stems = (string[])baseStems.Clone();
        var indices = Enumerable.Range(0, count).ToArray();

        foreach (var group in indices.GroupBy(i => baseStems[i], StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToList();
            if (members.Count < 2)
            {
                continue;
            }

            foreach (var sameFormId in members.GroupBy(i => scripts[i].FormId))
            {
                var suffix = string.Create(CultureInfo.InvariantCulture, $"_0x{sameFormId.Key:X8}");
                var ordered = sameFormId
                    .OrderBy(i => scripts[i].Offset)
                    .ThenBy(i => scripts[i].EditorId, StringComparer.Ordinal)
                    .ThenBy(i => i)
                    .ToList();
                for (var n = 0; n < ordered.Count; n++)
                {
                    var index = ordered[n];
                    var memberSuffix = ordered.Count == 1
                        ? suffix
                        : string.Create(CultureInfo.InvariantCulture, $"{suffix}_{n + 1}");
                    stems[index] = WithSuffix(baseStems[index], memberSuffix, adjustments[index]);
                    AddOnce(adjustments[index], CollisionAdjustment);
                }
            }
        }

        ResolveResidualCollisions(scripts, baseStems, stems, adjustments);

        var result = new ScriptExportName[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = new ScriptExportName(stems[i], adjustments[i]);
        }

        return result;
    }

    /// <summary>
    ///     A FormID suffix can meet a stem that already carried one (an EditorID such as
    ///     <c>Foo_0x00000010</c> beside two scripts named <c>Foo</c>). Such pathological groups are resolved
    ///     the same way — every member numbered — ordered by content, never by input position.
    /// </summary>
    private static void ResolveResidualCollisions(
        IReadOnlyList<ScriptRecord> scripts,
        string[] baseStems,
        string[] stems,
        List<string>[] adjustments)
    {
        for (var pass = 0; pass < MaxResidualCollisionPasses; pass++)
        {
            var groups = Enumerable.Range(0, stems.Length)
                .GroupBy(i => stems[i], StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Skip(1).Any())
                .ToList();
            if (groups.Count == 0)
            {
                return;
            }

            foreach (var group in groups)
            {
                var ordered = group
                    .OrderBy(i => baseStems[i], StringComparer.Ordinal)
                    .ThenBy(i => scripts[i].FormId)
                    .ThenBy(i => scripts[i].Offset)
                    .ThenBy(i => scripts[i].EditorId, StringComparer.Ordinal)
                    .ThenBy(i => i)
                    .ToList();
                for (var n = 0; n < ordered.Count; n++)
                {
                    var index = ordered[n];
                    stems[index] = WithSuffix(
                        stems[index],
                        string.Create(CultureInfo.InvariantCulture, $"_{n + 1}"),
                        adjustments[index]);
                    AddOnce(adjustments[index], CollisionAdjustment);
                }
            }
        }

        throw new InvalidOperationException(
            "Could not plan unique script file names after " +
            $"{MaxResidualCollisionPasses} collision passes.");
    }

    private static string BuildBaseStem(ScriptRecord script, List<string> adjustments)
    {
        string raw;
        if (string.IsNullOrWhiteSpace(script.EditorId))
        {
            raw = FormIdStem(script.FormId);
            adjustments.Add(MissingEditorIdAdjustment);
        }
        else
        {
            raw = script.EditorId;
        }

        var stem = Sanitize(raw);
        if (stem.Length == 0)
        {
            stem = FormIdStem(script.FormId);
            AddOnce(adjustments, MissingEditorIdAdjustment);
        }

        if (!string.Equals(stem, raw, StringComparison.Ordinal))
        {
            AddOnce(adjustments, SanitizedAdjustment);
        }

        if (stem.Length > MaxStemLength)
        {
            stem = stem[..MaxStemLength].TrimEnd(' ');
            AddOnce(adjustments, TruncatedAdjustment);
        }

        if (IsReservedDeviceName(stem))
        {
            stem = "_" + stem;
            adjustments.Add(ReservedNameAdjustment);
        }

        return stem;
    }

    private static string FormIdStem(uint formId)
    {
        return string.Create(CultureInfo.InvariantCulture, $"0x{formId:X8}");
    }

    private static string Sanitize(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            builder.Append(IsForbidden(c) ? '_' : c);
        }

        return builder.ToString().TrimEnd(' ');
    }

    private static bool IsForbidden(char c)
    {
        return c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' or '.'
               || char.IsControl(c)
               || char.IsSurrogate(c);
    }

    private static bool IsReservedDeviceName(string stem)
    {
        if (stem.Length == 3)
        {
            return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                   || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                   || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                   || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase);
        }

        return stem.Length == 4
               && char.IsAsciiDigit(stem[3])
               && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                   || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Appends <paramref name="suffix" />, cutting the stem first so the result fits the cap.</summary>
    private static string WithSuffix(string stem, string suffix, List<string> adjustments)
    {
        var room = MaxStemLength - suffix.Length;
        if (stem.Length > room)
        {
            stem = stem[..Math.Max(room, 0)].TrimEnd(' ');
            AddOnce(adjustments, TruncatedAdjustment);
        }

        return stem + suffix;
    }

    private static void AddOnce(List<string> adjustments, string adjustment)
    {
        if (!adjustments.Contains(adjustment))
        {
            adjustments.Add(adjustment);
        }
    }
}

/// <summary>A planned file stem and the adjustments that made it differ from the EditorID.</summary>
/// <param name="Stem">The file name without extension; unique case-insensitively within one export.</param>
/// <param name="Adjustments">
///     Why the stem differs from the EditorID: <c>missing-editor-id</c>, <c>sanitized</c>,
///     <c>reserved-device-name</c>, <c>truncated</c>, <c>collision</c>. Empty when it is the EditorID verbatim.
/// </param>
internal sealed record ScriptExportName(string Stem, IReadOnlyList<string> Adjustments);

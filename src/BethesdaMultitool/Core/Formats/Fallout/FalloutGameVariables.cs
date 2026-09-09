using System.Globalization;
using System.Text.RegularExpressions;

namespace BethesdaMultitool.Core.Formats.Fallout;

/// <summary>One declared global variable.</summary>
/// <param name="Index">Declaration order, which is the id scripts address it by.</param>
/// <param name="Name">The variable's name.</param>
/// <param name="Value">Its initial value.</param>
/// <param name="Comment">Trailing prose comment, or empty.</param>
internal readonly record struct FalloutGameVariable(int Index, string Name, int Value, string Comment);

/// <summary>The scope a <c>.GAM</c> file declares.</summary>
internal enum FalloutVariableScope
{
    /// <summary>Unknown or no section header.</summary>
    Unspecified,

    /// <summary>Campaign-wide globals (<c>GAME_GLOBAL_VARS</c>) — one file, <c>VAULT13.GAM</c>.</summary>
    Game,

    /// <summary>Per-map variables (<c>MAP_GLOBAL_VARS</c>).</summary>
    Map
}

/// <summary>
///     Fallout's <c>.GAM</c> global-variable declarations. Original RE 2026-09-06.
///     <para>
///         ⚑ Unlike every other Fallout container here, a <c>.GAM</c> is PLAIN TEXT: a section
///         header (<c>GAME_GLOBAL_VARS:</c> or <c>MAP_GLOBAL_VARS:</c>), then lines of
///         <c>NAME := VALUE;</c> with <c>//</c> comments. Scripts address a variable by its
///         DECLARATION ORDER, and the authors wrote that index into the trailing comment as
///         <c>// (n)</c>.
///     </para>
///     <para>
///         ⚑
///         <b>
///             That index is the oracle, and it is exact: 377 of 377 commented declarations across
///             the 36 shipped files have <c>(n)</c> equal to their position.
///         </b>
///         A parse that drops or
///         invents a declaration shows up immediately as an off-by-one from that point on, which is
///         precisely how the trap below was found.
///     </para>
///     <para>
///         ⚠⚠ <b>FIVE retail declarations have NO TERMINATING SEMICOLON</b> — <c>BAD_MONSTER</c> and
///         <c>MARK_SHADY_1</c> in <c>VAULT13.GAM</c>, <c>SIGNAL_NEW_STUDENT</c> in both
///         <c>BROHD12.GAM</c> and <c>BROHD34.GAM</c>, and <c>BETH_TALKED_ABOUT_BROTHER</c> in
///         <c>HUBDWNTN.GAM</c>. Requiring the semicolon silently DROPS them and shifts every later
///         index by one — 46 wrong indices in VAULT13 alone. The terminator is therefore optional
///         here, deliberately.
///     </para>
/// </summary>
internal sealed partial class FalloutGameVariables
{
    private FalloutGameVariables(string name, FalloutVariableScope scope, IReadOnlyList<FalloutGameVariable> variables)
    {
        Name = name;
        Scope = scope;
        Variables = variables;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>Whether the file declares campaign globals or per-map variables.</summary>
    public FalloutVariableScope Scope { get; }

    /// <summary>The declarations, in file order. The index IS the script-visible id.</summary>
    public IReadOnlyList<FalloutGameVariable> Variables { get; }

    /// <summary>
    ///     ⚠ The semicolon is OPTIONAL — five retail declarations omit it, and requiring it drops
    ///     them and shifts every subsequent index.
    /// </summary>
    [GeneratedRegex(@"^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*:=\s*(?<value>-?\d+)\s*;?\s*$")]
    private static partial Regex DeclarationPattern();

    [GeneratedRegex(@"^\s*(?<scope>GAME_GLOBAL_VARS|MAP_GLOBAL_VARS)\s*:")]
    private static partial Regex ScopePattern();

    /// <summary>Content probe: a section header, which every shipped file carries.</summary>
    public static bool IsGameVariableFile(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (var line in EnumerateLines(text))
        {
            if (ScopePattern().IsMatch(line))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses the declarations. Never throws — a file with none yields an empty list.</summary>
    public static FalloutGameVariables Parse(string text, string name)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(name);

        var scope = FalloutVariableScope.Unspecified;
        var variables = new List<FalloutGameVariable>();

        foreach (var line in EnumerateLines(text))
        {
            var header = ScopePattern().Match(line);
            if (header.Success)
            {
                // ⚠ VAULT13.GAM carries a COMMENTED-OUT "//MAP_GLOBAL_VARS:" above its real
                // GAME_GLOBAL_VARS header, so the scope must come from an uncommented line only.
                scope = header.Groups["scope"].Value == "GAME_GLOBAL_VARS"
                    ? FalloutVariableScope.Game
                    : FalloutVariableScope.Map;
                continue;
            }

            var comment = string.Empty;
            var code = line;
            var slashes = line.IndexOf("//", StringComparison.Ordinal);
            if (slashes >= 0)
            {
                code = line[..slashes];
                comment = line[(slashes + 2)..].Trim();
            }

            var declaration = DeclarationPattern().Match(code);
            if (!declaration.Success)
            {
                continue;
            }

            variables.Add(new FalloutGameVariable(
                variables.Count,
                declaration.Groups["name"].Value,
                int.Parse(declaration.Groups["value"].Value, CultureInfo.InvariantCulture),
                comment));
        }

        return new FalloutGameVariables(name, scope, variables);
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}

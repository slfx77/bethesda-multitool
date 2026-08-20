using System.Globalization;

namespace BethesdaMultitool.Core.WorldData.DayNight;

/// <summary>How a parsed enable/disable statement addresses its reference.</summary>
internal enum HourScheduleTargetKind
{
    /// <summary>Bare <c>Enable</c>/<c>Disable</c> — every placed instance of the scripted base.</summary>
    SelfInstance,

    /// <summary>
    ///     <c>SomePersistentREF.Enable</c> — a persistent reference addressed by editor ID.
    /// </summary>
    NamedReference,

    /// <summary>
    ///     <c>RefVar.Enable</c> where <c>RefVar</c> was assigned from <c>GetLinkedRef</c> — the
    ///     XLKR linked reference of each placed instance of the scripted base.
    /// </summary>
    LinkedReference,
}

/// <summary>One Enable/Disable statement with its guard, as authored in a GameMode block.</summary>
internal readonly record struct HourScheduleAction(
    HourScheduleTargetKind TargetKind,
    string TargetName,
    bool IsEnable,
    int Depth,
    IHourGuardNode Guard);

/// <summary>
///     Three-valued guard tree for one statement: hour comparisons evaluate True/False at a given
///     hour; every non-hour condition (quest variables, latches, GetInWorldSpace, …) is Unknown.
///     Unknown counts as "reachable" — the steady-state assumption that latch variables eventually
///     let the branch fire.
/// </summary>
internal interface IHourGuardNode
{
    HourTruth Evaluate(float hour);

    bool ContainsHourComparison { get; }
}

internal enum HourTruth
{
    False,
    Unknown,
    True,
}

internal sealed record HourGuardUnknown : IHourGuardNode
{
    internal static readonly HourGuardUnknown Instance = new();

    public HourTruth Evaluate(float hour) => HourTruth.Unknown;
    public bool ContainsHourComparison => false;
}

internal sealed record HourGuardComparison(string Operator, float Bound, bool HourOnLeft) : IHourGuardNode
{
    public HourTruth Evaluate(float hour)
    {
        var (left, right) = HourOnLeft ? (hour, Bound) : (Bound, hour);
        var result = Operator switch
        {
            "==" => left.Equals(right),
            "!=" => !left.Equals(right),
            "<" => left < right,
            "<=" => left <= right,
            ">" => left > right,
            ">=" => left >= right,
            _ => false,
        };
        return result ? HourTruth.True : HourTruth.False;
    }

    public bool ContainsHourComparison => true;
}

internal sealed record HourGuardNot(IHourGuardNode Inner) : IHourGuardNode
{
    public HourTruth Evaluate(float hour) => Inner.Evaluate(hour) switch
    {
        HourTruth.True => HourTruth.False,
        HourTruth.False => HourTruth.True,
        _ => HourTruth.Unknown,
    };

    public bool ContainsHourComparison => Inner.ContainsHourComparison;
}

internal sealed record HourGuardAnd(IHourGuardNode Left, IHourGuardNode Right) : IHourGuardNode
{
    public HourTruth Evaluate(float hour)
    {
        var l = Left.Evaluate(hour);
        if (l == HourTruth.False) return HourTruth.False;
        var r = Right.Evaluate(hour);
        if (r == HourTruth.False) return HourTruth.False;
        return l == HourTruth.Unknown || r == HourTruth.Unknown ? HourTruth.Unknown : HourTruth.True;
    }

    public bool ContainsHourComparison =>
        Left.ContainsHourComparison || Right.ContainsHourComparison;
}

internal sealed record HourGuardOr(IHourGuardNode Left, IHourGuardNode Right) : IHourGuardNode
{
    public HourTruth Evaluate(float hour)
    {
        var l = Left.Evaluate(hour);
        if (l == HourTruth.True) return HourTruth.True;
        var r = Right.Evaluate(hour);
        if (r == HourTruth.True) return HourTruth.True;
        return l == HourTruth.Unknown || r == HourTruth.Unknown ? HourTruth.Unknown : HourTruth.False;
    }

    public bool ContainsHourComparison =>
        Left.ContainsHourComparison || Right.ContainsHourComparison;
}

/// <summary>
///     Extracts the day/night Enable/Disable behavior authored in a GECK-format script (SCTX text
///     or our own decompiler's output). Recognizes the retail FNV patterns: direct
///     <c>GetCurrentTime</c>/<c>GameHour</c> comparisons, hour values staged through a local
///     (<c>set fTime to GetCurrentTime</c>), self-toggling object scripts, named persistent-ref
///     targets, and <c>GetLinkedRef</c> staging. Only <c>Begin GameMode</c> blocks participate —
///     OnLoad/OnActivate bodies restore or react to player state rather than defining the cycle.
/// </summary>
internal static class HourScheduleScriptParser
{
    internal static List<HourScheduleAction> Parse(string scriptText)
    {
        var actions = new List<HourScheduleAction>();
        if (string.IsNullOrWhiteSpace(scriptText)) return actions;

        // Pre-scan alias assignments anywhere in the script: hour aliases and linked-ref aliases.
        // GECK variable scope is script-wide, so a "set X to GetCurrentTime" inside the block also
        // covers comparisons textually above it within the same GameMode body.
        var hourAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var linkedRefAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = scriptText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var raw in lines)
        {
            var line = StripComment(raw).Trim();
            if (!line.StartsWith("set ", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = line[4..].Trim();
            var toIdx = rest.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
            if (toIdx <= 0) continue;
            var name = rest[..toIdx].Trim();
            var value = rest[(toIdx + 4)..].Trim();
            if (name.Contains('.', StringComparison.Ordinal)) continue;
            if (value.Equals("GetCurrentTime", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("GameHour", StringComparison.OrdinalIgnoreCase))
            {
                hourAliases.Add(name);
            }
            else if (value.Equals("GetLinkedRef", StringComparison.OrdinalIgnoreCase))
            {
                linkedRefAliases.Add(name);
            }
        }

        var inGameMode = false;
        // Guard stack: one entry per open `if`; each entry is the effective guard of the branch
        // currently taken (own condition AND the negation of every earlier branch in the chain).
        var branchGuards = new List<IHourGuardNode>();
        // Accumulated negation of earlier if/elseif conditions per open chain.
        var chainNegations = new List<IHourGuardNode?>();

        foreach (var raw in lines)
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("begin", StringComparison.OrdinalIgnoreCase))
            {
                var blockName = line[5..].Trim();
                inGameMode = blockName.StartsWith("GameMode", StringComparison.OrdinalIgnoreCase);
                branchGuards.Clear();
                chainNegations.Clear();
                continue;
            }

            if (line.Equals("end", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("end ", StringComparison.OrdinalIgnoreCase))
            {
                inGameMode = false;
                branchGuards.Clear();
                chainNegations.Clear();
                continue;
            }

            if (!inGameMode) continue;

            if (line.StartsWith("if ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("if(", StringComparison.OrdinalIgnoreCase))
            {
                var condition = ParseCondition(line[2..], hourAliases);
                branchGuards.Add(condition);
                chainNegations.Add(new HourGuardNot(condition));
                continue;
            }

            if (line.StartsWith("elseif", StringComparison.OrdinalIgnoreCase))
            {
                if (branchGuards.Count == 0) continue;
                var condition = ParseCondition(line[6..], hourAliases);
                var negation = chainNegations[^1] ?? HourGuardUnknown.Instance;
                branchGuards[^1] = new HourGuardAnd(negation, condition);
                chainNegations[^1] = new HourGuardAnd(negation, new HourGuardNot(condition));
                continue;
            }

            if (line.Equals("else", StringComparison.OrdinalIgnoreCase))
            {
                if (branchGuards.Count == 0) continue;
                branchGuards[^1] = chainNegations[^1] ?? HourGuardUnknown.Instance;
                continue;
            }

            if (line.Equals("endif", StringComparison.OrdinalIgnoreCase))
            {
                if (branchGuards.Count > 0)
                {
                    branchGuards.RemoveAt(branchGuards.Count - 1);
                    chainNegations.RemoveAt(chainNegations.Count - 1);
                }

                continue;
            }

            if (TryParseToggleStatement(line, linkedRefAliases, out var kind, out var target, out var isEnable))
            {
                var guard = ComposeGuards(branchGuards);
                actions.Add(new HourScheduleAction(kind, target, isEnable, branchGuards.Count, guard));
            }
        }

        return actions;
    }

    private static string StripComment(string line)
    {
        var idx = line.IndexOf(';', StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }

    private static IHourGuardNode ComposeGuards(List<IHourGuardNode> stack)
    {
        IHourGuardNode? combined = null;
        foreach (var guard in stack)
        {
            combined = combined is null ? guard : new HourGuardAnd(combined, guard);
        }

        return combined ?? HourGuardUnknown.Instance;
    }

    private static bool TryParseToggleStatement(
        string line,
        HashSet<string> linkedRefAliases,
        out HourScheduleTargetKind kind,
        out string target,
        out bool isEnable)
    {
        kind = HourScheduleTargetKind.SelfInstance;
        target = string.Empty;
        isEnable = false;

        // Statement form: [Target.]Enable|Disable [fadeArg]
        var firstToken = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        var dot = firstToken.LastIndexOf('.');
        string verb;
        string prefix;
        if (dot >= 0)
        {
            prefix = firstToken[..dot];
            verb = firstToken[(dot + 1)..];
        }
        else
        {
            prefix = string.Empty;
            verb = firstToken;
        }

        if (verb.Equals("Enable", StringComparison.OrdinalIgnoreCase))
        {
            isEnable = true;
        }
        else if (!verb.Equals("Disable", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (prefix.Length == 0)
        {
            kind = HourScheduleTargetKind.SelfInstance;
            return true;
        }

        if (prefix.Contains('.', StringComparison.Ordinal))
        {
            return false; // Quest.Var.Enable — not a form we schedule.
        }

        if (linkedRefAliases.Contains(prefix))
        {
            kind = HourScheduleTargetKind.LinkedReference;
            return true;
        }

        kind = HourScheduleTargetKind.NamedReference;
        target = prefix;
        return true;
    }

    private static IHourGuardNode ParseCondition(string text, HashSet<string> hourAliases)
    {
        var tokens = Tokenize(text);
        var pos = 0;
        var node = ParseOr(tokens, ref pos, hourAliases);
        return node;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c is '(' or ')')
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }

            if (c is '&' or '|')
            {
                if (i + 1 < text.Length && text[i + 1] == c)
                {
                    tokens.Add(new string(c, 2));
                    i += 2;
                    continue;
                }

                i++;
                continue;
            }

            if (c is '=' or '!' or '<' or '>')
            {
                if (i + 1 < text.Length && text[i + 1] == '=')
                {
                    tokens.Add(text.Substring(i, 2));
                    i += 2;
                }
                else
                {
                    tokens.Add(c.ToString());
                    i++;
                }

                continue;
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) &&
                   text[i] is not ('(' or ')' or '&' or '|' or '=' or '!' or '<' or '>'))
            {
                i++;
            }

            tokens.Add(text[start..i]);
        }

        return tokens;
    }

    private static IHourGuardNode ParseOr(List<string> tokens, ref int pos, HashSet<string> hourAliases)
    {
        var left = ParseAnd(tokens, ref pos, hourAliases);
        while (pos < tokens.Count && tokens[pos] == "||")
        {
            pos++;
            var right = ParseAnd(tokens, ref pos, hourAliases);
            left = new HourGuardOr(left, right);
        }

        return left;
    }

    private static IHourGuardNode ParseAnd(List<string> tokens, ref int pos, HashSet<string> hourAliases)
    {
        var left = ParseTerm(tokens, ref pos, hourAliases);
        while (pos < tokens.Count && tokens[pos] == "&&")
        {
            pos++;
            var right = ParseTerm(tokens, ref pos, hourAliases);
            left = new HourGuardAnd(left, right);
        }

        return left;
    }

    private static IHourGuardNode ParseTerm(List<string> tokens, ref int pos, HashSet<string> hourAliases)
    {
        if (pos >= tokens.Count) return HourGuardUnknown.Instance;

        if (tokens[pos] == "(")
        {
            pos++;
            var inner = ParseOr(tokens, ref pos, hourAliases);
            if (pos < tokens.Count && tokens[pos] == ")") pos++;
            return inner;
        }

        // Collect operand tokens until a comparator, boolean operator, or close paren. Multi-token
        // operands (e.g. "Player.GetInWorldSpace TheStripWorldnew") collapse into one opaque value.
        var leftTokens = CollectOperand(tokens, ref pos);
        if (pos >= tokens.Count || tokens[pos] is not ("==" or "!=" or "<" or "<=" or ">" or ">="))
        {
            return HourGuardUnknown.Instance; // bare identifier or call — truth unknown
        }

        var op = tokens[pos];
        pos++;
        var rightTokens = CollectOperand(tokens, ref pos);

        var leftHour = IsHourOperand(leftTokens, hourAliases);
        var rightHour = IsHourOperand(rightTokens, hourAliases);
        if (leftHour && TryParseNumber(rightTokens, out var rightValue))
        {
            return new HourGuardComparison(op, rightValue, HourOnLeft: true);
        }

        if (rightHour && TryParseNumber(leftTokens, out var leftValue))
        {
            return new HourGuardComparison(op, leftValue, HourOnLeft: false);
        }

        return HourGuardUnknown.Instance;
    }

    private static List<string> CollectOperand(List<string> tokens, ref int pos)
    {
        var operand = new List<string>();
        while (pos < tokens.Count &&
               tokens[pos] is not ("&&" or "||" or ")" or "==" or "!=" or "<" or "<=" or ">" or ">="))
        {
            if (tokens[pos] == "(")
            {
                // A parenthesized sub-expression inside an operand position — treat the whole
                // remainder as opaque by skipping the balanced group.
                var depth = 0;
                while (pos < tokens.Count)
                {
                    if (tokens[pos] == "(") depth++;
                    if (tokens[pos] == ")" && --depth == 0)
                    {
                        pos++;
                        break;
                    }

                    pos++;
                }

                operand.Add("(…)");
                continue;
            }

            operand.Add(tokens[pos]);
            pos++;
        }

        return operand;
    }

    private static bool IsHourOperand(List<string> operand, HashSet<string> hourAliases) =>
        operand.Count == 1 &&
        (operand[0].Equals("GetCurrentTime", StringComparison.OrdinalIgnoreCase) ||
         operand[0].Equals("GameHour", StringComparison.OrdinalIgnoreCase) ||
         hourAliases.Contains(operand[0]));

    private static bool TryParseNumber(List<string> operand, out float value)
    {
        value = 0f;
        return operand.Count == 1 &&
               float.TryParse(operand[0], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}

using System.Text;

namespace BethesdaMultitool.Core.Formats.Fallout;

/// <summary>
///     A Fallout <c>.LST</c> index: one file name per line, addressed by 1-based line number.
///     <para>
///         This is how a prototype id becomes a file. ⚠
///         <b>
///             The obvious rule — that
///             <c>PROTO\SCENERY\00000123.PRO</c> is prototype 123 — is wrong
///         </b>
///         , and quietly so: it
///         holds for most prototypes but fails for 1,151 of the 4,306 retail ones, 886 of them
///         scenery. Measured 2026-09-06: taking the PID's low 24 bits as a
///         <b>
///             1-based line number in
///             the type's <c>.LST</c>
///         </b>
///         , and reading the file that line names, resolves
///         <b>4,306/4,306</b> — every directory, no exceptions, and the line count equals the
///         prototype count in each.
///     </para>
///     <para>
///         The same shape indexes art (<c>ART\*\*.LST</c>) and scripts (<c>SCRIPTS\SCRIPTS.LST</c>),
///         so this reader serves those too. Lines may carry trailing comments after a <c>;</c> —
///         <c>SCRIPTS.LST</c> uses them heavily ("obj_dude.int   ; player script") — and the name is
///         what precedes it.
///     </para>
/// </summary>
internal sealed class FalloutProList
{
    private FalloutProList(string name, IReadOnlyList<string> names)
    {
        Name = name;
        Names = names;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>How many entries the list holds.</summary>
    public int Count => Names.Count;

    /// <summary>The entries in file order, comments stripped.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>Parses a list file. Blank lines are dropped; a <c>;</c> comment is trimmed off.</summary>
    public static FalloutProList Parse(ReadOnlySpan<byte> bytes, string name)
    {
        // The DOS-era text is code page 437; Latin-1 round-trips the byte values the few accented
        // names use, and nothing here depends on their glyphs.
        var text = Encoding.Latin1.GetString(bytes);
        var names = new List<string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            var comment = line.IndexOf(';', StringComparison.Ordinal);
            if (comment >= 0)
            {
                line = line[..comment];
            }

            line = line.Trim();
            if (line.Length > 0)
            {
                names.Add(line);
            }
        }

        return new FalloutProList(name, names);
    }

    /// <summary>
    ///     The file a prototype id names, or null when the id falls outside the list. Takes the whole
    ///     PID — the type byte is masked off here so callers cannot forget to.
    /// </summary>
    public string? Resolve(uint protoId)
    {
        var index = (int)(protoId & 0xFFFFFF);
        return index >= 1 && index <= Names.Count ? Names[index - 1] : null;
    }

    /// <summary>The <c>.LST</c> that indexes a prototype family, relative to the data root.</summary>
    public static string PathFor(FalloutProType type)
    {
        var directory = DirectoryFor(type);
        return $"PROTO/{directory}/{directory}.LST";
    }

    /// <summary>The <c>PROTO\</c> subdirectory a family lives in.</summary>
    public static string DirectoryFor(FalloutProType type)
    {
        return type switch
        {
            FalloutProType.Item => "ITEMS",
            FalloutProType.Critter => "CRITTERS",
            FalloutProType.Scenery => "SCENERY",
            FalloutProType.Wall => "WALLS",
            FalloutProType.Tile => "TILES",
            FalloutProType.Misc => "MISC",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "not one of the six prototype families")
        };
    }
}

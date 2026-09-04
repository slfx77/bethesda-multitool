using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     A Daggerfall quest: its text half (<c>.QRC</c>) and its compiled half (<c>.QBN</c>), which
///     share a base name in ARENA2.
///     <para>
///         The <c>.QRC</c> is a TEXT.RSC image — same u16 header length, same (id, offset) table
///         with a terminator entry, same token grammar — so it is parsed by
///         <see cref="DaggerfallTextFile" />. Measured on retail (2026-09-03): all 229 QRC files
///         parse that way, every terminator's offset equals the file length, and message ids run
///         1,000-2,999 (quest-local, unlike TEXT.RSC's 0-9,999).
///     </para>
///     <para>
///         The <c>.QBN</c> is the compiled quest program. Its layout is NOT documented in any
///         source available here, so nothing is decoded from it: the header words are surfaced raw
///         for the record browser and the rest is left alone. What is measured: 232 files,
///         254-13,006 bytes, 225 of them opening with sixteen zero bytes, and the u16 at offset 36
///         is 60 in every file.
///     </para>
/// </summary>
internal sealed class DaggerfallQuestFile
{
    /// <summary>Header words surfaced from a QBN (the fixed part before any decoded section).</summary>
    public const int QbnHeaderWords = 32;

    private DaggerfallQuestFile()
    {
    }

    /// <summary>Base name shared by the two halves (e.g. <c>S0000002</c>).</summary>
    public required string Name { get; init; }

    /// <summary>The parsed text half, or null when the install has no <c>.QRC</c> for this quest.</summary>
    public required DaggerfallTextFile? Text { get; init; }

    /// <summary>Bytes of the compiled half, or empty when the install has no <c>.QBN</c>.</summary>
    public required ReadOnlyMemory<byte> Compiled { get; init; }

    /// <summary>The first <see cref="QbnHeaderWords" /> little-endian words of the QBN, undecoded.</summary>
    public required IReadOnlyList<ushort> CompiledHeader { get; init; }

    /// <summary>Message ids in the text half, in table order.</summary>
    public IEnumerable<int> MessageIds => Text?.Records.Select(r => r.Id) ?? [];

    /// <summary>Loads one quest by base name from a data root.</summary>
    public static DaggerfallQuestFile Load(string dataRoot, string name)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        ArgumentNullException.ThrowIfNull(name);

        var textPath = Path.Combine(dataRoot, name + ".QRC");
        var compiledPath = Path.Combine(dataRoot, name + ".QBN");
        return Create(
            name,
            File.Exists(textPath) ? File.ReadAllBytes(textPath) : null,
            File.Exists(compiledPath) ? File.ReadAllBytes(compiledPath) : null);
    }

    /// <summary>Builds a quest from the bytes of its two halves (either may be absent).</summary>
    public static DaggerfallQuestFile Create(string name, byte[]? textBytes, byte[]? compiledBytes)
    {
        ArgumentNullException.ThrowIfNull(name);

        var header = new ushort[QbnHeaderWords];
        if (compiledBytes is not null)
        {
            for (var i = 0; i < QbnHeaderWords && (i + 1) * 2 <= compiledBytes.Length; i++)
            {
                header[i] = BinaryPrimitives.ReadUInt16LittleEndian(compiledBytes.AsSpan(i * 2));
            }
        }

        return new DaggerfallQuestFile
        {
            Name = name.ToUpperInvariant(),
            Text = textBytes is null ? null : DaggerfallTextFile.Parse(textBytes),
            Compiled = compiledBytes ?? ReadOnlyMemory<byte>.Empty,
            CompiledHeader = header
        };
    }

    /// <summary>
    ///     Every quest in a data root, by base name: any file with a <c>.QRC</c> or <c>.QBN</c>
    ///     extension contributes, so a quest with only one half still appears.
    /// </summary>
    public static IReadOnlyList<string> EnumerateNames(string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);

        if (!Directory.Exists(dataRoot))
        {
            return [];
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(dataRoot))
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".QRC", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".QBN", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(Path.GetFileNameWithoutExtension(path).ToUpperInvariant());
            }
        }

        return [.. names.Order(StringComparer.Ordinal)];
    }
}

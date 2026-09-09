using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     The per-game half of <see cref="TravelsRecordSynthesizer" />: the signature prefix and the
///     FormID domains one Travels game was given, plus the list the synthesized records accumulate
///     in. Stormhold and Dawnstar share every table reader and every record shape, so this is the
///     entire difference between them.
///     <para>
///         The composite index it builds — <c>(tableId &lt;&lt; 16) | rowOrdinal</c> — is what lets
///         ten tables share one 24-bit domain while each row keeps the ordinal the game itself uses
///         as a foreign key. The 16-bit ceiling on the ordinal is checked rather than masked: a
///         table that outgrew it would otherwise alias two rows silently.
///     </para>
/// </summary>
internal sealed class TravelsRecordContext
{
    /// <summary>Row ordinals the composite index can carry.</summary>
    public const int MaxRowOrdinal = 0xFFFF;

    private readonly List<GenericEsmRecord> _output;

    /// <summary>Creates the record context for one game's synthetic domains.</summary>
    /// <param name="signaturePrefix">One letter naming the game: <c>S</c> Stormhold, <c>D</c> Dawnstar.</param>
    /// <param name="firstDomain">The game's first reserved domain; table rows land here.</param>
    /// <param name="output">The list records are appended to.</param>
    public TravelsRecordContext(string signaturePrefix, byte firstDomain, List<GenericEsmRecord> output)
    {
        ArgumentNullException.ThrowIfNull(signaturePrefix);
        ArgumentNullException.ThrowIfNull(output);

        SignaturePrefix = signaturePrefix;
        TableDomain = firstDomain;
        StringDomain = (byte)(firstDomain + 1);
        _output = output;
    }

    /// <summary>The letter every record signature of this game starts with.</summary>
    public string SignaturePrefix { get; }

    /// <summary>Domain carrying the data-table rows, keyed by the composite table/row index.</summary>
    public byte TableDomain { get; }

    /// <summary>Domain carrying <c>npcstrings.dat</c> lines, keyed by <c>group &lt;&lt; 8 | line</c>.</summary>
    public byte StringDomain { get; }

    /// <summary>The four-character signature for one of the synthesizer's three-letter codes.</summary>
    public string Signature(string code)
    {
        return SignaturePrefix + code;
    }

    /// <summary>Appends an already-built record.</summary>
    public void Add(GenericEsmRecord record)
    {
        _output.Add(record);
    }

    /// <summary>Builds one table-row record with its composite identity.</summary>
    public GenericEsmRecord Create(
        string code,
        int tableId,
        int rowOrdinal,
        string editorId,
        string? fullName,
        Dictionary<string, object?> fields)
    {
        ArgumentNullException.ThrowIfNull(editorId);
        ArgumentNullException.ThrowIfNull(fields);

        if (rowOrdinal is < 0 or > MaxRowOrdinal)
        {
            throw new InvalidDataException(
                $"Travels table 0x{tableId:X2} row ordinal {rowOrdinal} does not fit the 16 bits "
                + "the composite record index leaves it.");
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(TableDomain, (uint)((tableId << 16) | rowOrdinal)),
            RecordType = Signature(code),
            EditorId = ClassicRecordNaming.ToEditorId(editorId),
            FullName = fullName,
            Fields = fields
        };
    }
}

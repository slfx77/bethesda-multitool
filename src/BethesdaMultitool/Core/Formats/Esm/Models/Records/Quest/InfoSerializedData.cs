namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

/// <summary>
///     The INFO <c>DATA</c> subrecord of a Fallout 3 / New Vegas plugin, as serialized: four single-byte
///     fields in the order the generated <c>FalloutNvSchema</c> and <c>Fallout3Schema</c> INFO DATA struct
///     declares them (Type, Next Speaker, Flags 1, Flags 2). Every field is one byte, so the values read the
///     same from a big-endian (Xbox 360) and a little-endian (PC) container.
///     <para>
///         Presentation-only. It deliberately does NOT feed <see cref="DialogueRecord.InfoFlags" /> or
///         <see cref="DialogueRecord.InfoFlagsExt" />: those drive <c>dmp to-esm</c> (goodbye detection and the
///         INFO encoder), and wiring the parsed DATA into them is a separate converter decision.
///     </para>
///     <para>
///         Names for the values are not held here. Presenters take them from the generated schema for the
///         game being shown, so show, reports and the GUI name the same bit the same way (Fallout 3 names two
///         Flags 2 bits, New Vegas six).
///     </para>
/// </summary>
public sealed record InfoSerializedData
{
    /// <summary>Offset of the Type byte (the schema's first DATA field).</summary>
    public const int TypeOffset = 0;

    /// <summary>Offset of the Next Speaker byte (the schema's second DATA field).</summary>
    public const int NextSpeakerOffset = 1;

    /// <summary>Offset of the Flags 1 byte (the schema's third DATA field).</summary>
    public const int Flags1Offset = 2;

    /// <summary>Offset of the Flags 2 byte (the schema's fourth DATA field).</summary>
    public const int Flags2Offset = 3;

    /// <summary>Length of a complete DATA payload: the four schema fields.</summary>
    public const int CompleteLength = 4;

    /// <summary>Length of the DATA payload as stored. Bytes beyond <see cref="CompleteLength" /> are not interpreted.</summary>
    public required int DataLength { get; init; }

    /// <summary>INFO type (Topic, Conversation, Combat, ...), the first DATA byte.</summary>
    public required byte InfoType { get; init; }

    /// <summary>Next speaker (Target, Self, Either), or null when DATA ends before it.</summary>
    public byte? NextSpeaker { get; init; }

    /// <summary>Flags 1 (Goodbye, Random, Say Once, ...), or null when DATA ends before it.</summary>
    public byte? Flags1 { get; init; }

    /// <summary>Flags 2 (Say Once a Day, Always Darken, ...), or null when DATA ends before it.</summary>
    public byte? Flags2 { get; init; }

    /// <summary>True when the stored DATA is shorter than the four schema fields.</summary>
    public bool IsTruncated => DataLength < CompleteLength;

    /// <summary>
    ///     Reads a DATA payload. Returns null for an empty payload; a shorter payload keeps the fields it has
    ///     and leaves the rest null, and a longer one is read for its first four bytes only.
    /// </summary>
    public static InfoSerializedData? TryRead(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return null;
        }

        return new InfoSerializedData
        {
            DataLength = data.Length,
            InfoType = data[TypeOffset],
            NextSpeaker = data.Length > NextSpeakerOffset ? data[NextSpeakerOffset] : null,
            Flags1 = data.Length > Flags1Offset ? data[Flags1Offset] : null,
            Flags2 = data.Length > Flags2Offset ? data[Flags2Offset] : null
        };
    }
}

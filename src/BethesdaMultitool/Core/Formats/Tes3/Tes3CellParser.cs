using BethesdaMultitool.Core.Formats.Esm;
namespace BethesdaMultitool.Core.Formats.Tes3;

/// <summary>
///     A placed object inside a TES3 cell, as read from the cell's reference stream (before base-record
///     resolution). The base object is named by its editor-id <b>string</b> (TES3 has no FormIDs).
/// </summary>
internal sealed class Tes3RefDraft
{
    public string? BaseId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float RotX { get; set; }
    public float RotY { get; set; }
    public float RotZ { get; set; }
    public float Scale { get; set; } = 1.0f;
}

/// <summary>
///     The decoded form of one TES3 CELL: header fields plus the list of placed references. Built by
///     <see cref="Tes3CellParser" /> while the record's bytes are in scope; base-record model/FormID
///     resolution happens later (it needs the whole-file editor-id index).
/// </summary>
internal sealed class Tes3CellDraft
{
    public uint FormId { get; set; }
    public long Offset { get; set; }
    public string? Name { get; set; }
    public string? Region { get; set; }
    public int Flags { get; set; }
    public int GridX { get; set; }
    public int GridY { get; set; }
    public float? WaterHeight { get; set; }
    public bool IsInterior => (Flags & 0x01) != 0;
    public List<Tes3RefDraft> References { get; } = [];
}

/// <summary>
///     Parses a Morrowind CELL record's bytes into a <see cref="Tes3CellDraft" />. A TES3 cell is a
///     header (NAME/DATA/RGNN/WHGT/AMBI/…) followed by a flat reference stream: each placed object
///     starts with an <c>FRMR</c> (object index) and carries a <c>NAME</c> (base editor-id), an
///     optional <c>XSCL</c> (scale), and a 24-byte <c>DATA</c> (position xyz + rotation xyz, radians).
///     Header vs reference is disambiguated by the first FRMR.
/// </summary>
internal static class Tes3CellParser
{
    public static Tes3CellDraft Parse(byte[] data, int dataSize, uint formId, long offset)
    {
        var draft = new Tes3CellDraft { FormId = formId, Offset = offset };
        var inReferences = false;
        Tes3RefDraft? current = null;

        foreach (var sub in Tes3SubrecordUtils.IterateSubrecords(data, dataSize))
        {
            var span = data.AsSpan(sub.DataOffset, sub.DataLength);

            if (sub.Signature == "FRMR")
            {
                if (current != null)
                {
                    draft.References.Add(current);
                }

                current = new Tes3RefDraft();
                inReferences = true;
                continue;
            }

            if (!inReferences)
            {
                ApplyHeaderField(draft, sub.Signature, span);
            }
            else if (current != null)
            {
                ApplyReferenceField(current, sub.Signature, span);
            }
        }

        if (current != null)
        {
            draft.References.Add(current);
        }

        return draft;
    }

    private static void ApplyHeaderField(Tes3CellDraft draft, string sig, ReadOnlySpan<byte> span)
    {
        var c = new Tes3Cursor(span);
        switch (sig)
        {
            case "NAME":
                draft.Name = c.ReadRemainingString();
                break;
            case "DATA" when span.Length >= 12:
                draft.Flags = c.ReadInt32();
                draft.GridX = c.ReadInt32();
                draft.GridY = c.ReadInt32();
                break;
            case "RGNN":
                draft.Region = c.ReadRemainingString();
                break;
            case "WHGT":
                draft.WaterHeight = c.ReadFloat();
                break;
        }
    }

    private static void ApplyReferenceField(Tes3RefDraft reference, string sig, ReadOnlySpan<byte> span)
    {
        var c = new Tes3Cursor(span);
        switch (sig)
        {
            case "NAME":
                reference.BaseId = c.ReadRemainingString();
                break;
            case "XSCL":
                reference.Scale = c.ReadFloat();
                break;
            case "DATA" when span.Length >= 24:
                reference.X = c.ReadFloat();
                reference.Y = c.ReadFloat();
                reference.Z = c.ReadFloat();
                reference.RotX = c.ReadFloat();
                reference.RotY = c.ReadFloat();
                reference.RotZ = c.ReadFloat();
                break;
        }
    }
}

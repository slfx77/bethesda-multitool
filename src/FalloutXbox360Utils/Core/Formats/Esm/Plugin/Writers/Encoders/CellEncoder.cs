using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a new <see cref="CellRecord" /> as PC-format CELL subrecord bytes — used for
///     DMP cells that don't exist in the master ESM. v4 supports both interior cells and
///     exterior cells (with synthetic XCLC).
///
///     Subrecords emitted in fopdoc-canonical CELL order:
///     EDID, FULL?, DATA, XCLC?, LTMP?, LNAM?, XCLW?, XEZN?, XCAS?, XCMO?, XCIM?.
///     For interior cells (v3+), bit 0 of DATA is set and XCLC is omitted.
///     For exterior cells (v4+), bit 0 of DATA is cleared and XCLC carries the grid coords.
/// </summary>
public sealed class CellEncoder : IRecordEncoder
{
    public string RecordType => "CELL";
    public Type ModelType => typeof(CellRecord);

    public EncodedRecord Encode(object model)
    {
        var cell = (CellRecord)model;
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(cell.EditorId))
        {
            warnings.Add($"New CELL 0x{cell.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        // EDID — required.
        subs.Add(EncodeStringSubrecord("EDID", cell.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(cell.FullName))
        {
            subs.Add(EncodeStringSubrecord("FULL", cell.FullName));
        }

        // DATA — 1 byte cell flags. Bit 0 = interior; clear it for exterior.
        // The model's IsInterior is computed from Flags bit 0 already, but we sanitize:
        // if cell.IsInterior, force bit 0 on; else force it off.
        var rawFlags = cell.Flags;
        var dataFlags = cell.IsInterior
            ? (byte)(rawFlags | 0x01)
            : (byte)(rawFlags & ~0x01);
        subs.Add(new EncodedSubrecord("DATA", [dataFlags]));

        // XCLC — exterior cells only. 12 bytes: int32 X, int32 Y, uint32 ForceHideLand=0.
        if (!cell.IsInterior)
        {
            if (!cell.GridX.HasValue || !cell.GridY.HasValue)
            {
                warnings.Add(
                    $"Exterior CELL 0x{cell.FormId:X8} has no grid coords — emitting XCLC at (0,0).");
            }

            var xclc = new byte[12];
            SubrecordEncoder.WriteInt32(xclc, 0, cell.GridX ?? 0);
            SubrecordEncoder.WriteInt32(xclc, 4, cell.GridY ?? 0);
            SubrecordEncoder.WriteUInt32(xclc, 8, 0); // ForceHideLand
            subs.Add(new EncodedSubrecord("XCLC", xclc));
        }

        // LTMP / LNAM — lighting template and inheritance flags. Both are typically present
        // together; emit in canonical order (LTMP before LNAM).
        if (cell.LightingTemplateFormId.HasValue)
        {
            subs.Add(EncodeFormIdSubrecord("LTMP", cell.LightingTemplateFormId.Value));
        }

        if (cell.LightingTemplateInheritanceFlags.HasValue)
        {
            var lnam = new byte[4];
            SubrecordEncoder.WriteUInt32(lnam, 0, cell.LightingTemplateInheritanceFlags.Value);
            subs.Add(new EncodedSubrecord("LNAM", lnam));
        }

        if (cell.WaterHeight.HasValue)
        {
            var xclw = new byte[4];
            SubrecordEncoder.WriteFloat(xclw, 0, cell.WaterHeight.Value);
            subs.Add(new EncodedSubrecord("XCLW", xclw));
        }

        // XCLR — array of REGN FormIDs supplying per-area radiation. Emit when the cell
        // captured at least one region. Without this, radiation regions don't carry over
        // to the override and irradiated cells lose their hazard.
        if (cell.RadiationRegionFormIds.Count > 0)
        {
            var xclr = new byte[cell.RadiationRegionFormIds.Count * 4];
            for (var i = 0; i < cell.RadiationRegionFormIds.Count; i++)
            {
                SubrecordEncoder.WriteFormId(xclr, i * 4, cell.RadiationRegionFormIds[i]);
            }

            subs.Add(new EncodedSubrecord("XCLR", xclr));
        }

        if (cell.EncounterZoneFormId.HasValue)
        {
            subs.Add(EncodeFormIdSubrecord("XEZN", cell.EncounterZoneFormId.Value));
        }

        if (cell.AcousticSpaceFormId.HasValue)
        {
            subs.Add(EncodeFormIdSubrecord("XCAS", cell.AcousticSpaceFormId.Value));
        }

        if (cell.MusicTypeFormId.HasValue)
        {
            subs.Add(EncodeFormIdSubrecord("XCMO", cell.MusicTypeFormId.Value));
        }

        if (cell.ImageSpaceFormId.HasValue)
        {
            subs.Add(EncodeFormIdSubrecord("XCIM", cell.ImageSpaceFormId.Value));
        }

        return new EncodedRecord
        {
            Subrecords = subs,
            Warnings = warnings
        };
    }

    private static EncodedSubrecord EncodeStringSubrecord(string signature, string value)
    {
        var byteCount = System.Text.Encoding.Latin1.GetByteCount(value);
        var buffer = new byte[byteCount + 1];
        System.Text.Encoding.Latin1.GetBytes(value, buffer);
        // Final byte already 0 (null terminator).
        return new EncodedSubrecord(signature, buffer);
    }

    private static EncodedSubrecord EncodeFormIdSubrecord(string signature, uint formId)
    {
        var bytes = new byte[4];
        SubrecordEncoder.WriteFormId(bytes, 0, formId);
        return new EncodedSubrecord(signature, bytes);
    }
}

using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a new <see cref="CellRecord" /> as PC-format CELL subrecord bytes — used for
///     DMP cells that don't exist in the master ESM. Supports both interior cells and
///     exterior cells (with synthetic XCLC).
///     Subrecords emitted in fopdoc-canonical CELL order:
///     EDID, FULL?, DATA, XCLC?, LTMP?, LNAM?, XCLW?, XCLR?, XCLL?, XEZN?, XCAS?, XCMO?, XCIM?.
///     For interior cells, bit 0 of DATA is set and XCLC is omitted.
///     For exterior cells, bit 0 of DATA is cleared and XCLC carries the grid coords.
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

        // LTMP / LNAM — lighting template and inheritance flags. LNAM (inheritance flags) is
        // only meaningful paired with LTMP; FNVEdit flags lone LNAM as "unexpected (or out of
        // order)". Without an explicit LTMP, the cell inherits the worldspace default — that's
        // the safe behavior, so we drop a stray LNAM rather than synthesize a zero LTMP.
        if (cell.LightingTemplateFormId.HasValue)
        {
            subs.Add(EncodeFormIdSubrecord("LTMP", cell.LightingTemplateFormId.Value));

            if (cell.LightingTemplateInheritanceFlags.HasValue)
            {
                var lnam = new byte[4];
                SubrecordEncoder.WriteUInt32(lnam, 0, cell.LightingTemplateInheritanceFlags.Value);
                subs.Add(new EncodedSubrecord("LNAM", lnam));
            }
        }

        if (ShouldEmitCellWater(cell, warnings))
        {
            var xclw = new byte[4];
            SubrecordEncoder.WriteFloat(xclw, 0, cell.WaterHeight.GetValueOrDefault());
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

        // XCLL — direct cell lighting. Existing master cells inherit their original XCLL
        // because PluginBuilder emits the master CELL anchor verbatim; this matters for
        // new DMP-only cells where no master CELL exists to supply ambient/fog lighting.
        if (cell.LightingData is not null)
        {
            var schema = SubrecordSchemaRegistry.GetSchema("XCLL", "CELL", 40);
            if (schema is not null)
            {
                subs.Add(new EncodedSubrecord("XCLL",
                    SchemaDictionarySerializer.Serialize(schema, cell.LightingData)));
            }
            else
            {
                warnings.Add($"CELL 0x{cell.FormId:X8} has XCLL lighting data but no schema was registered.");
            }
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

    private static bool ShouldEmitCellWater(CellRecord cell, List<string> warnings)
    {
        if (!cell.WaterHeight.HasValue)
        {
            return false;
        }

        // XCLW is only meaningful for cells marked as water-bearing. Runtime CELL captures
        // often expose a stale fWaterHeight while the DATA water flag is clear; emitting
        // that value makes dry cells render as flooded.
        if (!cell.HasWater)
        {
            warnings.Add(
                $"CELL 0x{cell.FormId:X8} XCLW {cell.WaterHeight.Value:F1} ignored because DATA has no water flag.");
            return false;
        }

        if (!IsPlausibleCellWater(cell.WaterHeight.Value, cell.Heightmap, cell.IsInterior))
        {
            warnings.Add(
                $"CELL 0x{cell.FormId:X8} XCLW {cell.WaterHeight.Value:F1} would submerge the " +
                $"captured terrain — suppressed so the cell inherits the worldspace default.");
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Decide whether the captured XCLW water height is plausible for the cell. Uses
    ///     the heightmap (when present) to compute the cell's max terrain elevation and
    ///     rejects any water level more than <c>WaterAboveTerrainTolerance</c> above it.
    ///     Exterior cells without captured terrain are rejected because inheriting the
    ///     worldspace default is safer than trusting a runtime-only water-height float.
    /// </summary>
    private static bool IsPlausibleCellWater(
        float waterHeight,
        LandHeightmap? heightmap,
        bool isInterior)
    {
        // Allow water a bit above the highest terrain vertex (shorelines, lake-edges,
        // and rounding slack). Cells where water is plainly above the whole landform get
        // rejected so the engine inherits the worldspace default instead.
        const float WaterAboveTerrainTolerance = 256.0f;
        const float UnknownTerrainAbsCeiling = 10_000.0f;

        if (heightmap is not null)
        {
            var heights = heightmap.CalculateHeights();
            var maxTerrain = float.NegativeInfinity;
            for (var y = 0; y < heights.GetLength(0); y++)
            {
                for (var x = 0; x < heights.GetLength(1); x++)
                {
                    if (heights[y, x] > maxTerrain)
                    {
                        maxTerrain = heights[y, x];
                    }
                }
            }

            return waterHeight <= maxTerrain + WaterAboveTerrainTolerance;
        }

        return isInterior && MathF.Abs(waterHeight) <= UnknownTerrainAbsCeiling;
    }

    private static EncodedSubrecord EncodeStringSubrecord(string signature, string value)
    {
        var byteCount = Encoding.Latin1.GetByteCount(value);
        var buffer = new byte[byteCount + 1];
        Encoding.Latin1.GetBytes(value, buffer);
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

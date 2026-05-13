using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESRegion (REGN, 72 bytes, FormType 0x37).
///     Reads worldspace FormID + EmittanceColor RGB. Skips pCurrentWeather
///     (runtime-mutated) and pDataList/pPointLists (variable-length lists).
/// </summary>
internal sealed class RuntimeRegionReader(RuntimeMemoryContext context)
{
    public RegionRecord? ReadRuntimeRegion(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != RegnFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + StructSize > context.FileSize)
        {
            return null;
        }

        var buffer = new byte[StructSize];
        try
        {
            context.Accessor.ReadArray(offset, buffer, 0, StructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, FormIdOffset);
        if (formId != entry.FormId || formId == 0)
        {
            return null;
        }

        var worldspaceFormId = context.FollowPointerToFormId(buffer, WorldspacePointerOffset) ?? 0;

        // NiColor at +60: 3 floats R/G/B
        var r = BinaryUtils.ReadFloatBE(buffer, EmittanceColorOffset);
        var g = BinaryUtils.ReadFloatBE(buffer, EmittanceColorOffset + 4);
        var b = BinaryUtils.ReadFloatBE(buffer, EmittanceColorOffset + 8);

        return new RegionRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            WorldspaceFormId = worldspaceFormId,
            EmittanceColorR = FloatToByteColor(r),
            EmittanceColorG = FloatToByteColor(g),
            EmittanceColorB = FloatToByteColor(b),
            Offset = offset,
            IsBigEndian = true
        };
    }

    private static byte FloatToByteColor(float f)
    {
        if (!RuntimeMemoryContext.IsNormalFloat(f) || f < 0) return 0;
        if (f >= 1.0f) return 255;
        return (byte)(f * 255f);
    }

    #region Constants

    private const byte RegnFormType = 0x37;
    private const int StructSize = 72;
    private const int FormIdOffset = 12;
    private const int WorldspacePointerOffset = 48;
    private const int EmittanceColorOffset = 60;

    #endregion
}

using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESRegion (REGN, FormType 0x37).
///     Reads worldspace FormID + EmittanceColor RGB via the PDB layout. Skips
///     pCurrentWeather (runtime-mutated) and pDataList/pPointLists (variable-length).
/// </summary>
internal sealed class RuntimeRegionReader(RuntimeMemoryContext context)
{
    private const byte RegnFormType = 0x37;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public RegionRecord? ReadRuntimeRegion(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != RegnFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, RegnFormType);
        if (view == null)
        {
            return null;
        }

        var colorOff = view.Offset("EmittanceColor", "TESRegion");
        if (colorOff is not { } o || o + 12 > view.Buffer.Length)
        {
            return null;
        }

        // NiColor: 3 floats R/G/B.
        var r = BinaryUtils.ReadFloatBE(view.Buffer, o);
        var g = BinaryUtils.ReadFloatBE(view.Buffer, o + 4);
        var b = BinaryUtils.ReadFloatBE(view.Buffer, o + 8);

        return new RegionRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            WorldspaceFormId = view.FormIdPointer("pWorldSpace", "TESRegion") ?? 0,
            EmittanceColorR = FloatToByteColor(r),
            EmittanceColorG = FloatToByteColor(g),
            EmittanceColorB = FloatToByteColor(b),
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }

    private static byte FloatToByteColor(float f)
    {
        if (!RuntimeMemoryContext.IsNormalFloat(f) || f < 0) return 0;
        if (f >= 1.0f) return 255;
        return (byte)(f * 255f);
    }
}

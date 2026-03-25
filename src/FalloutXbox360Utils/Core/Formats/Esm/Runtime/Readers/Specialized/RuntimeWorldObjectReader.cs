using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Reader for runtime ACTI/LIGH/DOOR/STAT/FURN structs from Xbox 360 memory dumps.
/// </summary>
internal sealed class RuntimeWorldObjectReader(RuntimeMemoryContext context)
{
    private readonly RuntimePdbFieldAccessor _fields = new(context);

    internal ActivatorRecord? ReadRuntimeActivator(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != 0x15)
        {
            return null;
        }

        var structData = _fields.ReadStruct(entry);
        if (structData == null)
        {
            return null;
        }

        var (layout, buffer, fileOffset) = structData.Value;
        return new ActivatorRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName ?? _fields.ReadBsString(fileOffset, layout, "cFullName", "TESFullName", entry),
            ModelPath = _fields.ReadBsString(fileOffset, layout, "cModel", "TESModel", entry),
            Bounds = _fields.ReadBounds(buffer, layout),
            Script = _fields.ReadFormIdPointer(buffer, layout, "pFormScript", "TESScriptableForm", 0x11),
            ActivationSoundFormId = _fields.ReadFormIdPointer(buffer, layout, "pSoundActivate", "TESObjectACTI"),
            RadioStationFormId = _fields.ReadFormIdPointer(buffer, layout, "pRadioStation", "TESObjectACTI"),
            WaterTypeFormId = _fields.ReadFormIdPointer(buffer, layout, "pWaterForm", "TESObjectACTI", 0x4E),
            Offset = fileOffset,
            IsBigEndian = true
        };
    }

    internal LightRecord? ReadRuntimeLight(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != 0x1E)
        {
            return null;
        }

        var structData = _fields.ReadStruct(entry);
        if (structData == null)
        {
            return null;
        }

        var (layout, buffer, fileOffset) = structData.Value;
        var lightDataOffset = RuntimePdbFieldAccessor.FindFieldOffset(layout, "data", "TESObjectLIGH");
        if (!lightDataOffset.HasValue || lightDataOffset.Value + 24 > buffer.Length)
        {
            return null;
        }

        return new LightRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName ?? _fields.ReadBsString(fileOffset, layout, "cFullName", "TESFullName", entry),
            ModelPath = _fields.ReadBsString(fileOffset, layout, "cModel", "TESModel", entry),
            Bounds = _fields.ReadBounds(buffer, layout),
            Duration = RuntimePdbFieldAccessor.ReadInt32(buffer, lightDataOffset.Value),
            Radius = RuntimePdbFieldAccessor.ReadUInt32(buffer, lightDataOffset.Value + 4),
            Color = RuntimePdbFieldAccessor.ReadUInt32(buffer, lightDataOffset.Value + 8),
            Flags = RuntimePdbFieldAccessor.ReadUInt32(buffer, lightDataOffset.Value + 12),
            FalloffExponent = RuntimePdbFieldAccessor.ReadFloat(buffer, lightDataOffset.Value + 16),
            FOV = RuntimePdbFieldAccessor.ReadFloat(buffer, lightDataOffset.Value + 20),
            Value = (int)RuntimePdbFieldAccessor.ReadUInt32(buffer,
                RuntimePdbFieldAccessor.FindFieldOffset(layout, "iValue", "TESValueForm") ?? lightDataOffset.Value),
            Weight = RuntimePdbFieldAccessor.ReadFloat(buffer,
                RuntimePdbFieldAccessor.FindFieldOffset(layout, "fWeight", "TESWeightForm") ?? lightDataOffset.Value),
            Offset = fileOffset,
            IsBigEndian = true
        };
    }

    internal DoorRecord? ReadRuntimeDoor(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != 0x1C)
        {
            return null;
        }

        var structData = _fields.ReadStruct(entry);
        if (structData == null)
        {
            return null;
        }

        var (layout, buffer, fileOffset) = structData.Value;
        var flagsOffset = RuntimePdbFieldAccessor.FindFieldOffset(layout, "cFlags", "TESObjectDOOR");

        return new DoorRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName ?? _fields.ReadBsString(fileOffset, layout, "cFullName", "TESFullName", entry),
            ModelPath = _fields.ReadBsString(fileOffset, layout, "cModel", "TESModel", entry),
            Bounds = _fields.ReadBounds(buffer, layout),
            Script = _fields.ReadFormIdPointer(buffer, layout, "pFormScript", "TESScriptableForm", 0x11),
            OpenSoundFormId = _fields.ReadFormIdPointer(buffer, layout, "pOpenSound", "TESObjectDOOR"),
            CloseSoundFormId = _fields.ReadFormIdPointer(buffer, layout, "pCloseSound", "TESObjectDOOR"),
            LoopSoundFormId = _fields.ReadFormIdPointer(buffer, layout, "pLoopSound", "TESObjectDOOR"),
            Flags = flagsOffset.HasValue ? buffer[flagsOffset.Value] : (byte)0,
            Offset = fileOffset,
            IsBigEndian = true
        };
    }

    internal StaticRecord? ReadRuntimeStatic(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != 0x20)
        {
            return null;
        }

        var structData = _fields.ReadStruct(entry);
        if (structData == null)
        {
            return null;
        }

        var (layout, buffer, fileOffset) = structData.Value;
        return new StaticRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            ModelPath = _fields.ReadBsString(fileOffset, layout, "cModel", "TESModel", entry),
            Bounds = _fields.ReadBounds(buffer, layout),
            Offset = fileOffset,
            IsBigEndian = true
        };
    }

    internal FurnitureRecord? ReadRuntimeFurniture(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != 0x27)
        {
            return null;
        }

        var structData = _fields.ReadStruct(entry);
        if (structData == null)
        {
            return null;
        }

        var (layout, buffer, fileOffset) = structData.Value;
        var markerFlagsOffset = RuntimePdbFieldAccessor.FindFieldOffset(layout, "iFurnFlags", "TESFurniture");

        return new FurnitureRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName ?? _fields.ReadBsString(fileOffset, layout, "cFullName", "TESFullName", entry),
            ModelPath = _fields.ReadBsString(fileOffset, layout, "cModel", "TESModel", entry),
            Bounds = _fields.ReadBounds(buffer, layout),
            Script = _fields.ReadFormIdPointer(buffer, layout, "pFormScript", "TESScriptableForm", 0x11),
            MarkerFlags = markerFlagsOffset.HasValue
                ? RuntimePdbFieldAccessor.ReadUInt32(buffer, markerFlagsOffset.Value)
                : 0,
            Offset = fileOffset,
            IsBigEndian = true
        };
    }
}

using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for MediaLocationController (ALOC, FormType 0x70).
///     Reads FullName, the 4 timer uints, and the pAudioMarker pointer via the PDB
///     layout. Skips the boolean runtime-state fields (bIsActive/bInCombat/etc) since
///     they are mutated at runtime and not parity-relevant.
/// </summary>
internal sealed class RuntimeAudioLocationControllerReader(RuntimeMemoryContext context)
{
    private const byte AlocFormType = 0x70;
    private const byte RefrFormType = 0x3A;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public AudioLocationControllerRecord? ReadRuntimeAudioLocationController(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != AlocFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, AlocFormType);
        if (view == null)
        {
            return null;
        }

        return new AudioLocationControllerRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = view.BsString("cFullName", "TESFullName"),
            LocationDelay = view.UInt32("uiLocationDelay", "MediaLocationController"),
            LayerTime = view.UInt32("uiLayerTime", "MediaLocationController"),
            LoopTime = view.UInt32("uiLoopTime", "MediaLocationController"),
            MediaStartTime = view.UInt32("uiMediaStartTime", "MediaLocationController"),
            // pAudioMarker — TESObjectREFR* (REFR = FormType 0x3A). Constrained so a
            // stale pointer doesn't accidentally resolve to a nearby DIAL/ACTI/etc.
            AudioMarkerFormId = view.FormIdPointer("pAudioMarker", "MediaLocationController", RefrFormType),
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}

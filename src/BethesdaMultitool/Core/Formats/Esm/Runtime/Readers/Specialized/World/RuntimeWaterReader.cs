using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized.World;

/// <summary>
///     Typed runtime reader for TESWaterForm (WATR, 420 bytes, FormType 0x4E).
///     Reads the fields the ESM model exposes: FullName, Damage (sAttackDamage),
///     Opacity (cAlpha), SoundFormId (pWaterSound pointer), and the 196-byte
///     WaterShaderData block (mirrors the ESM DNAM schema). All offsets resolve
///     from <c>pdb_layouts.json</c> via <see cref="PdbStructView" />.
/// </summary>
internal sealed class RuntimeWaterReader(RuntimeMemoryContext context)
{
    private const byte WatrFormType = 0x4E;
    private const int ShaderDataSize = 196;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    /// <summary>Reads the runtime water-type record for the given DMP entry, or null if it can't be read.</summary>
    public WaterRecord? ReadRuntimeWater(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != WatrFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, WatrFormType);
        if (view == null)
        {
            return null;
        }

        var fullName = view.BsString("cFullName", "TESFullName");
        var damage = view.UInt16("sAttackDamage", "TESAttackDamageForm");
        var opacity = view.Byte("cAlpha", "TESWaterForm");
        var soundFormId = view.FormIdPointer("pWaterSound", "TESWaterForm");

        Dictionary<string, object?>? visualProperties = null;
        if (view.Offset("Data", "TESWaterForm") is { } shaderOff)
        {
            var shaderBytes = new byte[ShaderDataSize];
            Array.Copy(view.Buffer, shaderOff, shaderBytes, 0, ShaderDataSize);
            visualProperties = SubrecordSchemaView.TryRead("DNAM", "WATR", shaderBytes, true)?.Raw;
        }

        return new WaterRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            Damage = damage,
            Opacity = opacity,
            SoundFormId = soundFormId,
            VisualProperties = visualProperties,
            RelatedWater = ReadRelatedWaters(view),
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     <c>TESWaterForm.pWaterWeatherControl</c> is <c>TESWaterForm*[3]</c> (12 bytes at +344 in
    ///     the PDB) — the engine's slot for the ESM's GNAM "Related Waters" in xEdit order:
    ///     Daytime, Nighttime, Underwater. It was the last never-read array field in the water
    ///     reader (the parity matrix carried it as "unknown PDB type" because the generic container
    ///     reader cannot type a pointer array). Each element is type-validated as WATR; a slot that
    ///     does not resolve to a water form is recorded as FormID 0, which is exactly what retail
    ///     GNAM carries for an unset related water. Null when no slot resolves at all, so a record
    ///     the capture could not evidence never overrides an ESM-parsed GNAM with zeros.
    /// </summary>
    private Dictionary<string, object?>? ReadRelatedWaters(PdbStructView view)
    {
        if (view.Offset("pWaterWeatherControl", "TESWaterForm") is not { } relatedOff
            || relatedOff + 12 > view.Buffer.Length)
        {
            return null;
        }

        var daytime = _fields.ReadPointerToFormId(view.Buffer, relatedOff, WatrFormType);
        var nighttime = _fields.ReadPointerToFormId(view.Buffer, relatedOff + 4, WatrFormType);
        var underwater = _fields.ReadPointerToFormId(view.Buffer, relatedOff + 8, WatrFormType);
        if (daytime is null && nighttime is null && underwater is null)
        {
            return null;
        }

        return new Dictionary<string, object?>
        {
            ["Daytime"] = daytime ?? 0u,
            ["Nighttime"] = nighttime ?? 0u,
            ["Underwater"] = underwater ?? 0u
        };
    }
}

using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

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
            visualProperties = SubrecordSchemaView.TryRead("DNAM", "WATR", shaderBytes, bigEndian: true)?.Raw;
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
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}

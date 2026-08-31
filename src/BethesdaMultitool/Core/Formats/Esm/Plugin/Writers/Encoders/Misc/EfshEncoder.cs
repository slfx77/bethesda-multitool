using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes an Effect Shader (EFSH) record. No typed model — it arrives as a
///     <see cref="GenericEsmRecord" />, so every field is read through
///     <see cref="GenericRecordFields" /> with both key forms.
///     <para>
///         Canonical order from xEdit <c>wbRecord(EFSH)</c> (wbDefinitionsFNV.pas):
///         EDID, ICON('Fill Texture'), ICO2('Particle Shader Texture'), NAM7('Holes Texture'), DATA.
///     </para>
///     <para>
///         PDB <c>TESEffectShader</c> (size 384): <c>Data</c> @40 (308-byte
///         <c>EffectShaderData</c>) → DATA, and three 12-byte <c>TESTexture</c> members →
///         <c>TextureShaderTexture</c> @348 → ICON (the membrane/fill shader's texture),
///         <c>ParticleShaderTexture</c> @360 → ICO2 (name-for-name), <c>BlockOutTexture</c> @372 →
///         NAM7 ("block out" is the holes texture).
///     </para>
///     <para>
///         <b>Endianness.</b> DATA is a mixed block of flags, blend-mode enums, packed colours and
///         floats, so it must not be hand-swapped.
///         <see cref="SubrecordSchemaProcessor.ConvertWithSchema" /> is the single BE→LE oracle and
///         already carries EFSH DATA schemas at every size the corpus shows (200/224/244/248/284/
///         300/308); the runtime struct is the 308-byte form. When it declines — no schema for the
///         captured length — the record is emitted without DATA rather than with bytes in the wrong
///         order.
///     </para>
/// </summary>
public sealed class EfshEncoder : IRecordEncoder
{
    /// <summary>Runtime <c>EffectShaderData</c> size; the largest of the seven schema variants.</summary>
    private const int EffectShaderDataSize = 308;

    public string RecordType => "EFSH";

    public Type ModelType => typeof(GenericEsmRecord);

    internal static EncodedRecord EncodeNew(GenericEsmRecord efsh)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(efsh.EditorId))
        {
            warnings.Add($"New EFSH 0x{efsh.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", efsh.EditorId ?? string.Empty));

        AddTexture(subs, efsh, "ICON", "TESEffectShader.TextureShaderTexture");
        AddTexture(subs, efsh, "ICO2", "TESEffectShader.ParticleShaderTexture");
        AddTexture(subs, efsh, "NAM7", "TESEffectShader.BlockOutTexture");

        var data = EncodeData(efsh);
        if (data is null)
        {
            warnings.Add(
                $"EFSH 0x{efsh.FormId:X8} has no convertible EffectShaderData — omitting DATA.");
        }
        else
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("DATA", data));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static void AddTexture(
        List<EncodedSubrecord> subs, GenericEsmRecord efsh, string signature, string runtimeKey)
    {
        if (GenericRecordFields.TryString(efsh, signature, runtimeKey) is { Length: > 0 } path)
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord(signature, path));
        }
    }

    /// <summary>
    ///     Produce PC little-endian DATA bytes from whichever shape the producer stored: the runtime
    ///     reader yields the raw 308 big-endian bytes, while the ESM carve path runs the subrecord
    ///     through the same registered schema and stores the decoded field dictionary instead. Both
    ///     go back through the schema rather than being reinterpreted here.
    /// </summary>
    private static byte[]? EncodeData(GenericEsmRecord efsh)
    {
        if (GenericRecordFields.TryBytes(efsh, EffectShaderDataSize, "DATA", "TESEffectShader.Data") is { } raw)
        {
            return SubrecordSchemaProcessor.ConvertWithSchema("DATA", raw, "EFSH");
        }

        if (efsh.Fields.TryGetValue("DATA", out var value)
            && value is IReadOnlyDictionary<string, object?> { Count: > 0 } decoded)
        {
            var schema = SubrecordSchemaRegistry.GetSchema("DATA", "EFSH", EffectShaderDataSize);
            return schema is null ? null : SchemaDictionarySerializer.Serialize(schema, decoded);
        }

        return null;
    }
}

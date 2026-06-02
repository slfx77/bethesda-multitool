using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Phase 3.2 PWAT encoder coverage via the
///     <see cref="SubrecordEncoderTestBase{TModel}" /> scaffold. Tests only the typed
///     DNAM payload; the other emitted subrecords (EDID, OBND, MODL, MODT) are
///     exercised by their generic encoders' tests.
/// </summary>
public sealed class PwatEncoderTests : SubrecordEncoderTestBase<PwatEncoderTests.PwatModel>
{
    public sealed record PwatModel(uint WaterFormId, uint Flags);

    protected override string RecordSignature => "PWAT";

    protected override IReadOnlyCollection<string> EmittedSubrecordSignatures => ["DNAM"];

    protected override PwatModel MakeSyntheticModel()
    {
        // Water FormID 0x000A1B2C with flags 0x80000001 — high bit + low bit set so a
        // byte-order regression on either word would surface in the expected-bytes check.
        return new PwatModel(WaterFormId: 0x000A1B2Cu, Flags: 0x80000001u);
    }

    protected override byte[] GetExpectedBytes()
    {
        // PWAT DNAM canonical layout (8 bytes, little-endian):
        //   0..3  uint32  WaterFormId
        //   4..7  uint32  Flags
        var expected = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(0, 4), 0x000A1B2Cu);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(4, 4), 0x80000001u);
        return expected;
    }

    protected override byte[] EncodeModel(PwatModel model)
    {
        return PwatEncoder.EncodePwatDnam(model.WaterFormId, model.Flags);
    }

    protected override (bool Parsed, PwatModel? Model) TryParseBytes(byte[] bytes)
    {
        if (bytes.Length != 8)
        {
            return (false, null);
        }

        var formId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        return (true, new PwatModel(formId, flags));
    }
}

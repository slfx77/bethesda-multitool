using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Phase 3.2 TREE encoder coverage via <see cref="SubrecordEncoderTestBase{TModel}" />.
///     Covers CNAM (32 bytes / 8 floats) which is the most byte-swap-risky subrecord. BNAM
///     (2 floats) and SNAM (variable uint32 array) share the same float-LE write path that
///     this test exercises and are smoke-checked by the round-trip alone.
/// </summary>
public sealed class TreeEncoderCnamTests : SubrecordEncoderTestBase<TreeData>
{
    protected override string RecordSignature => "TREE";

    protected override IReadOnlyCollection<string> EmittedSubrecordSignatures => ["CNAM"];

    protected override TreeData MakeSyntheticModel()
    {
        // Distinct float values per slot so a misordered write surfaces in the
        // byte-equality check rather than collapsing into a self-consistent permutation.
        return new TreeData
        {
            LeafCurvature = 1.5f,
            MinLeafAngle = 0.25f,
            MaxLeafAngle = 0.75f,
            BranchDimmingValue = 0.8f,
            LeafDimmingValue = 0.6f,
            ShadowRadius = 12.0f,
            RockSpeed = 0.05f,
            RustleSpeed = 0.125f
        };
    }

    protected override byte[] GetExpectedBytes()
    {
        // TREE CNAM canonical layout (32 bytes, little-endian):
        //   0..3   float  LeafCurvature
        //   4..7   float  MinLeafAngle
        //   8..11  float  MaxLeafAngle
        //   12..15 float  BranchDimmingValue
        //   16..19 float  LeafDimmingValue
        //   20..23 float  ShadowRadius
        //   24..27 float  RockSpeed
        //   28..31 float  RustleSpeed
        var expected = new byte[32];
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(0, 4), 1.5f);
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(4, 4), 0.25f);
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(8, 4), 0.75f);
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(12, 4), 0.8f);
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(16, 4), 0.6f);
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(20, 4), 12.0f);
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(24, 4), 0.05f);
        BinaryPrimitives.WriteSingleLittleEndian(expected.AsSpan(28, 4), 0.125f);
        return expected;
    }

    protected override byte[] EncodeModel(TreeData model)
    {
        return TreeEncoder.EncodeCnamData(model);
    }

    protected override (bool Parsed, TreeData? Model) TryParseBytes(byte[] bytes)
    {
        if (bytes.Length != 32)
        {
            return (false, null);
        }

        return (true, new TreeData
        {
            LeafCurvature = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(0, 4)),
            MinLeafAngle = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(4, 4)),
            MaxLeafAngle = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(8, 4)),
            BranchDimmingValue = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(12, 4)),
            LeafDimmingValue = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(16, 4)),
            ShadowRadius = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(20, 4)),
            RockSpeed = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(24, 4)),
            RustleSpeed = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(28, 4))
        });
    }
}

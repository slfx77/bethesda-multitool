using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     RE-17 (<see cref="NifRotationKeyEngineRule" />) pinned bit for bit on the plan's retail control keys. The raw key
///     bits below were copied from the checked-in cut-1b probe expectations (the independent Python probe's payload bits);
///     the expected outputs were computed from them by a separate binary32 model of the rule (numpy float32, every
///     operation rounded to binary32 in the documented order), not by this code.
/// </summary>
/// <remarks>
///     Each pin carries a control that shows it discriminates: a pairwise (not chained) sign test, double-precision
///     normalization, dividing by the length instead of multiplying by its reciprocal, an unclamped W on either side of
///     [-1, 1], and a W clamp run before the chain flip each give different bits on the same keys. TBC and CONST groups
///     are pinned on the same retail keys through their own key layouts. The summation order of the squared length (x, w, y, z against w, x, y, z) changes no output bit on any
///     LINEAR or CONST rotation key of the cut-1b manifest (measured with the same binary32 model), so no pin here can
///     discriminate it.
/// </remarks>
public sealed class NifRotationKeyEngineRuleTests
{
    /// <summary>
    ///     <c>meshes/creatures/protectron/h2hrecoil.kf</c> (SHA-256 303762d4...0560), block 15, keys 27 to 50: time, then
    ///     w, x, y, z as stored. Key 27 to 28 is the manifest's dot -0.99998 pair.
    /// </summary>
    private static readonly uint[][] H2hRecoilKeys =
    [
        [0x3F666667, 0xBEFEFC53, 0x3EF5690D, 0x3F0A8903, 0x3EF537EA],
        [0x3F6EEEEF, 0x3F003A59, 0xBEF49993, 0xBF096ACA, 0xBEF7013E],
        [0x3F777778, 0x3F00ECA8, 0xBEF3D711, 0xBF08510B, 0xBEF8BD35],
        [0x3F800000, 0x3F0192B9, 0xBEF32698, 0xBF073FDF, 0xBEFA63DE],
        [0x3F844444, 0x3F022A40, 0xBEF28D60, 0xBF063B74, 0xBEFBED71],
        [0x3F888889, 0x3F02B101, 0xBEF210BF, 0xBF054805, 0xBEFD5243],
        [0x3F8CCCCD, 0x3F0324CD, 0xBEF1B61A, 0xBF0469D8, 0xBEFE8AC0],
        [0x3F911111, 0x3F03837E, 0xBEF182DE, 0xBF03A53A, 0xBEFF8F5B],
        [0x3F955556, 0x3F03CAEC, 0xBEF17C83, 0xBF02FE7C, 0xBF002C4D],
        [0x3F99999A, 0x3F03F639, 0xBEF1AC5E, 0xBF0274D7, 0xBF007580],
        [0x3F9DDDDE, 0x3F040432, 0xBEF21425, 0xBF0202FC, 0xBF00A9AE],
        [0x3FA22222, 0x3F03F825, 0xBEF2ADB4, 0xBF01A652, 0xBF00CB35],
        [0x3FA66667, 0x3F03D54F, 0xBEF372D1, 0xBF015C32, 0xBF00DC6A],
        [0x3FAAAAAB, 0x3F039EE7, 0xBEF45D2B, 0xBF0121ED, 0xBF00DF96],
        [0x3FAEEEEF, 0x3F03581F, 0xBEF5666A, 0xBF00F4CA, 0xBF00D6FD],
        [0x3FB33333, 0x3F030425, 0xBEF68838, 0xBF00D215, 0xBF00C4E4],
        [0x3FB77778, 0x3F02A62B, 0xBEF7BC3E, 0xBF00B718, 0xBF00AB91],
        [0x3FBBBBBC, 0x3F024168, 0xBEF8FC29, 0xBF00A120, 0xBF008D4B],
        [0x3FC00000, 0x3F01D916, 0xBEFA41B9, 0xBF008D82, 0xBF006C65],
        [0x3FC44445, 0x3F017077, 0xBEFB86B5, 0xBF007999, 0xBF004B32],
        [0x3FC88889, 0x3F010AD3, 0xBEFCC4F2, 0xBF0062C5, 0xBF002C0F],
        [0x3FCCCCCD, 0x3F00AB7A, 0xBEFDF650, 0xBF00466D, 0xBF001160],
        [0x3FD11111, 0x3F0055C1, 0xBEFF14BD, 0xBF0021FE, 0xBEFFFB18],
        [0x3FD55556, 0x3F000D00, 0xBF000D16, 0xBEFFE5D0, 0xBEFFE5FF]
    ];

    /// <summary>The rule's output for <see cref="H2hRecoilKeys" />: x, y, z, w per key.</summary>
    private static readonly uint[][] H2hRecoilExpected =
    [
        [0x3EF5690D, 0x3F0A8903, 0x3EF537EA, 0xBEFEFC53],
        [0x3EF49993, 0x3F096ACA, 0x3EF7013E, 0xBF003A59],
        [0x3EF3D711, 0x3F08510B, 0x3EF8BD35, 0xBF00ECA8],
        [0x3EF3269A, 0x3F073FE0, 0x3EFA63E0, 0xBF0192BA],
        [0x3EF28D62, 0x3F063B75, 0x3EFBED73, 0xBF022A41],
        [0x3EF210BF, 0x3F054805, 0x3EFD5243, 0xBF02B101],
        [0x3EF1B61A, 0x3F0469D8, 0x3EFE8AC0, 0xBF0324CD],
        [0x3EF182E0, 0x3F03A53B, 0x3EFF8F5D, 0xBF03837F],
        [0x3EF17C83, 0x3F02FE7C, 0x3F002C4D, 0xBF03CAEC],
        [0x3EF1AC5E, 0x3F0274D7, 0x3F007580, 0xBF03F639],
        [0x3EF21425, 0x3F0202FC, 0x3F00A9AE, 0xBF040432],
        [0x3EF2ADB6, 0x3F01A653, 0x3F00CB36, 0xBF03F826],
        [0x3EF372D3, 0x3F015C33, 0x3F00DC6B, 0xBF03D550],
        [0x3EF45D2B, 0x3F0121ED, 0x3F00DF96, 0xBF039EE7],
        [0x3EF5666A, 0x3F00F4CA, 0x3F00D6FD, 0xBF03581F],
        [0x3EF68838, 0x3F00D215, 0x3F00C4E4, 0xBF030425],
        [0x3EF7BC3E, 0x3F00B718, 0x3F00AB91, 0xBF02A62B],
        [0x3EF8FC2B, 0x3F00A121, 0x3F008D4C, 0xBF024169],
        [0x3EFA41BB, 0x3F008D83, 0x3F006C66, 0xBF01D917],
        [0x3EFB86B5, 0x3F007999, 0x3F004B32, 0xBF017077],
        [0x3EFCC4F2, 0x3F0062C5, 0x3F002C0F, 0xBF010AD3],
        [0x3EFDF650, 0x3F00466D, 0x3F001160, 0xBF00AB7A],
        [0x3EFF14BD, 0x3F0021FE, 0x3EFFFB18, 0xBF0055C1],
        [0x3F000D16, 0x3EFFE5D0, 0x3EFFE5FF, 0xBF000D00]
    ];

    /// <summary>
    ///     <c>meshes/creatures/nvmantis/idleanims/specialidle_hitarmleft.kf</c> (SHA-256 6361891d...f2df), block 20, keys
    ///     0 to 3 (time, w, x, y, z). Keys 0 and 1 are the manifest's moderate dot -0.869 pair.
    /// </summary>
    private static readonly uint[][] HitArmLeftKeys =
    [
        [0x00000000, 0xBE960CDC, 0xBCC8315B, 0x3F74A895, 0x3C4D5FFB],
        [0x3D088889, 0x3F19ABE8, 0xBE73D5D5, 0xBF3BE6D5, 0x3E575586],
        [0x3D888889, 0x3F4CC30F, 0xBDC40CCE, 0xBF178E4B, 0x3CC7609D],
        [0x3DCCCCCD, 0x3F1CADC9, 0xBE46ABB5, 0xBF441C47, 0xBCFB0D2D]
    ];

    /// <summary>The rule's output for <see cref="HitArmLeftKeys" />: x, y, z, w per key.</summary>
    private static readonly uint[][] HitArmLeftExpected =
    [
        [0xBCC8315D, 0x3F74A897, 0x3C4D5FFD, 0xBE960CDD],
        [0x3E73D5D5, 0x3F3BE6D5, 0xBE575586, 0xBF19ABE8],
        [0x3DC40CCE, 0x3F178E4B, 0xBCC7609D, 0xBF4CC30F],
        [0x3E46ABB5, 0x3F441C47, 0x3CFB0D2D, 0xBF1CADC9]
    ];

    /// <summary>
    ///     <c>meshes/characters/_male/2hmholster.kf</c> (SHA-256 b11f6ea9...c4e9), block 2 (time, w, x, y, z). Key 0 fails
    ///     Shared's Float32 unit check as stored.
    /// </summary>
    private static readonly uint[][] TwoHmHolsterKeys =
    [
        [0x00000000, 0x3E5631E3, 0x3F04BCDA, 0xBF2BBE2C, 0x3EF90BFC],
        [0x3DCCCCC0, 0x3E564493, 0x3F04C8CB, 0xBF2BCE76, 0x3EF9234F]
    ];

    /// <summary>The rule's output for <see cref="TwoHmHolsterKeys" />: x, y, z, w per key.</summary>
    private static readonly uint[][] TwoHmHolsterExpected =
    [
        [0x3F04C930, 0xBF2BCE22, 0x3EF92321, 0x3E5645CA],
        [0x3F04C8CC, 0xBF2BCE77, 0x3EF92351, 0x3E564495]
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void H2hRecoilBlock15_ChainNegatesTwentyThreeKeysAndPinsEveryOutputBit(bool bigEndian)
    {
        var view = RotationGroup(H2hRecoilKeys, bigEndian, 1);

        Assert.True(NifRotationKeyEngineRule.TryApply(view, out var result));

        Assert.Equal(Flatten(H2hRecoilExpected), OutputBits(result.Values));
        Assert.Equal(23, result.NegatedCount);
        Assert.False(result.ChainNegated[0]);
        Assert.All(result.ChainNegated.Skip(1), static negated => Assert.True(negated));

        // Control: a pairwise reading (flip key i+1 when the RAW dot with key i is negative) flips one key, not 23.
        Assert.Equal(1, PairwiseNegativeDots(H2hRecoilKeys));
        AssertUnitAndSignAligned(result.Values);
        AssertDoublePrecisionDiffers(H2hRecoilKeys, result);
    }

    [Fact]
    public void HitArmLeftBlock20_ChainNegatesKeysOneToThree()
    {
        var result = ApplyToRawKeys(HitArmLeftKeys);

        Assert.Equal(Flatten(HitArmLeftExpected), OutputBits(result.Values));
        Assert.Equal(new[] { false, true, true, true }, result.ChainNegated);
        Assert.Equal(1, PairwiseNegativeDots(HitArmLeftKeys));
        AssertUnitAndSignAligned(result.Values);
        AssertDoublePrecisionDiffers(HitArmLeftKeys, result);
    }

    [Fact]
    public void TwoHmHolsterBlock2_NormalizesTheNonUnitKey()
    {
        var raw = new Quaternion(
            FromBits(TwoHmHolsterKeys[0][2]),
            FromBits(TwoHmHolsterKeys[0][3]),
            FromBits(TwoHmHolsterKeys[0][4]),
            FromBits(TwoHmHolsterKeys[0][1]));
        Assert.False(IsUnitRotation(raw), "The stored key must fail Shared's unit check, or the pin shows nothing.");

        var result = ApplyToRawKeys(TwoHmHolsterKeys);

        Assert.Equal(Flatten(TwoHmHolsterExpected), OutputBits(result.Values));
        Assert.Equal(0, result.NegatedCount);
        AssertUnitAndSignAligned(result.Values);
        AssertDoublePrecisionDiffers(TwoHmHolsterKeys, result);
    }

    [Fact]
    public void Normalize_MultipliesByTheReciprocalOfTheLength_NotDividesByIt()
    {
        // meshes/characters/_male/idleanims/siidle.kf (SHA-256 83eed415...1c91), block 13, key 0, as one key.
        const uint w = 0x3F2EA77B;
        const uint x = 0xBB4804C7;
        const uint y = 0x3B88D2CD;
        const uint z = 0x3F3B094E;

        var value = NifRotationKeyEngineRule.NormalizeExact(FromBits(w), FromBits(x), FromBits(y), FromBits(z));

        Assert.Equal(new uint[] { 0xBB48175D, 0x3B88DF84, 0x3F3B1AAF, 0x3F2EB7B6 }, OutputBits([value]));

        // Control: dividing each component by the length rounds z one unit higher.
        var squared = FromBits(x) * FromBits(x) + FromBits(w) * FromBits(w);
        squared += FromBits(y) * FromBits(y);
        squared += FromBits(z) * FromBits(z);
        var length = MathF.Sqrt(squared);
        Assert.Equal(0x3F3B1AB0u, BitConverter.SingleToUInt32Bits(FromBits(z) / length));
    }

    [Fact]
    public void ChainAlignment_NeverFlipsOnAZeroOrNaNDot()
    {
        // Every product is -0, so the dot is -0: not below zero, so key 1 keeps its stored sign.
        var negativeZeroDot = NifRotationKeyEngineRule.Apply(
            [1f, -0f], [-0f, 1f], [-0f, 0f], [-0f, 0f]);
        Assert.False(negativeZeroDot.ChainNegated[1]);
        Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(negativeZeroDot.Values[1].W));
        Assert.Equal(1f, negativeZeroDot.Values[1].X);

        var positiveZeroDot = NifRotationKeyEngineRule.Apply([1f, 0f], [0f, 1f], [0f, 0f], [0f, 0f]);
        Assert.False(positiveZeroDot.ChainNegated[1]);

        var nanDot = NifRotationKeyEngineRule.Apply([float.NaN, -1f], [0f, 0f], [0f, 0f], [0f, 0f]);
        Assert.False(nanDot.ChainNegated[1]);
        Assert.Equal(-1f, nanDot.Values[1].W);

        // Control: an opposite key does flip.
        var opposite = NifRotationKeyEngineRule.Apply([1f, -1f], [0f, 0f], [0f, 0f], [0f, 0f]);
        Assert.True(opposite.ChainNegated[1]);
        Assert.Equal(1f, opposite.Values[1].W);
    }

    [Fact]
    public void WClamp_RunsBeforeTheNormalize()
    {
        var clamped = NifRotationKeyEngineRule.Apply([1.5f], [0.5f], [0f], [0f]);

        Assert.Equal(new uint[] { 0x3EE4F92E, 0, 0, 0x3F64F92E }, OutputBits(clamped.Values));

        // Control: normalizing the unclamped key gives other bits (x 0x3EA1E89B, w 0x3F72DCE8).
        var unclamped = NifRotationKeyEngineRule.NormalizeExact(1.5f, 0.5f, 0f, 0f);
        Assert.Equal(new uint[] { 0x3EA1E89B, 0, 0, 0x3F72DCE8 }, OutputBits([unclamped]));
    }

    [Fact]
    public void WClamp_AlsoClampsBelowMinusOne()
    {
        var clamped = NifRotationKeyEngineRule.Apply([-1.5f], [0.5f], [0f], [0f]);

        Assert.Equal(new uint[] { 0x3EE4F92E, 0, 0, 0xBF64F92E }, OutputBits(clamped.Values));

        // Control: normalizing the unclamped key gives other bits (x 0x3EA1E89B, w 0xBF72DCE8).
        var unclamped = NifRotationKeyEngineRule.NormalizeExact(-1.5f, 0.5f, 0f, 0f);
        Assert.Equal(new uint[] { 0x3EA1E89B, 0, 0, 0xBF72DCE8 }, OutputBits([unclamped]));
    }

    [Fact]
    public void WClamp_RunsAfterTheChainFlip_SoTheFlipSeesTheStoredW()
    {
        // Key 0 stores w = 3. The engine's dot with key 1 uses that stored w, -0.2*1 + 3*0.1 = 0.1, so key 1 keeps its
        // sign. Clamping first would make the dot -0.2*1 + 1*0.1 = -0.1 and negate key 1 (x 0x3F64F92D, w 0xBEE4F92D).
        var result = NifRotationKeyEngineRule.Apply([3f, 0.1f], [1f, -0.2f], [0f, 0f], [0f, 0f]);

        Assert.False(result.ChainNegated[1]);
        Assert.Equal(new uint[] { 0x3F3504F3, 0, 0, 0x3F3504F3, 0xBF64F92D, 0, 0, 0x3EE4F92D },
            OutputBits(result.Values));

        // Control, the premise in the engine's own dot: the stored w keeps it positive, a clamped w makes it negative.
        Assert.True(NifRotationKeyEngineRule.ChainDot(3f, 1f, 0f, 0f, 0.1f, -0.2f, 0f, 0f) > 0f);
        Assert.True(NifRotationKeyEngineRule.ChainDot(1f, 1f, 0f, 0f, 0.1f, -0.2f, 0f, 0f) < 0f);
    }

    [Theory]
    [InlineData(3u, false)]
    [InlineData(3u, true)]
    [InlineData(5u, false)]
    [InlineData(5u, true)]
    public void TbcAndConstGroups_GiveTheLinearBitsFromTheirOwnKeyLayout(uint keyType, bool bigEndian)
    {
        // A TBC key is 32 bytes (its value, then three TBC floats) and a CONST key 20, like LINEAR. The TBC floats here are
        // non-zero, so reading a value from the wrong offset or stepping by the wrong stride changes the output bits.
        // TBC's value at a key stays provisional until SA6 (see NifRotationKeyEngineRule); this pins the rule's steps.
        var view = RotationGroup(HitArmLeftKeys, bigEndian, keyType, [0x3F000000, 0xBF000000, 0x3E800000]);

        Assert.True(NifRotationKeyEngineRule.TryApply(view, out var result));

        Assert.Equal(Flatten(HitArmLeftExpected), OutputBits(result.Values));
        Assert.Equal(new[] { false, true, true, true }, result.ChainNegated);
    }

    [Theory]
    [InlineData(1u, true)]
    [InlineData(2u, false)]
    [InlineData(3u, true)]
    [InlineData(5u, true)]
    public void TryApply_GovernsLinearTbcAndConstQuaternionGroupsOnly(uint keyType, bool governed)
    {
        var view = RotationGroup(HitArmLeftKeys, false, keyType);

        Assert.Equal(governed, NifRotationKeyEngineRule.TryApply(view, out var result));
        Assert.Equal(governed, result is not null);
    }

    [Fact]
    public void TryApply_RefusesEulerAxesAndEmptyGroups()
    {
        var floatGroup = new NifAnimationByteWriter(false).U32(1).U32(1).F32(0f).F32(1f).ToArray();
        var pos = 0;
        Assert.True(NifKeyGroupReader.TryReadGroupView(
            floatGroup, ref pos, floatGroup.Length, false, NifKeyValueLayout.Float, out var axis));
        Assert.False(NifRotationKeyEngineRule.TryApply(axis, out _));

        var empty = new NifAnimationByteWriter(false).U32(0).ToArray();
        pos = 0;
        Assert.True(NifKeyGroupReader.TryReadRotationView(
            empty, ref pos, empty.Length, false, 0x14020007, out var none));
        Assert.False(NifRotationKeyEngineRule.TryApply(none.Keys, out _));
    }

    /// <summary>
    ///     A quaternion key group written in the given byte order and read back through the view; a TBC key carries
    ///     <paramref name="tbcWords" /> (zeros when omitted) after its value.
    /// </summary>
    private static NifKeyGroupView RotationGroup(uint[][] keys, bool bigEndian, uint keyType, uint[]? tbcWords = null)
    {
        var writer = new NifAnimationByteWriter(bigEndian).U32((uint)keys.Length).U32(keyType);
        foreach (var key in keys)
        {
            writer.Words(key);
            if (keyType == 3)
            {
                writer.Words(tbcWords ?? new uint[3]);
            }
        }

        var data = writer.ToArray();
        var pos = 0;
        Assert.True(NifKeyGroupReader.TryReadRotationView(data, ref pos, data.Length, bigEndian, 0x14020007,
            out var view));
        Assert.Equal(data.Length, pos);
        return view.Keys;
    }

    private static NifEngineRotationKeys ApplyToRawKeys(uint[][] keys)
    {
        return NifRotationKeyEngineRule.Apply(
            keys.Select(static key => FromBits(key[1])).ToArray(),
            keys.Select(static key => FromBits(key[2])).ToArray(),
            keys.Select(static key => FromBits(key[3])).ToArray(),
            keys.Select(static key => FromBits(key[4])).ToArray());
    }

    /// <summary>The number of adjacent RAW pairs whose engine-order dot is negative (the pairwise reading's flips).</summary>
    private static int PairwiseNegativeDots(uint[][] keys)
    {
        var count = 0;
        for (var index = 0; index + 1 < keys.Length; index++)
        {
            var a = keys[index];
            var b = keys[index + 1];
            var dot = NifRotationKeyEngineRule.ChainDot(
                FromBits(a[1]), FromBits(a[2]), FromBits(a[3]), FromBits(a[4]),
                FromBits(b[1]), FromBits(b[2]), FromBits(b[3]), FromBits(b[4]));
            if (dot < 0f)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    ///     After the rule every key passes Shared's Float32 unit check and every adjacent dot is non-negative, both in the
    ///     sequential X, Y, Z, W order and in the pairwise order a vectorized dot uses.
    /// </summary>
    private static void AssertUnitAndSignAligned(Quaternion[] values)
    {
        Assert.All(values, static value => Assert.True(IsUnitRotation(value)));
        for (var index = 0; index + 1 < values.Length; index++)
        {
            var a = values[index];
            var b = values[index + 1];
            var sequential = a.X * b.X + a.Y * b.Y;
            sequential += a.Z * b.Z;
            sequential += a.W * b.W;
            var pairwise = (a.X * b.X + a.Y * b.Y) + (a.Z * b.Z + a.W * b.W);
            Assert.True(sequential >= 0f && pairwise >= 0f, $"keys {index} and {index + 1}: dot {sequential}");
        }
    }

    /// <summary>Control: normalizing the chained keys in double precision changes at least one output bit.</summary>
    private static void AssertDoublePrecisionDiffers(uint[][] keys, NifEngineRotationKeys result)
    {
        var differs = false;
        for (var index = 0; index < keys.Length; index++)
        {
            var sign = result.ChainNegated[index] ? -1.0 : 1.0;
            double w = sign * FromBits(keys[index][1]);
            double x = sign * FromBits(keys[index][2]);
            double y = sign * FromBits(keys[index][3]);
            double z = sign * FromBits(keys[index][4]);
            var length = Math.Sqrt(x * x + w * w + y * y + z * z);
            var candidate = new Quaternion((float)(x / length), (float)(y / length), (float)(z / length),
                (float)(w / length));
            differs |= !OutputBits([candidate]).SequenceEqual(OutputBits([result.Values[index]]));
        }

        Assert.True(differs, "Double-precision normalization matched every pinned bit, so the pin cannot tell them apart.");
    }

    /// <summary>Shared's Float32 unit check (SceneAnimationValidation: |LengthSquared - 1| &lt;= 1e-4 in Float32).</summary>
    private static bool IsUnitRotation(Quaternion value)
    {
        var squared = value.X * value.X + value.Y * value.Y;
        squared += value.Z * value.Z;
        squared += value.W * value.W;
        return MathF.Abs(squared - 1f) <= 0.0001f;
    }

    private static uint[] OutputBits(Quaternion[] values)
    {
        return values
            .SelectMany(static value => new[] { value.X, value.Y, value.Z, value.W })
            .Select(BitConverter.SingleToUInt32Bits)
            .ToArray();
    }

    private static uint[] Flatten(uint[][] rows)
    {
        return rows.SelectMany(static row => row).ToArray();
    }

    private static float FromBits(uint bits)
    {
        return BitConverter.UInt32BitsToSingle(bits);
    }
}

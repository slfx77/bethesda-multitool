using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     The renderer-safety proof for the key-group refactor: <see cref="NifKeyGroupReader" />'s renderer methods are now
///     projections of the lossless view, and on every generated group (but the multi-record Euler blocks the remarks
///     describe) they must return EXACTLY what the pre-view reader
///     (<see cref="NifKeyGroupReaderLegacyReference" />, a verbatim copy) returned: the same success, the same
///     interpolation label, the same keys bit for bit (time, value, tangents and the tangent flag; NaN payloads and signed
///     zeros compared as bits), the same Euler keys, and on success the same cursor
///     (<see cref="NifKeyGroupProjectionSignatures" />).
/// </summary>
/// <remarks>
///     <para>
///         The groups are generated, not random bytes, so every path is reached: empty groups, every stored key type
///         including the unknown ones, counts past the cap, Euler blocks with and without the legacy word and with one or
///         several records, special float patterns (infinities, NaNs, the 1e30 renderer threshold, the FLT_MAX
///         sentinels), truncated buffers and a caller's end short of the buffer. Each group runs through the
///         NiTransformData chain (rotation, translation, scale) and through each single-group reader from the same start.
///         The seeds are fixed; the tallies at the end prove every outcome occurred, so a generator that stopped reaching a
///         path fails instead of passing vacuously.
///     </para>
///     <para>
///         An Euler block that stores more than one record is where the projection deliberately stopped matching the
///         pre-view reader (RE-20: the engine reads every record, nif.xml one). Those blocks are compared with the
///         engine's walk composed from the pre-view reader's float-group reader
///         (<see cref="NifKeyGroupProjectionSignatures" />), and the tallies require that comparison to have run and to
///         have succeeded. The Quadratic Vector3 value gate is tallied only where it alone refused the group (the lossless
///         view reads the same bytes), so a truncated Quadratic group cannot satisfy that tally.
///     </para>
/// </remarks>
public sealed class NifKeyGroupReaderProjectionEquivalenceTests
{
    private const int Iterations = 4000;

    /// <summary>RE-20: an NiEulerRotKey record stores the leading legacy word below stream version 10.1.0.104.</summary>
    private const uint EngineLegacyWordEnd = 0x0A010068;

    private static readonly uint[] SpecialWords =
    [
        0x00000000, 0x80000000, 0x3F800000, 0xBF800000, 0x7F800000, 0xFF800000, 0x7FC00000, 0x7FC00001,
        0xFFC00000, 0x7F800001, 0x7F7FFFFF, 0xFF7FFFFF, 0x7149F2CA, 0x7149F2C9, 0xF149F2CA, 0x00000001
    ];

    private static readonly uint[] KeyTypeChoices = [0, 1, 2, 3, 4, 5, 6, 0xFFFFFFFF];

    private static readonly uint[] AxisKeyTypes = [1, 2, 3, 5];

    private static readonly uint[] HugeCounts = [(1u << 20) - 1, 1u << 20, (1u << 20) + 1, 0xFFFFFFFF];

    [Theory]
    [InlineData(false, NifVersions.Gamebryo202007)]
    [InlineData(true, NifVersions.Gamebryo202007)]
    [InlineData(false, NifVersions.NetImmerse4002)]
    [InlineData(true, NifVersions.NetImmerse4002)]
    public void GeneratedKeyGroups_ProjectExactlyWhatTheLegacyReaderReturned(bool bigEndian, uint binaryVersion)
    {
        var random = new Random(unchecked((int)binaryVersion) ^ (bigEndian ? 0x5EED : 0x0BAD));
        var tally = new Tally();
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var writer = new NifAnimationByteWriter(bigEndian);
            WriteGroup(writer, random, 4, true, binaryVersion);
            WriteGroup(writer, random, 3, false, binaryVersion);
            WriteGroup(writer, random, 1, false, binaryVersion);
            var full = writer.ToArray();
            var length = random.Next(4) == 0 ? random.Next(full.Length + 1) : full.Length;
            var data = full.AsSpan(0, length).ToArray();
            var end = random.Next(8) == 0 ? random.Next(data.Length + 1) : data.Length;
            var context = $"iteration {iteration}, end {end}, bytes {Convert.ToHexString(data)}";

            var rotation = Check(NifKeyGroupProjectionSignatures.Quat(data, 0, end, bigEndian, binaryVersion),
                context);
            tally.CountQuat(rotation);
            if (rotation.Read)
            {
                var translation = Check(
                    NifKeyGroupProjectionSignatures.Vector3(data, rotation.Position, end, bigEndian), context);
                tally.CountVector3(translation, ValueGateAlone(translation, data, rotation.Position, end, bigEndian));
                if (translation.Read)
                {
                    tally.CountFloat(Check(
                        NifKeyGroupProjectionSignatures.Float(data, translation.Position, end, bigEndian), context));
                }
            }

            var single = Check(NifKeyGroupProjectionSignatures.Vector3(data, 0, end, bigEndian), context);
            tally.CountVector3(single, ValueGateAlone(single, data, 0, end, bigEndian));
            tally.CountFloat(Check(NifKeyGroupProjectionSignatures.Float(data, 0, end, bigEndian), context));
        }

        // The generator must have reached both outcomes of every reader, and the Euler and tangent paths.
        Assert.True(tally.QuatSuccesses > 200 && tally.QuatFailures > 200, tally.ToString());
        Assert.True(tally.Vector3Successes > 200 && tally.Vector3Failures > 200, tally.ToString());
        Assert.True(tally.FloatSuccesses > 200 && tally.FloatFailures > 200, tally.ToString());
        Assert.True(tally.EulerSuccesses > 10, tally.ToString());
        Assert.True(tally.EngineEulerComparisons > 50 && tally.EngineEulerSuccesses > 10, tally.ToString());
        Assert.True(tally.QuadraticVector3Successes > 10 && tally.ValueGatedVector3Rejections > 10, tally.ToString());
    }

    /// <summary>
    ///     True when a failed Quadratic Vector3 read was refused by the renderer's value gate alone: the lossless view, which
    ///     applies no value gate, reads the same group from the same start.
    /// </summary>
    private static bool ValueGateAlone(
        NifKeyGroupProjectionComparison comparison, byte[] data, int start, int end, bool bigEndian)
    {
        if (comparison.Read || comparison.Interpolation != NifKeyInterpolation.Quadratic)
        {
            return false;
        }

        var pos = start;
        return NifKeyGroupReader.TryReadGroupView(data, ref pos, end, bigEndian, NifKeyValueLayout.Vector3, out _);
    }

    private static NifKeyGroupProjectionComparison Check(NifKeyGroupProjectionComparison comparison, string context)
    {
        Assert.True(comparison.Matches, $"{context}\nlegacy {comparison.Legacy}\nview   {comparison.View}");
        return comparison;
    }

    /// <summary>
    ///     Writes one generated group: a count (often small, sometimes 0 or past the cap), a key type drawn from every
    ///     stored value including unknown ones, and for a known type exactly the key words it needs (so most groups fit
    ///     and the truncation step decides). A rotation group of type 4 gets as many NiEulerRotKey records as a small count
    ///     says (one for a huge count, so the engine's walk runs out of bytes), each the legacy word below 10.1.0.104 and
    ///     three generated float axis groups.
    /// </summary>
    private static void WriteGroup(
        NifAnimationByteWriter writer, Random random, int valueWords, bool rotation, uint binaryVersion)
    {
        var roll = random.Next(10);
        var count = roll switch
        {
            < 2 => 0u,
            9 => HugeCounts[random.Next(HugeCounts.Length)],
            _ => (uint)random.Next(1, 5)
        };
        writer.U32(count);
        if (count == 0)
        {
            return;
        }

        var keyType = KeyTypeChoices[random.Next(KeyTypeChoices.Length)];
        writer.U32(keyType);
        if (rotation && keyType == 4)
        {
            var records = count <= 4 ? count : 1u;
            for (var record = 0u; record < records; record++)
            {
                if (binaryVersion < EngineLegacyWordEnd)
                {
                    writer.U32(RandomWord(random));
                }

                for (var axis = 0; axis < 3; axis++)
                {
                    WriteAxisGroup(writer, random);
                }
            }

            return;
        }

        var wordsPerKey = keyType switch
        {
            1 or 5 => 1 + valueWords,
            2 => rotation ? 1 + valueWords : 1 + 3 * valueWords,
            3 => 1 + valueWords + 3,
            _ => random.Next(0, 6)
        };

        // A clean group carries only small finite floats, so value-gated keys (Quadratic Vector3) also succeed.
        var clean = random.Next(10) < 4;
        var keysWritten = (int)Math.Min(count, 6u);
        for (var word = 0; word < keysWritten * wordsPerKey; word++)
        {
            writer.U32(clean ? SmallFloat(random) : RandomWord(random));
        }
    }

    /// <summary>
    ///     Writes one Euler axis group: 0 to 3 keys, usually of a type an axis accepts (1, 2, 3, 5) and one time in ten of
    ///     any stored type including the unknown ones, so records of several axes mostly read and the refusals still occur.
    /// </summary>
    private static void WriteAxisGroup(NifAnimationByteWriter writer, Random random)
    {
        var count = random.Next(4);
        writer.U32((uint)count);
        if (count == 0)
        {
            return;
        }

        var keyType = random.Next(10) == 0
            ? KeyTypeChoices[random.Next(KeyTypeChoices.Length)]
            : AxisKeyTypes[random.Next(AxisKeyTypes.Length)];
        writer.U32(keyType);
        var wordsPerKey = keyType switch
        {
            1 or 5 => 2,
            2 => 4,
            3 => 5,
            _ => random.Next(0, 6)
        };
        for (var word = 0; word < count * wordsPerKey; word++)
        {
            writer.U32(RandomWord(random));
        }
    }

    private static uint SmallFloat(Random random)
    {
        return BitConverter.SingleToUInt32Bits((float)(random.NextDouble() * 4.0 - 2.0));
    }

    private static uint RandomWord(Random random)
    {
        return random.Next(4) switch
        {
            0 or 1 => SpecialWords[random.Next(SpecialWords.Length)],
            2 => SmallFloat(random),
            _ => unchecked((uint)random.Next()) ^ (random.Next(2) == 0 ? 0u : 0x80000000u)
        };
    }

    private sealed class Tally
    {
        public int QuatSuccesses { get; private set; }

        public int QuatFailures { get; private set; }

        public int EulerSuccesses { get; private set; }

        public int EngineEulerComparisons { get; private set; }

        public int EngineEulerSuccesses { get; private set; }

        public int Vector3Successes { get; private set; }

        public int Vector3Failures { get; private set; }

        public int QuadraticVector3Successes { get; private set; }

        public int ValueGatedVector3Rejections { get; private set; }

        public int FloatSuccesses { get; private set; }

        public int FloatFailures { get; private set; }

        public void CountQuat(NifKeyGroupProjectionComparison comparison)
        {
            if (comparison.EngineEulerReference)
            {
                EngineEulerComparisons++;
            }

            if (!comparison.Read)
            {
                QuatFailures++;
                return;
            }

            QuatSuccesses++;
            if (comparison.Euler)
            {
                EulerSuccesses++;
                if (comparison.EngineEulerReference)
                {
                    EngineEulerSuccesses++;
                }
            }
        }

        public void CountVector3(NifKeyGroupProjectionComparison comparison, bool valueGateAlone)
        {
            var quadratic = comparison.Interpolation == NifKeyInterpolation.Quadratic;
            if (!comparison.Read)
            {
                Vector3Failures++;
                if (valueGateAlone)
                {
                    ValueGatedVector3Rejections++;
                }

                return;
            }

            Vector3Successes++;
            if (quadratic && comparison.KeyCount > 0)
            {
                QuadraticVector3Successes++;
            }
        }

        public void CountFloat(NifKeyGroupProjectionComparison comparison)
        {
            if (comparison.Read)
            {
                FloatSuccesses++;
            }
            else
            {
                FloatFailures++;
            }
        }

        public override string ToString()
        {
            return FormattableString.Invariant(
                $"quat {QuatSuccesses}/{QuatFailures}, euler {EulerSuccesses}, engine euler {EngineEulerSuccesses}/{EngineEulerComparisons}, vector3 {Vector3Successes}/{Vector3Failures}, quadratic vector3 {QuadraticVector3Successes}, value-gated {ValueGatedVector3Rejections}, float {FloatSuccesses}/{FloatFailures}");
        }
    }
}

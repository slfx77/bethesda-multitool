using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BethesdaMultitool.Core.Actors;

/// <summary>Produces versioned preview draws independently of runtime-specific Random implementations.</summary>
internal sealed class ActorInventoryRandom
{
    private readonly uint _seed;
    private ulong _counter;

    /// <summary>Starts a counter at zero with the complete unsigned 32-bit seed.</summary>
    /// <param name="seed">The seed retained in the generation report.</param>
    internal ActorInventoryRandom(uint seed) => _seed = seed;

    /// <summary>Returns a uniform bounded draw by rejecting incomplete residue groups.</summary>
    /// <param name="exclusiveMaximum">Positive upper bound, excluded from the result.</param>
    /// <returns>An integer from zero through the upper bound minus one.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The upper bound is not positive.</exception>
    /// <exception cref="InvalidDataException">The bounded rejection budget is exhausted.</exception>
    internal int Next(int exclusiveMaximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMaximum);
        var bound = (uint)exclusiveMaximum;
        var threshold = unchecked(0u - bound) % bound;
        for (var attempt = 0; attempt < 256; attempt++)
        {
            var value = NextUInt32();
            if (value >= threshold) return (int)(value % bound);
        }
        throw new InvalidDataException("The preview random rejection budget was exhausted.");
    }

    /// <summary>Tests a finite percentage without drawing at the certain endpoints zero and one hundred.</summary>
    /// <param name="percentage">A validated chance from zero through one hundred.</param>
    /// <returns>Whether this preview draw selects the chance-none outcome.</returns>
    internal bool ChanceNone(float percentage) => percentage >= 100 ||
        (percentage > 0 && NextUInt32() / 4294967296.0 < percentage / 100.0);

    /// <summary>Hashes little-endian seed/counter bytes and reads the first little-endian output word.</summary>
    /// <returns>The next reproducible 32-bit draw; the counter advances exactly once.</returns>
    private uint NextUInt32()
    {
        Span<byte> input = stackalloc byte[12];
        Span<byte> hash = stackalloc byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(input, _seed);
        BinaryPrimitives.WriteUInt64LittleEndian(input[4..], _counter++);
        SHA256.HashData(input, hash);
        return BinaryPrimitives.ReadUInt32LittleEndian(hash);
    }
}

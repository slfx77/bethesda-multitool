namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime;

/// <summary>
///     Abstraction over memory-mapped file access for reading binary data.
///     Enables testing with sparse captured data instead of full dump files.
/// </summary>
public interface IMemoryAccessor
{
    /// <summary>
    ///     Read <paramref name="count" /> bytes starting at <paramref name="position" />
    ///     into <paramref name="array" /> at the given <paramref name="offset" />.
    /// </summary>
    int ReadArray(long position, byte[] array, int offset, int count);
}

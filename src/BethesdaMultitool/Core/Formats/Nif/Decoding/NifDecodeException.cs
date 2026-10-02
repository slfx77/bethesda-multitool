namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     Thrown by a <see cref="NifDecodeMode.Strict" /> decode. The message names the block index, block type, field
///     path and offset; <see cref="Failure" /> carries them as data.
/// </summary>
/// <remarks>
///     <see cref="InvalidDataException" /> is sealed, so this derives from <see cref="Exception" />; a model reader
///     surfaces it as an <see cref="InvalidDataException" /> whose inner exception is this one, as the source-reader
///     contract requires for corrupt input.
/// </remarks>
internal sealed class NifDecodeException : Exception
{
    /// <summary>Creates the exception for a located failure.</summary>
    public NifDecodeException(NifDecodeFailure failure)
        : base(failure.Message)
    {
        Failure = failure;
    }

    /// <summary>The located failure.</summary>
    public NifDecodeFailure Failure { get; }
}

using Slfx77.Multitool.Core.Media;

namespace BethesdaMultitool.Core.Formats.Xngine.Flic;

/// <summary>Adapts the bounded Arena-style FLC/CEL profile to Shared's decoded-media contract.</summary>
/// <remarks>Indexed frames are materialized within explicit limits, so seeking is indexed after preparation.
/// Construction owns the retained input only on success; a failed constructor leaves it with the caller.
/// Shared's session serializes calls, while this adapter also drains synchronous conversions before retirement.
/// Existing classic preview dispatch and timers do not use this adapter yet.</remarks>
internal sealed class FlicDecodedMediaDecoder : IDecodedMediaDecoder
{
    private readonly Lock _gate = new();
    private readonly long _frameTicks;
    private readonly int _frameCount;
    private FlicFile? _file;
    private byte[] _rgba;
    private IAsyncDisposable? _retainedInput;
    private int _nextFrame;
    private Task? _retirement;

    /// <summary>Validates and decodes stable encoded bytes before taking ownership of an optional input prerequisite.</summary>
    /// <param name="encoded">Borrowed original bytes; stable until this synchronous preparation returns.</param>
    /// <param name="name">Source display label, independent of occurrence identity.</param>
    /// <param name="trackId">Caller-supplied stable occurrence identity within the retained source.</param>
    /// <param name="limits">Explicit allocation admission limits, or the bounded default profile.</param>
    /// <param name="retainedInput">A source lease or other prerequisite transferred only by successful construction.</param>
    /// <param name="cancellationToken">Cancels preparation before ownership is transferred.</param>
    internal FlicDecodedMediaDecoder(ReadOnlySpan<byte> encoded, string name, string trackId,
        FlicDecodeLimits? limits = null, IAsyncDisposable? retainedInput = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        var file = FlicFile.ParseBounded(encoded, name, limits ?? new FlicDecodeLimits(), cancellationToken);
        _frameTicks = checked((long)file.MillisecondsPerFrame * TimeSpan.TicksPerMillisecond);
        _frameCount = file.Frames.Count;
        Selection = new DecodedMediaTrackSelection(trackId, null);
        Description = new DecodedMediaDescription(Array.AsReadOnly(new[]
        {
            new DecodedMediaTrack(trackId, DecodedMediaKind.Video, "Autodesk.FLC", true,
                NativeIndex: 0, Label: name, Revision: "0xAF12/8-bit",
                Video: new DecodedVideoFormat(file.Width, file.Height, file.Width, file.Height,
                    NominalFrameRate: NominalFrameRate(file.MillisecondsPerFrame)))
        }), TimeSpan.Zero, TimeSpan.FromTicks(checked(_frameTicks * _frameCount)), DecodedMediaSeekKind.Indexed);
        cancellationToken.ThrowIfCancellationRequested();
        _rgba = new byte[checked(file.Width * file.Height * 4)];
        cancellationToken.ThrowIfCancellationRequested();
        _file = file;
        _retainedInput = retainedInput;
    }

    /// <inheritdoc />
    public DecodedMediaDescription Description { get; }
    /// <inheritdoc />
    public DecodedMediaTrackSelection Selection { get; }

    /// <summary>Supplies the exact fixed rate when its reduced ratio fits the optional native metadata.</summary>
    /// <param name="milliseconds">The positive stored delay already validated by the bounded parser.</param>
    /// <returns>An exact frames-per-second ratio, or null when its denominator exceeds the metadata range.</returns>
    private static (int Numerator, int Denominator)? NominalFrameRate(uint milliseconds)
    {
        uint divisor = 1000;
        for (var remainder = milliseconds; remainder != 0;)
            (divisor, remainder) = (remainder, divisor % remainder);
        var denominator = milliseconds / divisor;
        return denominator <= int.MaxValue ? ((int)(1000 / divisor), (int)denominator) : null;
    }

    /// <summary>Converts exactly one palette-paired display slot; the borrowed buffer expires at the next decoder call.</summary>
    /// <param name="cancellationToken">Cancels bounded row conversion without consuming the display slot.</param>
    /// <returns>The exact integer-millisecond timestamp/duration, or null at the declared endpoint.</returns>
    public ValueTask<VideoFrame?> ReadVideoAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_retirement is not null, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_nextFrame == _frameCount)
            {
                return ValueTask.FromResult<VideoFrame?>(null);
            }
            var file = _file!;
            var frame = file.Frames[_nextFrame];
            var palette = frame.Palette.Rgba;
            for (var row = 0; row < file.Height; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var column = 0; column < file.Width; column++)
                {
                    var pixel = row * file.Width + column;
                    palette.Slice(frame.Image.Indices[pixel] * 4, 4).CopyTo(_rgba.AsSpan(pixel * 4, 4));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = new VideoFrame(new DecodedImage(file.Width, file.Height, _rgba),
                TimeSpan.FromTicks(checked(_nextFrame * _frameTicks)), TimeSpan.FromTicks(_frameTicks));
            _nextFrame++;
            return ValueTask.FromResult<VideoFrame?>(result);
        }
    }

    /// <summary>Reports genuinely absent audio without touching the caller's buffer or advancing video.</summary>
    /// <param name="samples">Unused caller-owned PCM destination.</param>
    /// <param name="cancellationToken">Cancels before returning the absent-track result.</param>
    /// <returns>Zero complete audio frames at the current video cursor's source timestamp.</returns>
    public ValueTask<DecodedAudioReadResult> ReadAudioAsync(Memory<float> samples,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_retirement is not null, this);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new DecodedAudioReadResult(0, 0,
                TimeSpan.FromTicks(checked(_nextFrame * _frameTicks))));
        }
    }

    /// <summary>Aligns to the display-slot boundary at or before the request, clamping requests past duration to EOF.</summary>
    /// <param name="request">Absolute source timestamp and the unchanged single video selection.</param>
    /// <param name="cancellationToken">Cancels before any cursor mutation.</param>
    /// <returns>The actual frame boundary or exact endpoint; replay requires an explicit seek to zero.</returns>
    public ValueTask<DecodedMediaPosition> SeekAsync(DecodedMediaSeekRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_retirement is not null, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Selection != Selection)
            {
                throw new NotSupportedException("FLC has one fixed video track and no audio.");
            }
            ArgumentOutOfRangeException.ThrowIfLessThan(request.Position, TimeSpan.Zero);
            var frame = (int)Math.Min(request.Position.Ticks / _frameTicks, _frameCount);
            cancellationToken.ThrowIfCancellationRequested();
            _nextFrame = frame;
            return ValueTask.FromResult(new DecodedMediaPosition(TimeSpan.FromTicks(frame * _frameTicks), Selection));
        }
    }

    /// <summary>Rejects new calls before retiring the input, retaining the same terminal result on every observation.</summary>
    /// <returns>The input retirement task; failure retains the owner and never starts an implicit retry.</returns>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_retirement is not null)
            {
                return new ValueTask(_retirement);
            }
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _retirement = completion.Task;
            _file = null;
            _rgba = [];
        }
        _ = RetireInputAsync(completion);
        return new ValueTask(completion.Task);
    }

    /// <summary>Releases the prerequisite after conversions drained; failure leaves its owner reachable.</summary>
    /// <param name="completion">The already published terminal ownership boundary.</param>
    /// <returns>Completion after forwarding the original retirement result.</returns>
    private async Task RetireInputAsync(TaskCompletionSource completion)
    {
        try
        {
            if (_retainedInput is { } input)
            {
                await input.DisposeAsync().ConfigureAwait(false);
            }
            _retainedInput = null;
            completion.TrySetResult();
        }
        catch (OperationCanceledException failure) { completion.TrySetCanceled(failure.CancellationToken); }
        catch (Exception failure) { completion.TrySetException(failure); }
    }
}

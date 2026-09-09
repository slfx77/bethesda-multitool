// ORIGINAL CLEAN-ROOM IMPLEMENTATION — see InterplayMveDecoder.cs for the provenance statement.

using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Formats.Dds;

namespace BethesdaMultitool.Core.Formats.Interplay;

/// <summary>
///     The Interplay MVE implementation of <see cref="IVideoFrameSource" />, so Fallout's and
///     Fallout 2's cutscenes play through the same seam as Arena's FLIC, Daggerfall's VID and Bink.
///     <para>
///         Frames are decoded on demand, as <c>BinkVideoClip</c> does: Fallout 2's TANKER.MVE is
///         2,136 frames of 640x320, 437 MB as indices and 1.7 GB as RGBA, and every frame is coded
///         against its predecessor with no keyframes, so <see cref="GetFrame" /> decodes forward
///         from where the decoder is and replays from the start when asked to go backwards.
///     </para>
///     <para>
///         ⚑ The clip's frames are the movie's DISPLAY TICKS (send-buffer opcodes), not its
///         decodes — the game shows the still buffer during the audio-only pre-roll and the
///         timer runs through it, so <see cref="FrameCount" /> x <see cref="SecondsPerFrame" /> is
///         the real running time. A tick before the first decode shows a black frame, which is
///         what the player shows too (its buffers start zeroed and the palette black).
///     </para>
///     <para>
///         ⚠ The GUI's playback timer calls <see cref="GetFrame" /> where nothing can catch an
///         exception, so the stream is validated when the clip opens: the container must walk,
///         the buffer must be 8-bit, and every video-data opcode must be version 3. Past that the
///         decoder never throws — a malformed block skips and counts rather than faulting.
///     </para>
/// </summary>
internal sealed class InterplayMveVideoClip : IVideoFrameSource
{
    private readonly InterplayMveDecoder _decoder;
    private byte[]? _audioPcm;

    private InterplayMveVideoClip(InterplayMveDecoder decoder)
    {
        _decoder = decoder;
    }

    /// <summary>The decoder, for callers that want counters or the raw indices.</summary>
    internal InterplayMveDecoder Decoder => _decoder;

    /// <summary>Source file name, for display.</summary>
    internal string Name => _decoder.Name;

    public int Width => _decoder.Width;

    public int Height => _decoder.Height;

    /// <summary>Display ticks, or the decode count when the movie never sends a buffer to screen.</summary>
    public int FrameCount => _decoder.DisplayTicks.Count > 0 ? _decoder.DisplayTicks.Count : _decoder.DecodedFrameCount;

    public double SecondsPerFrame => _decoder.SecondsPerFrame;

    /// <summary>The audio stream's format, or null when the movie has none.</summary>
    internal MveAudioFormat? Audio => _decoder.Audio;

    /// <summary>
    ///     The whole audio track as interleaved little-endian PCM in <see cref="Audio" />'s format,
    ///     decoded once on first use. Empty when there is no audio.
    /// </summary>
    internal byte[] AudioPcm => _audioPcm ??= _decoder.DecodeAudioPcm();

    public DecodedTexture GetFrame(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);

        var decoded = _decoder.DisplayTicks.Count > 0 ? _decoder.DisplayTicks[index] : index;
        if (decoded < 0)
        {
            // Before the first decode: the zeroed buffer through the black palette.
            if (_decoder.CurrentFrame >= 0)
            {
                _decoder.Reset();
            }

            return DecodedTexture.FromBaseLevel(new byte[Width * Height * 4], Width, Height, false);
        }

        return DecodedTexture.FromBaseLevel(_decoder.DecodeFrameRgba(decoded), Width, Height, false);
    }

    /// <summary>True for the <c>.mve</c> extension. Pairs with <see cref="TryOpen" />, which checks the content.</summary>
    internal static bool CanOpen(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        return Path.GetExtension(fileName).Equals(".mve", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Opens a movie from its bytes, or returns null when it is not an Interplay MVE or will not
    ///     decode (a 16-bit buffer, a pre-version-3 video opcode, a container that does not walk).
    /// </summary>
    internal static InterplayMveVideoClip? TryOpen(byte[] bytes, string name)
    {
        if (bytes is null || bytes.Length == 0 || !InterplayMveFile.IsMveFile(bytes))
        {
            return null;
        }

        try
        {
            return new InterplayMveVideoClip(new InterplayMveDecoder(bytes, name));
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Opens a movie around a decoder the caller already built.</summary>
    internal static InterplayMveVideoClip Open(InterplayMveDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);

        return new InterplayMveVideoClip(decoder);
    }
}

using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Audio;
using BethesdaMultitool.Core.Formats.Interplay;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>One playable sound: RIFF/WAV bytes plus how long it runs, when that is known.</summary>
internal readonly record struct PlayableAudio(byte[] Riff, int SampleRate, int BitsPerSample, int Channels)
{
    /// <summary>Bytes of RIFF header ahead of the samples in <see cref="WavWriter.BuildPcm" /> output.</summary>
    public const int WavHeaderBytes = 44;

    /// <summary>Frames of audio, or 0 when the format did not declare enough to say.</summary>
    public int FrameCount =>
        Channels * BitsPerSample == 0 ? 0 : (Riff.Length - WavHeaderBytes) * 8 / (Channels * BitsPerSample);
}

/// <summary>
///     Turns an <see cref="AssetNode" /> into bytes a platform media player can play.
///     <para>
///         Classic formats are converted to RIFF/WAV rather than decoded in the player, because
///         every classic decoder in this repo already produces raw PCM plus a rate and depth —
///         which is exactly what <see cref="WavWriter.BuildPcm" /> wraps. Anything already in a
///         container the system understands passes straight through.
///     </para>
///     <para>
///         ⚠ Scope, stated honestly: this handles formats where ONE FILE IS ONE SOUND. Most classic
///         audio is not like that — Daggerfall's DAGGER.SND holds 459 sounds, Redguard's MAIN.SFX
///         118, and ENGLISH.RTX 3,933 voice lines. Those are databases addressed by index or tag,
///         so they belong to the record surfaces that can name an entry, not to a file preview that
///         can only point at the whole bank. They are listed and extractable meanwhile.
///     </para>
/// </summary>
internal static class AssetAudioSource
{
    /// <summary>Containers the platform player opens directly, so no conversion is needed.</summary>
    private static readonly string[] PassThroughExtensions = [".wav", ".mp3", ".ogg", ".flac", ".m4a"];

    /// <summary>True when this build can hand the node's bytes to a player.</summary>
    public static bool CanPlay(AssetNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Kind != AssetNodeKind.Audio)
        {
            return false;
        }

        var extension = Path.GetExtension(node.Name);
        return extension.Equals(".voc", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".acm", StringComparison.OrdinalIgnoreCase)
               || PassThroughExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Reads and converts one sound, or returns null when the node is not a single playable
    ///     sound or will not decode.
    /// </summary>
    public static PlayableAudio? TryLoad(
        AssetBrowseSession session, AssetNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(node);

        if (!CanPlay(node))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var bytes = session.FileSystem.TryReadAllBytes(node.VirtualPath);
            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            if (Path.GetExtension(node.Name).Equals(".acm", StringComparison.OrdinalIgnoreCase))
            {
                // Fallout's Interplay ACM: decoded through the libacm port to interleaved s16 at the
                // header's rate and the channel count the VALUES support — the header says 2 on every
                // speech line and they are mono (InterplayAcmChannelProbe has the measurements);
                // taken at its word, speech plays at double speed. An odd value total read as stereo
                // cannot make a whole final frame, so a stray value would be dropped here.
                var acm = InterplayAcmFile.Parse(bytes, node.Name);
                var pcm = acm.Decode().AsInferred();
                var wholeFrames = pcm.FrameCount * pcm.Channels;
                if (wholeFrames == 0)
                {
                    return null;
                }

                return new PlayableAudio(
                    WavWriter.BuildPcm(MemoryMarshal.AsBytes(pcm.Samples.AsSpan(0, wholeFrames)), pcm.SampleRate, 16,
                        pcm.Channels),
                    pcm.SampleRate,
                    16,
                    pcm.Channels);
            }

            if (!Path.GetExtension(node.Name).Equals(".voc", StringComparison.OrdinalIgnoreCase))
            {
                // Already a container the player understands; the rate fields are unknown here and
                // the player reads them itself.
                return new PlayableAudio(bytes, 0, 0, 0);
            }

            var voc = VocFile.Parse(bytes, node.Name);
            if (voc.Samples.Length == 0 || voc.SampleRate <= 0)
            {
                return null;
            }

            return new PlayableAudio(
                WavWriter.BuildPcm(voc.Samples, voc.SampleRate, voc.BitsPerSample, voc.Channels),
                voc.SampleRate,
                voc.BitsPerSample,
                voc.Channels);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException
                                      or IOException or ArgumentException)
        {
            return null;
        }
    }
}

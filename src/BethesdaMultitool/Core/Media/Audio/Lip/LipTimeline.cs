using System.Collections.ObjectModel;

namespace BethesdaMultitool.Core.Media.Audio.Lip;

/// <summary>A source track in the Fallout 3/New Vegas revision-one facial sample grid.</summary>
public sealed record LipTrack(int Index, string Group, string Name);

/// <summary>Decoded source weights, before engine settings, interpolation or actor-specific morph application.</summary>
public sealed class LipTimeline
{
    private readonly float[] _samples;

    /// <summary>Owns a validated sample array produced by the decoder.</summary>
    internal LipTimeline(uint declaredSize, int encodedSize, int frameCount, int startingFrame, float[] samples)
    {
        DeclaredSize = declaredSize;
        EncodedSize = encodedSize;
        FrameCount = frameCount;
        StartingFrame = startingFrame;
        _samples = samples;
    }

    /// <summary>The supported on-disk revision.</summary>
    public uint Revision { get; } = 1;
    /// <summary>The supported on-disk flags: byte zero-run compression, little endian.</summary>
    public uint Flags { get; } = 1;
    /// <summary>The original size field; this is neither compressed file length nor duration.</summary>
    public uint DeclaredSize { get; }
    /// <summary>The complete encoded file length.</summary>
    public int EncodedSize { get; }
    /// <summary>The number of decoded sample rows.</summary>
    public int FrameCount { get; }
    /// <summary>The signed starting-frame field, retained without guessing an audio alignment.</summary>
    public int StartingFrame { get; }
    /// <summary>The frame rate used by the verified New Vegas engine's facial keyframe submission.</summary>
    public int FramesPerSecond { get; } = 30;
    /// <summary>Source-order tracks: sixteen phonemes followed by seventeen modifiers.</summary>
    public static IReadOnlyList<LipTrack> Tracks { get; } = CreateTracks();

    /// <summary>Returns a source weight without clamping signed or above-one values.</summary>
    public float GetValue(int frameIndex, int trackIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frameIndex, FrameCount);
        ArgumentOutOfRangeException.ThrowIfNegative(trackIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(trackIndex, Tracks.Count);
        return _samples[frameIndex * Tracks.Count + trackIndex];
    }

    /// <summary>Returns time relative to the first decoded sample; it is not an audio timestamp.</summary>
    public double GetRelativeTimeSeconds(int frameIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frameIndex, FrameCount);
        return frameIndex / (double)FramesPerSecond;
    }

    /// <summary>Creates track identities verified from the New Vegas PDB enums and loader ordering.</summary>
    private static ReadOnlyCollection<LipTrack> CreateTracks()
    {
        string[] phonemes = ["Aah", "BigAah", "BMP", "ChJSh", "DST", "Eee", "Eh", "FV", "I", "K", "N", "Oh", "OohQ", "R", "Th", "W"];
        string[] modifiers = ["BlinkLeft", "BlinkRight", "BrowDownLeft", "BrowDownRight", "BrowInLeft", "BrowInRight", "BrowUpLeft", "BrowUpRight", "LookDown", "LookLeft", "LookRight", "LookUp", "SquintLeft", "SquintRight", "HeadPitch", "HeadRoll", "HeadYaw"];
        var result = new LipTrack[phonemes.Length + modifiers.Length];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = index < phonemes.Length
                ? new LipTrack(index, "phoneme", phonemes[index])
                : new LipTrack(index, "modifier", modifiers[index - phonemes.Length]);
        }
        return Array.AsReadOnly(result);
    }
}

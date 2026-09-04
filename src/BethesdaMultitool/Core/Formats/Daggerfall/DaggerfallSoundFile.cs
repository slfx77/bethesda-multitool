// The sample format is taken from daggerfall-unity's DaggerfallConnect API (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/SndFile.cs, whose reader
//   wraps each record's bytes in a PCM WAVE header at 11,025 Hz. License texts are collected
//   centrally in THIRD_PARTY_LICENSES.

using BethesdaMultitool.Core.Formats.Audio;
using BethesdaMultitool.Core.Formats.Xngine.Bsa;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     Daggerfall's sound archive, <c>DAGGER.SND</c>: a number-record XnGine BSA whose records are
///     raw, headerless 8-bit unsigned PCM at 11,025 Hz, mono. Records are addressed by INDEX, since
///     retail repeats one id.
///     <para>
///         Measured on retail (2026-09-03): 459 records, ids 0-11,462 with id 220 used twice, one
///         zero-length record, and the longest 93,717 bytes (8.5 seconds). Sample means cluster on
///         128, the silence level for unsigned 8-bit audio.
///     </para>
/// </summary>
internal sealed class DaggerfallSoundFile
{
    /// <summary>The archive's file name.</summary>
    public const string FileName = "DAGGER.SND";

    /// <summary>Sample rate of every record.</summary>
    public const int SampleRate = 11025;

    /// <summary>Bit depth of every record.</summary>
    public const int BitsPerSample = 8;

    /// <summary>Channel count of every record.</summary>
    public const int Channels = 1;

    private readonly byte[] _bytes;
    private readonly IReadOnlyList<XnGineBsaEntry> _entries;

    private DaggerfallSoundFile(byte[] bytes, IReadOnlyList<XnGineBsaEntry> entries)
    {
        _bytes = bytes;
        _entries = entries;
    }

    /// <summary>Records in the archive.</summary>
    public int Count => _entries.Count;

    /// <summary>Opens a DAGGER.SND from disk.</summary>
    public static DaggerfallSoundFile Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var archive = XnGineBsaParser.Parse(path);
        if (!archive.IsNumbered)
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is a name-record BSA; DAGGER.SND is number-record.");
        }

        return new DaggerfallSoundFile(File.ReadAllBytes(path), archive.Entries);
    }

    /// <summary>The sound id at an index.</summary>
    public uint SoundId(int index)
    {
        return _entries[index].Id ?? throw new InvalidDataException($"DAGGER.SND record {index} has no id.");
    }

    /// <summary>The first index carrying a sound id, or -1.</summary>
    public int IndexOf(uint soundId)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Id == soundId)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The raw PCM samples of one record.</summary>
    public ReadOnlyMemory<byte> Samples(int index)
    {
        var entry = _entries[index];
        if (entry.Offset < 0 || entry.Size < 0 || entry.Offset + entry.Size > _bytes.Length)
        {
            throw new InvalidDataException($"DAGGER.SND record {index} ({entry.Offset}+{entry.Size}) lies outside the {_bytes.Length}-byte archive.");
        }

        return new ReadOnlyMemory<byte>(_bytes, (int)entry.Offset, entry.Size);
    }

    /// <summary>The playing time of one record.</summary>
    public double DurationSeconds(int index)
    {
        return Samples(index).Length / (double)SampleRate;
    }

    /// <summary>One record as a complete PCM WAVE file.</summary>
    public byte[] ToWav(int index)
    {
        return WavWriter.BuildPcm(Samples(index).Span, SampleRate, BitsPerSample, Channels);
    }
}

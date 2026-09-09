using System.Text;
using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>DAGGER.SND: numbered records of headerless PCM, addressed by index, wrapped as WAV.</summary>
public class DaggerfallSoundFileTests
{
    /// <summary>A number-record XnGine BSA holding the given (id, samples) records.</summary>
    internal static byte[] Archive(params (uint Id, byte[] Samples)[] records)
    {
        var bytes = new List<byte>
        {
            (byte)(records.Length & 0xFF), (byte)((records.Length >> 8) & 0xFF), 0x00, 0x02
        };
        foreach (var record in records)
        {
            bytes.AddRange(record.Samples);
        }

        foreach (var record in records)
        {
            bytes.AddRange(BitConverter.GetBytes(record.Id));
            bytes.AddRange(BitConverter.GetBytes(record.Samples.Length));
        }

        return [.. bytes];
    }

    private static string WriteTemp(byte[] bytes)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bmt-snd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, DaggerfallSoundFile.FileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Open_ReadsRecordsByIndex_AndWrapsThemAsWav()
    {
        var path = WriteTemp(Archive(
            (3, [128, 130, 126, 128]),
            (220, [100, 200]),
            (220, []),
            (11462, [1])));
        try
        {
            var sounds = DaggerfallSoundFile.Open(path);

            Assert.Equal(4, sounds.Count);
            Assert.Equal(3u, sounds.SoundId(0));
            Assert.Equal(11462u, sounds.SoundId(3));

            // A repeated id resolves to the FIRST record; the second is reachable only by index.
            Assert.Equal(1, sounds.IndexOf(220));
            Assert.Equal(-1, sounds.IndexOf(999));
            Assert.Equal(2, sounds.Samples(1).Length);
            Assert.Equal(0, sounds.Samples(2).Length);

            Assert.Equal(4 / 11025d, sounds.DurationSeconds(0));

            var wav = sounds.ToWav(0);
            Assert.Equal(44 + 4, wav.Length);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
            Assert.Equal(1, BitConverter.ToInt16(wav, 20));
            Assert.Equal(1, BitConverter.ToInt16(wav, 22));
            Assert.Equal(DaggerfallSoundFile.SampleRate, BitConverter.ToInt32(wav, 24));
            Assert.Equal(8, BitConverter.ToInt16(wav, 34));
            Assert.Equal(new byte[] { 128, 130, 126, 128 }, wav[44..]);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void Open_RejectsANameRecordArchive()
    {
        var named = DaggerfallBlockFixture.Archive(("A.RMB", [1, 2, 3]));
        var path = WriteTemp(named);
        try
        {
            Assert.Throws<InvalidDataException>(() => DaggerfallSoundFile.Open(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }
}
using System.Text;
using BethesdaMultitool.Core.Formats.Audio;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Audio;

/// <summary>
///     Naming an audio container from its leading bytes.
///     <para>
///         The bug this guards is invisible on a console: the asset browser wrote every preview to
///         disk as <c>.wav</c>, and Media Foundation picks its handler by extension before reading
///         a byte, so an Ogg handed over under a <c>.wav</c> name is rejected by the WAVE handler.
///         Nothing throws, nothing logs a decode error — the pane just shows a platform error
///         string that reads like a corrupt file.
///     </para>
/// </summary>
public class AudioContainerFormatTests
{
    private static byte[] Leading(string tag, int totalLength = 64)
    {
        var bytes = new byte[totalLength];
        Encoding.ASCII.GetBytes(tag).CopyTo(bytes, 0);
        return bytes;
    }

    [Theory]
    [InlineData("RIFF", ".wav")]
    [InlineData("OggS", ".ogg")]
    [InlineData("fLaC", ".flac")]
    [InlineData("ID3", ".mp3")]
    public void LeadingTagNamesTheContainer(string tag, string expected)
    {
        Assert.Equal(expected, AudioContainerFormat.ExtensionFor(Leading(tag)));
    }

    /// <summary>An MP4 box carries its brand at +4, after the box length.</summary>
    [Fact]
    public void FtypAtOffsetFourIsM4a()
    {
        var bytes = new byte[64];
        Encoding.ASCII.GetBytes("ftyp").CopyTo(bytes, 4);

        Assert.Equal(".m4a", AudioContainerFormat.ExtensionFor(bytes));
    }

    /// <summary>An MP3 with no ID3 tag is recognised by the eleven-bit frame sync.</summary>
    [Theory]
    [InlineData(0xFB)]
    [InlineData(0xF3)]
    [InlineData(0xE0)]
    public void BareFrameSyncIsMp3(byte second)
    {
        Assert.Equal(".mp3", AudioContainerFormat.ExtensionFor([0xFF, second, 0x00, 0x00]));
    }

    /// <summary>
    ///     ⚠ The discriminating case. A hardcoded <c>".wav"</c> — the shipped bug — passes every
    ///     other assertion here that involves RIFF or an unknown container. Only a non-WAV
    ///     container separates the fix from the bug, so this states it directly.
    /// </summary>
    [Theory]
    [InlineData("OggS")]
    [InlineData("fLaC")]
    [InlineData("ID3")]
    public void ANonWaveContainerIsNeverNamedWav(string tag)
    {
        Assert.NotEqual(".wav", AudioContainerFormat.ExtensionFor(Leading(tag)));
    }

    /// <summary>
    ///     Unrecognised bytes stay <c>.wav</c>: every converted path (VOC, ACM, the sound banks)
    ///     produces RIFF, so the default has to be the one that plays.
    /// </summary>
    [Fact]
    public void UnknownBytesFallBackToWav()
    {
        Assert.Equal(".wav", AudioContainerFormat.ExtensionFor([0x01, 0x02, 0x03, 0x04, 0x05]));
        Assert.Equal(AudioContainerFormat.DefaultExtension, AudioContainerFormat.ExtensionFor([1, 2, 3]));
    }

    /// <summary>A truncated or empty buffer answers rather than reading off the end.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void ShortBuffersDoNotThrow(int length)
    {
        var extension = AudioContainerFormat.ExtensionFor(new byte[length]);

        Assert.StartsWith(".", extension, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A buffer long enough for "ftyp" at +4 but not for the tag itself must not be claimed.
    /// </summary>
    [Fact]
    public void AShortBufferIsNotMistakenForAnMp4Box()
    {
        Assert.Equal(".wav", AudioContainerFormat.ExtensionFor([0, 0, 0, 0, (byte)'f', (byte)'t']));
    }
}
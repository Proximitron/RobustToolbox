using NUnit.Framework;
using Robust.Shared.Audio.AudioLoading;

namespace Robust.Shared.Tests.Audio;

[TestFixture]
[TestOf(typeof(AudioLoaderWav))]
internal sealed class AudioLoaderWavTest
{
    private static MemoryStream MakeWav(short format, short channels, short bits, byte[] data, bool extensible)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        var fmtSize = extensible ? 40 : 16;
        const int sampleRate = 44100;

        w.Write("RIFF"u8);
        w.Write(4 + 8 + fmtSize + 8 + data.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(fmtSize);
        w.Write(extensible ? unchecked((short) 0xFFFE) : format);
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * bits / 8);
        w.Write((short) (channels * bits / 8));
        w.Write(bits);
        if (extensible)
        {
            w.Write((short) 22);
            w.Write(bits);
            w.Write(0);
            w.Write(format);
            w.Write(new byte[14]);
        }

        w.Write("data"u8);
        w.Write(data.Length);
        w.Write(data);
        ms.Position = 0;
        return ms;
    }

    [Test]
    public void LoadsPlainPcm()
    {
        var wav = AudioLoaderWav.LoadAudioData(MakeWav(1, 1, 16, new byte[8], false));
        Assert.That(wav.AudioType, Is.EqualTo(AudioLoaderWav.WavAudioFormatType.PCM));
        Assert.That(wav.Data.Length, Is.EqualTo(8));
    }

    [Test]
    public void LoadsExtensiblePcm()
    {
        var wav = AudioLoaderWav.LoadAudioData(MakeWav(1, 6, 24, new byte[6 * 3 * 2], true));
        Assert.That(wav.AudioType, Is.EqualTo(AudioLoaderWav.WavAudioFormatType.PCM));
        Assert.That(wav.NumChannels, Is.EqualTo(6));
        Assert.That(wav.BitsPerSample, Is.EqualTo(24));
        Assert.That(wav.Data.Length, Is.EqualTo(36));
    }

    [Test]
    public void LoadsFloat()
    {
        var wav = AudioLoaderWav.LoadAudioData(MakeWav(3, 2, 32, new byte[16], false));
        Assert.That(wav.AudioType, Is.EqualTo(AudioLoaderWav.WavAudioFormatType.IeeeFloat));
    }

    [Test]
    public void RejectsCompressed()
        => Assert.Throws<NotImplementedException>(() => AudioLoaderWav.LoadAudioData(MakeWav(0x55, 2, 16, new byte[16], false)));
}

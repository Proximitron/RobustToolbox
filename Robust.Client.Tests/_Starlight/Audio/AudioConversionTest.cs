using NUnit.Framework;
using Robust.Client.Audio;

// Resharper disable once CheckNamespace
namespace Robust.Client.Tests.Audio;

[TestFixture]
[TestOf(typeof(AudioConversion))]
internal sealed class AudioConversionTest
{
    [Test]
    public void Downmix51VorbisKeepsFrontChannels()
    {
        // Vorbis 5.1 order: FL, C, FR, RL, RR, LFE
        short[] frame = [10000, 0, -10000, 0, 0, 30000];
        var stereo = AudioConversion.DownmixToStereo(frame, 6, AudioConversion.ChannelOrder.Vorbis);

        Assert.That(stereo, Has.Length.EqualTo(2));
        Assert.That(stereo[0], Is.GreaterThan(0), "front left goes to left");
        Assert.That(stereo[1], Is.LessThan(0), "front right goes to right");
        Assert.That(stereo[0], Is.EqualTo(-stereo[1]), "LFE is dropped and channels are symmetric");
    }

    [Test]
    public void Downmix51WaveCenterGoesToBoth()
    {
        // WAV 5.1 order: FL, FR, FC, LFE, BL, BR
        short[] frame = [0, 0, 20000, 0, 0, 0];
        var stereo = AudioConversion.DownmixToStereo(frame, 6, AudioConversion.ChannelOrder.Wave);

        Assert.That(stereo[0], Is.EqualTo(stereo[1]));
        Assert.That(stereo[0], Is.GreaterThan(0));
    }

    [Test]
    public void DownmixNeverClips()
    {
        short[] frame = [short.MaxValue, short.MaxValue, short.MaxValue, short.MaxValue, short.MaxValue, short.MaxValue, short.MaxValue, short.MaxValue];
        var stereo = AudioConversion.DownmixToStereo(frame, 8, AudioConversion.ChannelOrder.Vorbis);

        Assert.That(stereo[0], Is.LessThanOrEqualTo(short.MaxValue).And.GreaterThan(30000));
        Assert.That(stereo[1], Is.LessThanOrEqualTo(short.MaxValue).And.GreaterThan(30000));
    }

    [Test]
    public void DownmixUnknownLayout()
    {
        var frame = new short[12];
        frame[0] = 1000;
        frame[1] = -1000;
        var stereo = AudioConversion.DownmixToStereo(frame, 12, AudioConversion.ChannelOrder.Wave);

        Assert.That(stereo[0], Is.GreaterThan(0));
        Assert.That(stereo[1], Is.LessThan(0));
    }
}

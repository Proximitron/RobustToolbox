using System;
using System.Buffers.Binary;
using Robust.Shared.Audio.AudioLoading;

// Resharper disable once CheckNamespace
namespace Robust.Client.Audio;

/// <summary>
/// Contains audio conversion utilities, such as downmixing and sample format conversion.
/// </summary>
internal static class AudioConversion
{
    /// <summary>
    /// The channel order of the input audio data.
    /// </summary>
    public enum ChannelOrder : byte
    {
        Vorbis,
        Wave,
    }

    private enum Speaker : byte
    {
        FrontLeft,
        FrontRight,
        Center,
        Lfe,
        SideLeft,
        SideRight,
        BackLeft,
        BackRight,
        BackCenter,
    }

    // https://www.xiph.org/vorbis/doc/Vorbis_I_spec.html#x1-810004.3.9
    private static readonly Speaker[][] VorbisLayouts =
    [
        [],
        [Speaker.Center],
        [Speaker.FrontLeft, Speaker.FrontRight],
        [Speaker.FrontLeft, Speaker.Center, Speaker.FrontRight],
        [Speaker.FrontLeft, Speaker.FrontRight, Speaker.BackLeft, Speaker.BackRight],
        [Speaker.FrontLeft, Speaker.Center, Speaker.FrontRight, Speaker.BackLeft, Speaker.BackRight],
        [Speaker.FrontLeft, Speaker.Center, Speaker.FrontRight, Speaker.BackLeft, Speaker.BackRight, Speaker.Lfe],
        [Speaker.FrontLeft, Speaker.Center, Speaker.FrontRight, Speaker.SideLeft, Speaker.SideRight, Speaker.BackCenter, Speaker.Lfe],
        [Speaker.FrontLeft, Speaker.Center, Speaker.FrontRight, Speaker.SideLeft, Speaker.SideRight, Speaker.BackLeft, Speaker.BackRight, Speaker.Lfe],
    ];

    // Default WAVE_FORMAT_EXTENSIBLE speaker assignments.
    private static readonly Speaker[][] WaveLayouts =
    [
        [],
        [Speaker.Center],
        [Speaker.FrontLeft, Speaker.FrontRight],
        [Speaker.FrontLeft, Speaker.FrontRight, Speaker.Center],
        [Speaker.FrontLeft, Speaker.FrontRight, Speaker.BackLeft, Speaker.BackRight],
        [Speaker.FrontLeft, Speaker.FrontRight, Speaker.Center, Speaker.BackLeft, Speaker.BackRight],
        [Speaker.FrontLeft, Speaker.FrontRight, Speaker.Center, Speaker.Lfe, Speaker.BackLeft, Speaker.BackRight],
        [Speaker.FrontLeft, Speaker.FrontRight, Speaker.Center, Speaker.Lfe, Speaker.BackCenter, Speaker.SideLeft, Speaker.SideRight],
        [Speaker.FrontLeft, Speaker.FrontRight, Speaker.Center, Speaker.Lfe, Speaker.BackLeft, Speaker.BackRight, Speaker.SideLeft, Speaker.SideRight],
    ];

    private const float Minus3Db = 0.70710678f;

    /// <summary>
    /// Downmix interleaved multichannel audio to stereo.
    /// </summary>
    public static short[] DownmixToStereo(ReadOnlySpan<short> samples, int channels, ChannelOrder order)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(channels, 2);

        var left = new float[channels];
        var right = new float[channels];
        var layouts = order == ChannelOrder.Vorbis ? VorbisLayouts : WaveLayouts;

        if (channels < layouts.Length)
        {
            var layout = layouts[channels];
            for (var c = 0; c < channels; c++)
            {
                (left[c], right[c]) = Coefficients(layout[c]);
            }
        }
        else
        {
            // Unknown layout, alternate channels between left and right.
            for (var c = 0; c < channels; c++)
            {
                if (c % 2 == 0)
                    left[c] = 1;
                else
                    right[c] = 1;
            }
        }

        // Normalize so the mix can't clip.
        var leftSum = 0f;
        var rightSum = 0f;
        for (var c = 0; c < channels; c++)
        {
            leftSum += left[c];
            rightSum += right[c];
        }

        var scale = 1f / MathF.Max(1f, MathF.Max(leftSum, rightSum));

        var frames = samples.Length / channels;
        var output = new short[frames * 2];
        for (var f = 0; f < frames; f++)
        {
            var frame = samples.Slice(f * channels, channels);
            float l = 0, r = 0;
            for (var c = 0; c < channels; c++)
            {
                l += frame[c] * left[c];
                r += frame[c] * right[c];
            }

            output[f * 2] = ToShort(l * scale);
            output[f * 2 + 1] = ToShort(r * scale);
        }

        return output;
    }

    private static (float Left, float Right) Coefficients(Speaker speaker)
    {
        return speaker switch
        {
            Speaker.FrontLeft => (1, 0),
            Speaker.FrontRight => (0, 1),
            Speaker.Center => (Minus3Db, Minus3Db),
            Speaker.SideLeft or Speaker.BackLeft => (Minus3Db, 0),
            Speaker.SideRight or Speaker.BackRight => (0, Minus3Db),
            Speaker.BackCenter => (0.5f, 0.5f),
            // LFE is conventionally dropped in a stereo downmix.
            _ => (0, 0),
        };
    }

    /// <summary>
    /// Convert any supported WAV sample format to interleaved 16-bit PCM.
    /// </summary>
    public static short[] WavToPcm16(in AudioLoaderWav.WavData wav)
    {
        var data = wav.Data.Span;
        var bytesPerSample = wav.BitsPerSample / 8;
        if (bytesPerSample <= 0)
            throw new InvalidOperationException($"Invalid bits per sample: {wav.BitsPerSample}");

        var count = data.Length / bytesPerSample;
        var output = new short[count];
        var isFloat = wav.AudioType == AudioLoaderWav.WavAudioFormatType.IeeeFloat;

        for (var i = 0; i < count; i++)
        {
            var sample = data.Slice(i * bytesPerSample, bytesPerSample);
            output[i] = (isFloat, wav.BitsPerSample) switch
            {
                (false, 8) => (short) ((sample[0] - 128) << 8),
                (false, 16) => BinaryPrimitives.ReadInt16LittleEndian(sample),
                (false, 24) => (short) ((sample[2] << 8) | sample[1]),
                (false, 32) => (short) (BinaryPrimitives.ReadInt32LittleEndian(sample) >> 16),
                (true, 32) => ToShort(BinaryPrimitives.ReadSingleLittleEndian(sample) * short.MaxValue),
                (true, 64) => ToShort((float) BinaryPrimitives.ReadDoubleLittleEndian(sample) * short.MaxValue),
                _ => throw new InvalidOperationException(
                    $"Unsupported wav sample format: {(isFloat ? "float" : "PCM")} {wav.BitsPerSample}-bit"),
            };
        }

        return output;
    }

    private static short ToShort(float value)
        => (short) Math.Clamp(value, short.MinValue, short.MaxValue);
}

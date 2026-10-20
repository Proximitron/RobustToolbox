using System;
using Robust.Shared.Graphics;

namespace Robust.Client.Audio;

/// <summary>
/// Has the metadata for a particular audio stream as well as the relevant internal handle to it.
/// </summary>
public sealed class AudioStream : IDisposable
{
    private IAudioInternal _audio;

    /// <summary>
    /// Buffer ID for this audio in AL.
    /// </summary>
    internal int BufferId { get; }

    public TimeSpan Length { get; }
    internal IClydeHandle? ClydeHandle { get; }
    public string? Name { get; }
    public string? Title { get; }
    public string? Artist { get; }
    public int ChannelCount { get; }

    // Starlight: which OpenAL device the buffer lives on, a rebuild invalidates every older buffer id.
    internal int DeviceGeneration { get; }

    internal AudioStream(IAudioInternal internalAudio, int bufferId, IClydeHandle? handle, TimeSpan length, int channelCount, string? name = null, string? title = null, string? artist = null)
    {
        _audio = internalAudio;
        DeviceGeneration = (internalAudio as AudioManager)?.DeviceGeneration ?? 0; // Starlight
        BufferId = bufferId;
        ClydeHandle = handle;
        Length = length;
        ChannelCount = channelCount;
        Name = name;
        Title = title;
        Artist = artist;
    }

    public void Dispose()
    {
        _audio.Remove(this);
    }
}

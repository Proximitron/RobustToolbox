using OpenTK.Audio.OpenAL;

// ReSharper disable once CheckNamespace
namespace Robust.Client.Audio.Sources;

internal sealed partial class BufferedAudioSource
{
    // Streamed audio (MIDI) refills its queue continuously, there is no offset to seek back to.
    private protected override bool RestoresPlaybackOffset => false;

    private protected override bool BindDeviceBuffers()
    {
        BufferHandles = AL.GenBuffers(BufferHandles.Length);
        BufferMap.Clear();
        for (var i = 0; i < BufferHandles.Length; i++)
        {
            BufferMap[BufferHandles[i]] = i;
        }

        // The renderer only refills buffers the source reports as processed, so queue silence to restart the cycle.
        EmptyBuffers();
        return true;
    }
}

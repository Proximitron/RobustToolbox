using System.Collections.Concurrent;
using OpenTK.Audio.OpenAL;

namespace Robust.Client.Audio;

internal partial class AudioManager
{
    // Used to track audio sources that were disposed in the finalizer thread,
    // so we need to properly send them off in the main thread.
    // Starlight-edit: entries remember their device generation, after a rebuild old ids can belong to new objects.
    private readonly ConcurrentQueue<(int sourceHandle, int filterHandle, int generation)> _sourceDisposeQueue = new();
    private readonly ConcurrentQueue<(int sourceHandle, int filterHandle, int generation)> _bufferedSourceDisposeQueue = new();
    private readonly ConcurrentQueue<(int handle, int generation)> _bufferDisposeQueue = new();

    public void FlushALDisposeQueues()
    {
        // Clear out finalized audio sources.
        while (_sourceDisposeQueue.TryDequeue(out var handles))
        {
            if (handles.generation != DeviceGeneration) // Starlight
                continue;

            OpenALSawmill.Debug("Cleaning out source {0} which finalized in another thread.", handles.sourceHandle);
            if (IsEfxSupported) RemoveEfx((handles.sourceHandle, handles.filterHandle)); // Starlight-edit
            AL.DeleteSource(handles.sourceHandle);
            _checkAlError();
            _audioSources.Remove(handles.sourceHandle);
        }

        // Clear out finalized buffered audio sources.
        while (_bufferedSourceDisposeQueue.TryDequeue(out var handles))
        {
            if (handles.generation != DeviceGeneration) // Starlight
                continue;

            OpenALSawmill.Debug("Cleaning out buffered source {0} which finalized in another thread.", handles.sourceHandle);
            if (IsEfxSupported) RemoveEfx((handles.sourceHandle, handles.filterHandle)); // Starlight-edit
            AL.DeleteSource(handles.sourceHandle);
            _checkAlError();
            _bufferedAudioSources.Remove(handles.sourceHandle);
        }

        // Clear out finalized audio buffers.
        while (_bufferDisposeQueue.TryDequeue(out var entry)) // Starlight-edit
        {
            if (entry.generation != DeviceGeneration)
                continue;

            AL.DeleteBuffer(entry.handle);
            _checkAlError();
        }
    }

    // Starlight-edit: generation parameters, defaulting to the current device.
    internal void DeleteSourceOnMainThread(int sourceHandle, int filterHandle, int generation = -1)
    {
        _sourceDisposeQueue.Enqueue((sourceHandle, filterHandle, generation == -1 ? DeviceGeneration : generation));
    }

    internal void DeleteBufferedSourceOnMainThread(int bufferedSourceHandle, int filterHandle, int generation = -1)
    {
        _bufferedSourceDisposeQueue.Enqueue((bufferedSourceHandle, filterHandle, generation == -1 ? DeviceGeneration : generation));
    }

    internal void DeleteAudioBufferOnMainThread(int bufferHandle, int generation = -1)
    {
        _bufferDisposeQueue.Enqueue((bufferHandle, generation == -1 ? DeviceGeneration : generation));
    }
}

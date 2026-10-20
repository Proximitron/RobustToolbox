using System.Numerics;
using OpenTK.Audio.OpenAL;
using Robust.Client.Audio.Effects;

// ReSharper disable once CheckNamespace
namespace Robust.Client.Audio.Sources;

public abstract partial class BaseAudioSource
{
    private AuxiliaryAudio? _auxiliary;

    internal int DeviceGeneration;

    internal struct DeviceSnapshot
    {
        public bool Looping;
        public bool Global;
        public float Pitch;
        public float MaxDistance;
        public float RolloffFactor;
        public float ReferenceDistance;
        public Vector2 Position;
        public Vector2 Velocity;
        public float Offset;
        public int State;
    }

    internal bool IsAlive => SourceHandle > 0;

    internal DeviceSnapshot CaptureDeviceState()
    {
        var snapshot = new DeviceSnapshot();
        AL.GetSource(SourceHandle, ALSourceb.Looping, out snapshot.Looping);
        AL.GetSource(SourceHandle, ALSourceb.SourceRelative, out snapshot.Global);
        AL.GetSource(SourceHandle, ALSourcef.Pitch, out snapshot.Pitch);
        AL.GetSource(SourceHandle, ALSourcef.MaxDistance, out snapshot.MaxDistance);
        AL.GetSource(SourceHandle, ALSourcef.RolloffFactor, out snapshot.RolloffFactor);
        AL.GetSource(SourceHandle, ALSourcef.ReferenceDistance, out snapshot.ReferenceDistance);
        AL.GetSource(SourceHandle, ALSource3f.Position, out var px, out var py, out _);
        AL.GetSource(SourceHandle, ALSource3f.Velocity, out var vx, out var vy, out _);
        AL.GetSource(SourceHandle, ALSourcef.SecOffset, out snapshot.Offset);
        snapshot.State = AL.GetSource(SourceHandle, ALGetSourcei.SourceState);
        snapshot.Position = new Vector2(px, py);
        snapshot.Velocity = new Vector2(vx, vy);
        AL.GetError();
        return snapshot;
    }

    internal void RestoreDeviceState(int newSourceHandle, in DeviceSnapshot snapshot)
    {
        SourceHandle = newSourceHandle;
        FilterHandle = 0;
        DeviceGeneration = Master.DeviceGeneration;

        var hasAudio = BindDeviceBuffers();

        AL.Source(SourceHandle, ALSourceb.Looping, snapshot.Looping);
        AL.Source(SourceHandle, ALSourceb.SourceRelative, snapshot.Global);
        AL.Source(SourceHandle, ALSourcef.Pitch, snapshot.Pitch);
        AL.Source(SourceHandle, ALSourcef.MaxDistance, snapshot.MaxDistance);
        AL.Source(SourceHandle, ALSourcef.RolloffFactor, snapshot.RolloffFactor);
        AL.Source(SourceHandle, ALSourcef.ReferenceDistance, snapshot.ReferenceDistance);
        AL.Source(SourceHandle, ALSource3f.Position, snapshot.Position.X, snapshot.Position.Y, 0);
        AL.Source(SourceHandle, ALSource3f.Velocity, snapshot.Velocity.X, snapshot.Velocity.Y, 0);
        AL.Source(SourceHandle, ALSourcef.Gain, _gain);

        // Re-applies the low-pass filter (EFX) or the gain based occlusion.
        Occlusion = _occlusion;

        if (Master.IsEfxSupported && _auxiliary is { Handle: > 0 } aux)
            ALC.EFX.Source(SourceHandle, EFXSourceInteger3.AuxiliarySendFilter, aux.Handle, 0, 0);

        if (hasAudio && snapshot.State is (int) ALSourceState.Playing or (int) ALSourceState.Paused)
        {
            if (RestoresPlaybackOffset && snapshot.Offset > 0)
                AL.Source(SourceHandle, ALSourcef.SecOffset, snapshot.Offset);

            AL.SourcePlay(SourceHandle);
            if (snapshot.State == (int) ALSourceState.Paused)
                AL.SourcePause(SourceHandle);
        }

        AL.GetError();
    }

    private protected abstract bool BindDeviceBuffers();

    private protected virtual bool RestoresPlaybackOffset => true;
}

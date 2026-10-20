using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using OpenTK.Audio.OpenAL;
using Robust.Client.Audio.Effects;
using Robust.Client.Audio.Sources;
using Robust.Client.ResourceManagement;
using Robust.Shared;

// ReSharper disable once CheckNamespace
namespace Robust.Client.Audio;

/// <summary>
/// Output device lifetime: opening with fallbacks, surviving disconnects and moving everything to a new device.
/// </summary>
internal sealed partial class AudioManager
{
    internal static readonly object DeviceLock = new();

    internal int DeviceGeneration { get; private set; }

    internal static bool HasCurrentContext { get; private set; }

    private bool _deviceDisconnected;
    private bool _forceLoopback;
    private int _loopbackProbeFailures;
    private Dictionary<AudioStream, AudioStream>? _migratedStreams;

    private readonly List<WeakReference<AudioEffect>> _effects = [];
    private readonly List<WeakReference<AuxiliaryAudio>> _auxiliaries = [];

    internal void TrackEffect(AudioEffect effect)
    {
        if (_effects.Count >= 64 && (_effects.Count & 63) == 0)
            _effects.RemoveAll(x => !x.TryGetTarget(out var e) || !e.IsAlive);

        _effects.Add(new WeakReference<AudioEffect>(effect));
    }

    internal void TrackAuxiliary(AuxiliaryAudio auxiliary)
    {
        if (_auxiliaries.Count >= 64 && (_auxiliaries.Count & 63) == 0)
            _auxiliaries.RemoveAll(x => !x.TryGetTarget(out var a) || !a.IsAlive);

        _auxiliaries.Add(new WeakReference<AuxiliaryAudio>(auxiliary));
    }

    #region Opening

    private bool OpenDeviceAndContext()
    {
        _loopback = false;
        if (!_forceLoopback && AudioOpenDevice())
        {
            if (AudioCreateContext())
                return true;

            OpenALSawmill.Warning("Output device opened but no context could be created on it.");
            CloseDeviceAndContext();
        }

        if (TryOpenLoopbackDevice())
        {
            if (AudioCreateContext())
                return true;

            CloseDeviceAndContext();
        }

        _loopback = false;
        return false;
    }

    private void CloseDeviceAndContext()
    {
        if (_openALContext != ALContext.Null)
        {
            ALC.MakeContextCurrent(ALContext.Null);
            HasCurrentContext = false;
            ALC.DestroyContext(_openALContext);
            _openALContext = ALContext.Null;
        }

        if (_openALDevice != ALDevice.Null)
        {
            ALC.CloseDevice(_openALDevice);
            _openALDevice = ALDevice.Null;
        }
    }

    private void HoldSourcesOnDisconnect()
    {
        if (!AL.IsExtensionPresent("AL_SOFTX_hold_on_disconnect"))
            return;

        var stopOnDisconnect = AL.GetEnumValue("AL_STOP_SOURCES_ON_DISCONNECT_SOFT");
        if (stopOnDisconnect != 0)
            AL.Disable((ALCapability) stopOnDisconnect);

        AL.GetError();
    }

    private bool IsDeviceConnected()
    {
        if (!HasAlDeviceExtension("ALC_EXT_disconnect"))
            return true;

        var connected = ALC.GetInteger(_openALDevice, (AlcGetInteger) AlcConnected);
        ALC.GetError(_openALDevice);
        return connected != 0;
    }

    private bool ProbeOutputDevice()
    {
        var probe = OpenDeviceUtf8(null);
        if (probe == ALDevice.Null)
            return false;

        var context = ALC.CreateContext(probe, (int[]?) null);
        var ok = context != ALContext.Null;
        if (ok)
            ALC.DestroyContext(context);

        ALC.GetError(probe);
        ALC.CloseDevice(probe);
        return ok;
    }

    internal void DebugRebuild(bool loopback)
    {
        if (!_audioInitialized)
            return;

        _forceLoopback = loopback;
        try
        {
            RebuildAudioDevice();
        }
        finally
        {
            _forceLoopback = false;
        }
    }

    #endregion

    #region Monitoring

    private void StarlightFrameUpdate(float frameTime)
    {
        if (!_audioInitialized)
            return;

        if (_loopback)
            RenderLoopback(frameTime);

        _deviceCheckTimer += frameTime;
        // Back off while the default device keeps refusing to open, every probe opens a device.
        var interval = DeviceCheckInterval * (1 + Math.Min(_loopbackProbeFailures, 15));
        if (_deviceCheckTimer < interval)
            return;

        _deviceCheckTimer = 0;

        try
        {
            CheckDevice();
        }
        catch (Exception e)
        {
            OpenALSawmill.Error($"Error while checking audio device state: {e}");
        }
    }

    private void CheckDevice()
    {
        if (_loopback)
        {
            // Output device showed up (e.g. headphones plugged in), move over to it.
            if (GetAudioDevices().Count == 0)
                return;

            // Make sure it actually works before tearing everything down, a rebuild reloads every sound.
            if (!ProbeOutputDevice())
            {
                _loopbackProbeFailures++;
                return;
            }

            _loopbackProbeFailures = 0;
            OpenALSawmill.Info("Audio output device became available.");
            RebuildAudioDevice();
            return;
        }

        if (!IsDeviceConnected())
        {
            HandleDisconnectedDevice();
            return;
        }

        if (!_cfg.GetCVar(CVars.AudioFollowDefaultDevice) || _cfg.GetCVar(CVars.AudioDevice) != "")
            return;

        var defaultDevice = GetDefaultAudioDevice();
        if (defaultDevice == null || defaultDevice == _lastDefaultDevice)
            return;

        var previous = _lastDefaultDevice;
        _lastDefaultDevice = defaultDevice;
        if (previous == null)
            return;

        OpenALSawmill.Info($"System default audio device changed to '{defaultDevice}', switching.");
        lock (DeviceLock)
        {
            if (TryReopenAudioDevice(""))
                ReloadDeviceExtensions();
        }
    }

    private void HandleDisconnectedDevice()
    {
        if (!_deviceDisconnected)
        {
            _deviceDisconnected = true;
            OpenALSawmill.Warning("Audio output device got disconnected, audio resumes once an output is available.");
        }

        var devices = GetAudioDevices();
        if (devices.Count == 0)
            return;

        var preferred = _cfg.GetCVar(CVars.AudioDevice);
        lock (DeviceLock)
        {
            if (preferred != "" && devices.Contains(preferred) && TryReopenAudioDevice(preferred)
                || TryReopenAudioDevice(""))
            {
                ReloadDeviceExtensions();
                _lastDefaultDevice = GetDefaultAudioDevice();
                OpenALSawmill.Info("Recovered audio output.");
                return;
            }
        }

        // Only without ALC_SOFT_reopen_device (not OpenAL Soft) a rebuild is the way back.
        if (!ALC.IsExtensionPresent(_openALDevice, "ALC_SOFT_reopen_device"))
            RebuildAudioDevice();
    }

    #endregion

    #region Rebuild

    private void RebuildAudioDevice()
    {
        lock (DeviceLock)
        {
            RebuildAudioDeviceLocked();
        }
    }

    private void RebuildAudioDeviceLocked()
    {
        FlushALDisposeQueues();

        var sources = new List<(BaseAudioSource Source, BaseAudioSource.DeviceSnapshot State)>();
        foreach (var weak in _audioSources.Values)
        {
            if (weak.TryGetTarget(out var source) && source.IsAlive)
                sources.Add((source, source.CaptureDeviceState()));
        }

        foreach (var weak in _bufferedAudioSources.Values)
        {
            if (weak.TryGetTarget(out var source) && source.IsAlive)
                sources.Add((source, source.CaptureDeviceState()));
        }

        var effects = new List<(AudioEffect Effect, AudioEffect.DeviceSnapshot? State)>();
        foreach (var weak in _effects)
        {
            if (weak.TryGetTarget(out var effect) && effect.IsAlive)
                effects.Add((effect, IsEfxSupported ? effect.CaptureDeviceState() : null));
        }

        var auxiliaries = new List<AuxiliaryAudio>();
        foreach (var weak in _auxiliaries)
        {
            if (weak.TryGetTarget(out var auxiliary) && auxiliary.IsAlive)
                auxiliaries.Add(auxiliary);
        }

        // Destroying the context and closing the device frees every source, buffer and effect on it.
        CloseDeviceAndContext();
        _audioSources.Clear();
        _bufferedAudioSources.Clear();
        _audioSampleBuffers.Clear();
        DeviceGeneration++;
        _deviceDisconnected = false;

        if (!OpenDeviceAndContext())
        {
            OpenALSawmill.Error("Failed to re-create any OpenAL device, audio is disabled.");
            _audioInitialized = false;
            return;
        }

        IsEfxSupported = HasAlDeviceExtension("ALC_EXT_EFX");
        SetMasterGain(_cfg.GetCVar(CVars.AudioMasterVolume));

        // Distance model and doppler are context state, the old rebuild path forgot to restore these.
        if (_attenuation != Shared.Audio.Attenuation.Invalid)
            SetAttenuation(_attenuation);
        SetDopplerFactor(_dopplerFactor);

        _lastDefaultDevice = GetDefaultAudioDevice();
        _migratedStreams = ReloadAudioResources();

        try
        {
            foreach (var (effect, state) in effects)
            {
                effect.RestoreDeviceState(state, IsEfxSupported);
            }

            foreach (var auxiliary in auxiliaries)
            {
                auxiliary.RestoreDeviceState(IsEfxSupported);
            }

            var failed = 0;
            foreach (var (source, state) in sources)
            {
                var handle = AL.GenSource();
                if (!AL.IsSource(handle))
                {
                    failed++;
                    continue;
                }

                source.RestoreDeviceState(handle, state);
                if (source is BufferedAudioSource buffered)
                    _bufferedAudioSources[handle] = new WeakReference<BufferedAudioSource>(buffered);
                else
                    _audioSources[handle] = new WeakReference<BaseAudioSource>(source);
            }

            OpenALSawmill.Info($"Moved {sources.Count - failed}/{sources.Count} audio sources and {effects.Count} effects to the new audio device.");
        }
        finally
        {
            _migratedStreams = null;
        }

        AL.GetError();
    }

    private Dictionary<AudioStream, AudioStream> ReloadAudioResources()
    {
        var migrated = new Dictionary<AudioStream, AudioStream>(ReferenceEqualityComparer.Instance);
        var resources = _cache.GetAllResources<AudioResource>().ToArray();
        var failed = 0;
        foreach (var (path, resource) in resources)
        {
            var old = resource.AudioStream;
            try
            {
                _cache.ReloadResource<AudioResource>(path);
                if (!ReferenceEquals(old, resource.AudioStream))
                    migrated[old] = resource.AudioStream;
            }
            catch (Exception)
            {
                // Resources created from memory (e.g. raw/streamed audio) have no file to reload from.
                failed++;
            }
        }

        OpenALSawmill.Info($"Reloaded {resources.Length - failed}/{resources.Length} audio resources after device rebuild.");
        return migrated;
    }

    internal bool TryGetMigratedStream(AudioStream old, [NotNullWhen(true)] out AudioStream? stream)
    {
        if (old.DeviceGeneration == DeviceGeneration)
        {
            stream = old;
            return true;
        }

        stream = null;
        return _migratedStreams != null && _migratedStreams.TryGetValue(old, out stream);
    }

    internal bool TryBindStream(int sourceHandle, AudioStream stream)
    {
        if (stream.DeviceGeneration != DeviceGeneration
            || !_audioSampleBuffers.TryGetValue(stream.BufferId, out var sample))
        {
            return false;
        }

        AL.Source(sourceHandle, ALSourcei.Buffer, sample.BufferHandle);
        return true;
    }

    #endregion
}

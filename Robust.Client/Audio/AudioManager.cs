using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using OpenTK.Audio.OpenAL;
using Robust.Client.Audio.Sources;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared;
using Robust.Shared.Audio;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Robust.Client.Audio;

internal sealed partial class AudioManager : IAudioInternal
{
    [Shared.IoC.Dependency] private IConfigurationManager _cfg = default!;
    [Shared.IoC.Dependency] private ILogManager _logMan = default!;
    [Shared.IoC.Dependency] private IReloadManager _reload = default!;
    [Shared.IoC.Dependency] private IResourceCache _cache = default!;
    [Shared.IoC.Dependency] private IClydeInternal _clyde = default!;

    private Thread? _gameThread;

    private ALDevice _openALDevice;
    private ALContext _openALContext;
    #region Starlight
    private readonly Dictionary<int, LoadedAudioSample> _audioSampleBuffers = [];

    private readonly Dictionary<int, WeakReference<BaseAudioSource>> _audioSources = [];

    private readonly Dictionary<int, WeakReference<BufferedAudioSource>> _bufferedAudioSources = [];

    private readonly HashSet<string> _alcDeviceExtensions = [];
    private readonly HashSet<string> _alContextExtensions = [];
    #endregion
    private Attenuation _attenuation;
    private bool _audioInitialized;
    private bool _focused = true;
    private bool _muteUnfocused;
    private const float MasterFadeDuration = 0.25f;
    private float _masterFadeElapsed = MasterFadeDuration;
    private float _masterFadeStartGain = 1f;
    private float _masterFadeTargetGain = 1f;

    public bool HasAlDeviceExtension(string extension) => _alcDeviceExtensions.Contains(extension);
    public bool HasAlContextExtension(string extension) => _alContextExtensions.Contains(extension);

    internal bool IsEfxSupported;

    bool IAudioInternal.IsEfxSupported => IsEfxSupported;

    internal ISawmill OpenALSawmill = default!;

    private void AudioCreateContext() // Starlight-edit
    {
        // Load up AL context extensions.
        var s = ALC.GetString(_openALDevice, AlcGetString.Extensions) ?? "";
        foreach (var extension in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            _alContextExtensions.Add(extension);
        }

        var supportsHrtf = HasAlContextExtension("ALC_SOFT_HRTF");
        var attributeList = BuildContextAttributes(); // Starlight: HRTF, output mode and loopback attributes.

        _openALContext = ALC.CreateContext(_openALDevice, attributeList);

        ALC.MakeContextCurrent(_openALContext);
        CheckAlcError(_openALDevice); // Starlight-edit
        _checkAlError();

        var hrtfEnabled = supportsHrtf ? ALC.GetInteger(_openALDevice, (AlcGetInteger)AlcSoftGetInteger.HrtfStatus) : 0;
        OpenALSawmill.Debug("OpenAL Vendor: {0}", AL.Get(ALGetString.Vendor));
        OpenALSawmill.Debug("OpenAL Renderer: {0}", AL.Get(ALGetString.Renderer));
        OpenALSawmill.Debug("OpenAL Version: {0}", AL.Get(ALGetString.Version));
        OpenALSawmill.Debug("HRTF status: {0}", hrtfEnabled == 1 ? "Enabled" : "Disabled");
    }

    public IReadOnlyList<string> GetAudioDevices()
    {
        // Starlight-start: UTF-8 device names.
        if (ALC.EnumerateAll.IsExtensionPresent() && AlcGetStringListUtf8(AlcAllDevicesSpecifier) is { } allDevices)
            return allDevices;

        if (ALC.IsExtensionPresent(ALDevice.Null, "ALC_ENUMERATION_EXT") && AlcGetStringListUtf8(AlcDeviceSpecifier) is { } devices)
            return devices;
        // Starlight-end

        if (ALC.EnumerateAll.IsExtensionPresent())
            return ALC.EnumerateAll.GetStringList(GetEnumerateAllContextStringList.AllDevicesSpecifier).ToList();

        if (ALC.IsExtensionPresent(ALDevice.Null, "ALC_ENUMERATION_EXT"))
            return ALC.GetStringList(GetEnumerationStringList.DeviceSpecifier).ToList();

        return [];
    }

    public string? GetDefaultAudioDevice()
    {
        // Starlight-start: UTF-8 device names.
        if (ALC.EnumerateAll.IsExtensionPresent() && AlcGetStringUtf8(ALDevice.Null, AlcDefaultAllDevicesSpecifier) is { } defaultAll)
            return defaultAll;

        if (ALC.IsExtensionPresent(ALDevice.Null, "ALC_ENUMERATION_EXT") && AlcGetStringUtf8(ALDevice.Null, AlcDefaultDeviceSpecifier) is { } defaultDevice)
            return defaultDevice;
        // Starlight-end

        if (ALC.EnumerateAll.IsExtensionPresent())
            return ALC.EnumerateAll.GetString(ALDevice.Null, GetEnumerateAllContextString.DefaultAllDevicesSpecifier);

        if (ALC.IsExtensionPresent(ALDevice.Null, "ALC_ENUMERATION_EXT"))
            return ALC.GetString(ALDevice.Null, AlcGetString.DefaultDeviceSpecifier);

        return null;
    }

    private bool AudioOpenDevice()
    {
        var preferredDevice = _cfg.GetCVar(CVars.AudioDevice);

        // Open device.
        if (!string.IsNullOrEmpty(preferredDevice))
        {
            _openALDevice = OpenDeviceUtf8(preferredDevice); // Starlight-edit
            if (_openALDevice == IntPtr.Zero)
            {
                OpenALSawmill.Warning("Unable to open preferred audio device '{0}': {1}. Falling back default.",
                    preferredDevice, ALC.GetError(ALDevice.Null));

                _openALDevice = OpenDeviceUtf8(null); // Starlight-edit
            }
        }
        else
        {
            _openALDevice = OpenDeviceUtf8(null); // Starlight-edit
        }

        CheckAlcError(_openALDevice); // Starlight-edit

        if (_openALDevice == IntPtr.Zero)
        {
            OpenALSawmill.Error("Unable to open OpenAL device! {0}", ALC.GetError(ALDevice.Null)); // Starlight-edit: format index fix
            return false;
        }

        // Load up ALC extensions.
        ReloadDeviceExtensions(); // Starlight-edit
        return true;
    }

    private void InitializeAudio()
    {
        OpenALSawmill = _logMan.GetSawmill("clyde.oal");

        PreloadOpenAl();

        // Starlight-start: without any output device, run on a silent loopback device instead of having no AL context.
        if (!AudioOpenDevice() && !TryOpenLoopbackDevice())
            return;

        // Create OpenAL context.
        AudioCreateContext();
        // Starlight-end

        IsEfxSupported = HasAlDeviceExtension("ALC_EXT_EFX");

        _cfg.OnValueChanged(CVars.AudioMasterVolume, SetMasterGain, true);
        _cfg.OnValueChanged(CVars.AudioMuteUnfocused, OnMuteUnfocusedChanged, true);
        _cfg.OnValueChanged(CVars.AudioDevice, OnAudioDeviceChanged);
        _clyde.OnWindowFocused += OnWindowFocused;

        _reload.Register("/Audio", "*.ogg");
        _reload.Register("/Audio", "*.wav");

        _reload.OnChanged += OnReload;
        _audioInitialized = true;
        InitializeStarlight(); // Starlight
    }

    private void OnMuteUnfocusedChanged(bool muteUnfocused)
    {
        _muteUnfocused = muteUnfocused;
        SetMasterFadeTarget(GetMasterFadeTarget());
    }

    private void OnWindowFocused(WindowFocusedEventArgs args)
    {
        if (args.Window != _clyde.MainWindow)
            return;

        _focused = args.Focused;
        SetMasterFadeTarget(GetMasterFadeTarget());
    }

    private float GetMasterFadeTarget()
    {
        return _muteUnfocused && !_focused ? 0f : 1f;
    }

    private void SetMasterFadeTarget(float fadeGain)
    {
        if (MathF.Abs(_masterFadeTargetGain - fadeGain) < 0.001f)
            return;

        _masterFadeStartGain = FadeGain;
        _masterFadeTargetGain = fadeGain;
        _masterFadeElapsed = 0f;
        ApplyMasterGain();
    }

    private void OnAudioDeviceChanged(string deviceSpecifier)
    {
        if (!_audioInitialized)
            return;

        SwitchAudioDevice(deviceSpecifier);
    }

    private void SwitchAudioDevice(string requestedDevice)
    {
        OpenALSawmill.Info("Switching OpenAL output device to {0}.",
            string.IsNullOrEmpty(requestedDevice) ? "<default>" : requestedDevice);

        if (!_loopback && TryReopenAudioDevice(requestedDevice)) // Starlight-edit: loopback devices can't be reopened, only rebuilt.
        {
            ReloadDeviceExtensions();
            IsEfxSupported = HasAlDeviceExtension("ALC_EXT_EFX");
            SetMasterGain(_cfg.GetCVar(CVars.AudioMasterVolume));
            _lastDefaultDevice = GetDefaultAudioDevice(); // Starlight-edit
            return;
        }

        // The skrunkly path that's hopefully never needed.
        OpenALSawmill.Warning(
            "ALC_SOFT_reopen_device is unavailable or failed. Falling back to full audio device rebuild.");

        RebuildAudioDevice(); // Starlight-edit
    }

    private bool TryReopenAudioDevice(string requestedDevice)
    {
        if (_loopback || // Starlight-edit
            _openALDevice == ALDevice.Null || // Starlight-edit
            !ALC.IsExtensionPresent(_openALDevice, "ALC_SOFT_reopen_device"))
        {
            return false;
        }

        var reopen = LoadAlcDelegate<AlcReopenDeviceSoftDelegate>("alcReopenDeviceSOFT");
        if (reopen == null)
            return false;

        var reopenTarget = string.IsNullOrEmpty(requestedDevice) ? null : requestedDevice;
        var reopened = reopen(_openALDevice, reopenTarget, BuildContextAttributes()); // Starlight-edit: pass our context attributes, otherwise HRTF/output mode were reset on every device switch.
        CheckAlcError(_openALDevice); // Starlight-edit
        return reopened;
    }

    private void ReloadDeviceExtensions()
    {
        _alcDeviceExtensions.Clear();

        var s = ALC.GetString(_openALDevice, AlcGetString.Extensions) ?? "";
        foreach (var extension in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            _alcDeviceExtensions.Add(extension);
        }
    }

    private void OnReload(ResPath args)
    {
        if (args.Extension != "ogg" &&
            args.Extension != "wav")
        {
            return;
        }

        _cache.ReloadResource<AudioResource>(args);
    }

    internal bool IsMainThread()
    {
        return Thread.CurrentThread == _gameThread;
    }

    private static void RemoveEfx((int sourceHandle, int filterHandle) handles)
    {
        if (handles.filterHandle != 0)
            ALC.EFX.DeleteFilter(handles.filterHandle);
    }

    private void CheckAlcError(ALDevice device, // Starlight-edit
        [CallerMemberName] string callerMember = "",
        [CallerLineNumber] int callerLineNumber = -1)
    {
        var error = ALC.GetError(device);
        if (error != AlcError.NoError)
        {
            OpenALSawmill.Error("[{0}:{1}] ALC error: {2}", callerMember, callerLineNumber, error);
        }
    }

    internal void LogError(string message)
    {
        OpenALSawmill.Error(message);
    }

    /*
     * Evil hack because OpenTK doesn't expose the device switch call.
     */

    // Starlight-start: ALCboolean is one byte and device names are UTF-8, the default marshalling got both wrong.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool AlcReopenDeviceSoftDelegate(
        ALDevice device,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? deviceName,
        int[] attribs);
    // Starlight-end

    private static TDelegate? LoadAlcDelegate<TDelegate>(string name)
        where TDelegate : Delegate
    {
        var type = typeof(ALC);
        while (type != null)
        {
            var method = type.GetMethod(
                "LoadDelegate",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);

            if (method != null)
                return (TDelegate?) method.MakeGenericMethod(typeof(TDelegate)).Invoke(null, [name]);

            type = type.BaseType;
        }

        return null;
    }

    /// <summary>
    /// Like _checkAlError but allows custom data to be passed in as relevant.
    /// </summary>
    internal void LogALError(ALErrorInterpolatedStringHandler message, [CallerMemberName] string callerMember = "", [CallerLineNumber] int callerLineNumber = -1)
    {
        if (message.Error != ALError.NoError)
        {
            OpenALSawmill.Error("[{0}:{1}] AL error: {2}, {3}. Stacktrace is {4}", callerMember, callerLineNumber, message.Error, message.ToStringAndClear(), Environment.StackTrace);
        }
    }

    public void _checkAlError([CallerMemberName] string callerMember = "", [CallerLineNumber] int callerLineNumber = -1)
    {
        var error = AL.GetError();
        if (error != ALError.NoError)
        {
            OpenALSawmill.Error("[{0}:{1}] AL error: {2}", callerMember, callerLineNumber, error);
        }
    }

    /// <summary>
    ///     OpenAL Soft specific enum. OpenTK packages the regular OpenAL ones, but we use OpenAL Soft on most platforms,
    ///     and these are used at the moment for HRTF support specifically.
    /// </summary>
    // https://github.com/kcat/openal-soft/blob/e9c479eb4190101bc51179afae56fc6dd5d26066/include/AL/alext.h#L471
    public enum AlcSoftGetInteger
    {
        Hrtf = 0x1992,
        HrtfStatus = 0x1993,
        NumHrtfSpecifiers = 0x1994,
        HrtfId = 0x1996
    }

    private sealed class LoadedAudioSample(int bufferHandle)
    {
        public readonly int BufferHandle = bufferHandle;
    }

    [InterpolatedStringHandler]
    internal ref struct ALErrorInterpolatedStringHandler
    {
        private DefaultInterpolatedStringHandler _handler;
        public ALError Error;

        public ALErrorInterpolatedStringHandler(int literalLength, int formattedCount, out bool shouldAppend)
        {
            Error = AL.GetError();
            if (Error == ALError.NoError)
            {
                shouldAppend = false;
                _handler = default;
            }
            else
            {
                shouldAppend = true;
                _handler = new DefaultInterpolatedStringHandler(literalLength, formattedCount);
            }
        }

        public string ToStringAndClear() => _handler.ToStringAndClear();
        public override string ToString() => _handler.ToString();
        public void AppendLiteral(string value) => _handler.AppendLiteral(value);
        public void AppendFormatted<T>(T value) => _handler.AppendFormatted(value);
        public void AppendFormatted<T>(T value, string? format) => _handler.AppendFormatted(value, format);
    }
}

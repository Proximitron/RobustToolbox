using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using OpenTK.Audio.OpenAL;
using Robust.Client.ResourceManagement;
using Robust.Shared;

// ReSharper disable once CheckNamespace
namespace Robust.Client.Audio;

/// <summary>
/// Contains OpenAL device management and audio output mode handling. This is the Starlight-specific part of <see cref="AudioManager"/>.
/// </summary>
internal sealed partial class AudioManager
{
    // ALC enums not exposed by OpenTK. See openal-soft include/AL/alc.h and alext.h
    private const int AlcDefaultDeviceSpecifier = 0x1004;
    private const int AlcDeviceSpecifier = 0x1005;
    private const int AlcFrequency = 0x1007;
    private const int AlcDefaultAllDevicesSpecifier = 0x1012;
    private const int AlcAllDevicesSpecifier = 0x1013;
    private const int AlcConnected = 0x313;
    private const int AlcShortSoft = 0x1402;
    private const int AlcMonoSoft = 0x1500;
    private const int AlcStereoSoft = 0x1501;
    private const int AlcQuadSoft = 0x1503;
    private const int AlcSurround51Soft = 0x1504;
    private const int AlcSurround71Soft = 0x1506;
    private const int AlcFormatChannelsSoft = 0x1990;
    private const int AlcFormatTypeSoft = 0x1991;
    private const int AlcOutputModeSoft = 0x19AC;
    private const int AlcStereoBasicSoft = 0x19AE;
    private const int AlcStereoHrtfSoft = 0x19B2;

    private const int LoopbackFrequency = 44100;
    private const float DeviceCheckInterval = 2f;

    private bool _loopback;
    private AlcRenderSamplesSoftDelegate? _renderSamples;
    private readonly short[] _loopbackBuffer = new short[LoopbackFrequency / 10 * 2];
    private double _loopbackPendingFrames;

    private float _deviceCheckTimer;
    private string? _lastDefaultDevice;
    private float _dopplerFactor = 1f;

    private AlcGetStringDelegate? _alcGetString;
    private AlcOpenDeviceDelegate? _alcOpenDevice;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AlcGetStringDelegate(ALDevice device, int param);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ALDevice AlcOpenDeviceDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string? deviceName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ALDevice AlcLoopbackOpenDeviceSoftDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string? deviceName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AlcRenderSamplesSoftDelegate(ALDevice device, IntPtr buffer, int samples);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool AlcResetDeviceSoftDelegate(ALDevice device, int[] attribs);

    private void InitializeStarlight()
    {
        _cfg.OnValueChanged(CVars.AudioOutputMode, OnOutputModeChanged);
        _lastDefaultDevice = GetDefaultAudioDevice();
    }

    private void ShutdownStarlight()
    {
        _cfg.UnsubValueChanged(CVars.AudioOutputMode, OnOutputModeChanged);
    }

    #region Device names

    private string? AlcGetStringUtf8(ALDevice device, int param)
    {
        _alcGetString ??= LoadAlcDelegate<AlcGetStringDelegate>("alcGetString");
        if (_alcGetString == null)
            return null;

        var ptr = _alcGetString(device, param);
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }

    private unsafe List<string>? AlcGetStringListUtf8(int param)
    {
        _alcGetString ??= LoadAlcDelegate<AlcGetStringDelegate>("alcGetString");
        if (_alcGetString == null)
            return null;

        // Device lists are a sequence of null-terminated strings, terminated by an empty string.
        var ptr = (byte*) _alcGetString(ALDevice.Null, param);
        var list = new List<string>();
        if (ptr == null)
            return list;

        while (*ptr != 0)
        {
            var span = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(ptr);
            list.Add(System.Text.Encoding.UTF8.GetString(span));
            ptr += span.Length + 1;
        }

        return list;
    }

    private ALDevice OpenDeviceUtf8(string? name)
    {
        _alcOpenDevice ??= LoadAlcDelegate<AlcOpenDeviceDelegate>("alcOpenDevice");
        if (_alcOpenDevice == null)
            return ALC.OpenDevice(name);

        var device = _alcOpenDevice(name);

        // Names saved before this fix were decoded as ANSI, try that too so the user's choice isn't lost.
        if (device == ALDevice.Null && name != null)
            device = ALC.OpenDevice(name);

        return device;
    }

    #endregion

    #region Context attributes

    private int[] BuildContextAttributes()
    {
        if (_loopback)
        {
            return
            [
                AlcFormatChannelsSoft, AlcStereoSoft,
                AlcFormatTypeSoft, AlcShortSoft,
                AlcFrequency, LoopbackFrequency,
                0
            ];
        }

        var attributes = new List<int>();
        var supportsHrtf = HasAlContextExtension("ALC_SOFT_HRTF");
        var mode = ParseOutputMode(_cfg.GetCVar(CVars.AudioOutputMode));

        if (mode != null && HasAlDeviceExtension("ALC_SOFT_output_mode"))
        {
            attributes.Add(AlcOutputModeSoft);
            attributes.Add(mode.Value);

            // An explicit speaker layout overrides the HRTF cvar, HRTF is only for headphones.
            if (supportsHrtf)
            {
                attributes.Add((int) AlcSoftGetInteger.Hrtf);
                attributes.Add(mode.Value == AlcStereoHrtfSoft ? 1 : 0);
            }
        }
        else if (_cfg.GetCVar(CVars.AudioHrtf) && supportsHrtf)
        {
            var hrtfCount = ALC.GetInteger(_openALDevice, (AlcGetInteger)AlcSoftGetInteger.NumHrtfSpecifiers);
            OpenALSawmill.Debug("HRTF specifier count: {0}", hrtfCount);

            if (hrtfCount > 0)
            {
                // default to the first specifier, im not even really sure in what contexts more than one of these
                // exists and i dont think its super valuable to expose another cvar for changing the index
                attributes.AddRange([(int) AlcSoftGetInteger.Hrtf, 1, (int) AlcSoftGetInteger.HrtfId, 0]);
            }
            else
            {
                OpenALSawmill.Warning("HRTF support enabled but no supported specifiers, HRTF will be disabled");
            }
        }

        attributes.Add(0);
        return attributes.ToArray();
    }

    private int? ParseOutputMode(string mode)
    {
        switch (mode.Trim().ToLowerInvariant())
        {
            case "":
            case "auto":
                return null;
            case "mono":
                return AlcMonoSoft;
            case "stereo":
                return AlcStereoBasicSoft;
            case "headphones":
            case "hrtf":
                return AlcStereoHrtfSoft;
            case "quad":
                return AlcQuadSoft;
            case "5.1":
            case "surround51":
                return AlcSurround51Soft;
            case "7.1":
            case "surround71":
                return AlcSurround71Soft;
            default:
                OpenALSawmill.Warning($"Unknown audio.output_mode '{mode}', using auto. Valid: auto, mono, stereo, headphones, quad, 5.1, 7.1");
                return null;
        }
    }

    private void OnOutputModeChanged(string _)
    {
        if (!_audioInitialized || _loopback)
            return;

        // alcResetDeviceSOFT applies new attributes without losing any buffers or sources.
        var reset = HasAlDeviceExtension("ALC_SOFT_HRTF")
            ? LoadAlcDelegate<AlcResetDeviceSoftDelegate>("alcResetDeviceSOFT")
            : null;

        bool applied;
        lock (DeviceLock)
        {
            applied = reset != null && reset(_openALDevice, BuildContextAttributes());
        }

        if (applied)
        {
            OpenALSawmill.Info("Applied new audio output mode.");
            return;
        }

        RebuildAudioDevice();
    }

    #endregion

    #region Loopback (no output device)

    private bool TryOpenLoopbackDevice()
    {
        if (!ALC.IsExtensionPresent(ALDevice.Null, "ALC_SOFT_loopback"))
            return false;

        var open = LoadAlcDelegate<AlcLoopbackOpenDeviceSoftDelegate>("alcLoopbackOpenDeviceSOFT");
        _renderSamples = LoadAlcDelegate<AlcRenderSamplesSoftDelegate>("alcRenderSamplesSOFT");
        if (open == null || _renderSamples == null)
            return false;

        var device = open(null);
        if (device == ALDevice.Null)
            return false;

        _openALDevice = device;
        _loopback = true;
        _loopbackPendingFrames = 0;
        ReloadDeviceExtensions();

        OpenALSawmill.Warning("No audio output device available, audio is silent until one gets connected.");
        return true;
    }

    /// <summary>
    /// Loopback devices only advance when rendered, so mix and discard output to keep sources progressing
    /// (and finishing) like they would on a real device.
    /// </summary>
    private unsafe void RenderLoopback(float frameTime)
    {
        if (_renderSamples == null)
            return;

        _loopbackPendingFrames += frameTime * LoopbackFrequency;

        // Don't try to catch up after long hitches.
        if (_loopbackPendingFrames > LoopbackFrequency)
            _loopbackPendingFrames = LoopbackFrequency;

        var frames = (int) _loopbackPendingFrames;
        _loopbackPendingFrames -= frames;

        fixed (short* ptr = _loopbackBuffer)
        {
            while (frames > 0)
            {
                var count = Math.Min(frames, _loopbackBuffer.Length / 2);
                _renderSamples(_openALDevice, (IntPtr) ptr, count);
                frames -= count;
            }
        }
    }

    #endregion
}

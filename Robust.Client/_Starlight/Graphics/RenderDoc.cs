using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Robust.Shared.Log;

namespace Robust.Client.Graphics.Clyde;

/// <summary>
/// RenderDoc in-application API (renderdoc_app.h). Only works when the library is loaded before any graphics API
/// is initialized, which <see cref="Clyde.InitializePreWindowing"/> does when <c>display.renderdoc</c> is set.
/// </summary>
internal static unsafe class RenderDoc
{
    /// <summary>
    /// eRENDERDOC_API_Version_1_6_0, the function table below is laid out for it.
    /// </summary>
    private const int ApiVersion = 1_06_00;

    private const string PathEnvVar = "ROBUST_RENDERDOC_PATH";
    private const string CapturesEnvVar = "ROBUST_RENDERDOC_CAPTURES";

    // Indices into RENDERDOC_API_1_6_0, every entry is one function pointer.
    private const int FnSetCaptureFilePathTemplate = 11;
    private const int FnGetNumCaptures = 13;
    private const int FnGetCapture = 14;
    private const int FnTriggerCapture = 15;
    private const int FnIsTargetControlConnected = 16;
    private const int FnLaunchReplayUI = 17;
    private const int FnTriggerMultiFrameCapture = 22;
    private const int FnShowReplayUI = 25;

    private static void** _api;

    public static bool IsLoaded => _api != null;

    public static bool TryLoad(ISawmill sawmill)
    {
        if (_api != null)
            return true;

        var handle = IntPtr.Zero;
        foreach (var candidate in Candidates())
        {
            if (NativeLibrary.TryLoad(candidate, out handle))
            {
                sawmill.Info($"Loaded RenderDoc from {candidate}");
                break;
            }
        }

        if (handle == IntPtr.Zero)
        {
            sawmill.Warning($"RenderDoc requested but not found. Install it or set {PathEnvVar} to its library.");
            return false;
        }

        if (!NativeLibrary.TryGetExport(handle, "RENDERDOC_GetAPI", out var getApiPtr))
        {
            sawmill.Error("RenderDoc library has no RENDERDOC_GetAPI export.");
            return false;
        }

        void* api;
        var getApi = (delegate* unmanaged[Cdecl]<int, void**, int>)getApiPtr;
        if (getApi(ApiVersion, &api) != 1 || api == null)
        {
            sawmill.Error("RenderDoc doesn't support API 1.6.0, update RenderDoc.");
            return false;
        }

        _api = (void**)api;

        if (Environment.GetEnvironmentVariable(CapturesEnvVar) is { Length: > 0 } template)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(template) ?? template);
            SetCaptureFilePathTemplate(template);
        }

        return true;
    }

    public static void TriggerCapture(uint frames)
    {
        if (frames <= 1)
            ((delegate* unmanaged[Cdecl]<void>)_api[FnTriggerCapture])();
        else
            ((delegate* unmanaged[Cdecl]<uint, void>)_api[FnTriggerMultiFrameCapture])(frames);
    }

    /// <summary>
    /// Brings the already connected replay UI forward, or launches a new one connected to this process.
    /// </summary>
    public static uint OpenReplayUI()
    {
        if (((delegate* unmanaged[Cdecl]<uint>)_api[FnIsTargetControlConnected])() != 0)
            return ((delegate* unmanaged[Cdecl]<uint>)_api[FnShowReplayUI])();

        return ((delegate* unmanaged[Cdecl]<uint, byte*, uint>)_api[FnLaunchReplayUI])(1, null);
    }

    public static List<string> GetCaptures()
    {
        var result = new List<string>();
        var count = ((delegate* unmanaged[Cdecl]<uint>)_api[FnGetNumCaptures])();
        var getCapture = (delegate* unmanaged[Cdecl]<uint, byte*, uint*, ulong*, uint>)_api[FnGetCapture];

        for (uint i = 0; i < count; i++)
        {
            uint length;
            if (getCapture(i, null, &length, null) == 0 || length == 0)
                continue;

            var buffer = new byte[length];
            fixed (byte* p = buffer)
                getCapture(i, p, &length, null);

            result.Add(Encoding.UTF8.GetString(buffer, 0, (int)length).TrimEnd('\0'));
        }

        return result;
    }

    private static void SetCaptureFilePathTemplate(string template)
    {
        var bytes = Encoding.UTF8.GetBytes(template + '\0');
        fixed (byte* p = bytes)
            ((delegate* unmanaged[Cdecl]<byte*, void>)_api[FnSetCaptureFilePathTemplate])(p);
    }

    private static IEnumerable<string> Candidates()
    {
        if (Environment.GetEnvironmentVariable(PathEnvVar) is { Length: > 0 } path)
            yield return path;

        if (OperatingSystem.IsWindows())
        {
            yield return "renderdoc.dll";
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RenderDoc", "renderdoc.dll");
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            yield return "librenderdoc.so";
        }
    }
}

using System;
using System.Linq;
using System.Threading;
using Robust.Shared._Starlight.Utility;
using Robust.Shared.Timing;
using Stopwatch = Robust.Shared.Timing.Stopwatch;

// ReSharper disable once CheckNamespace
namespace Robust.Client.Graphics;

/// <summary>
/// Manages the loading screen and its performance metrics during the loading phase of the application.
/// </summary>
internal sealed partial class LoadingScreenManager
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromSeconds(1 / 60.0);

    private static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(100);

    private readonly Stopwatch _frameSw = new();
    private readonly Stopwatch _totalSw = new();
    private TimeSpan _lastFrame;
    private TimeSpan _longestGap;
    private string? _longestGapSection;
    private int _frames;
    private int _stalls;
    private ThreadPriority _originalPriority;

    private void StarlightInitialize()
    {
        _totalSw.Restart();
        _frameSw.Restart();
        LoadingPump.Start(PumpFrame, PumpInterval);

        _originalPriority = Thread.CurrentThread.Priority;
        if (_originalPriority < ThreadPriority.AboveNormal)
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
    }

    private void PumpFrame()
    {
        if (_finished || !_clyde.IsInitialized)
            return;

        _clyde.ProcessInput(new FrameEventArgs((float) _frameSw.Elapsed.TotalSeconds));
        _clyde.Render();
    }

    private void StarlightOnFrame()
    {
        var now = _totalSw.Elapsed;
        var gap = now - _lastFrame;
        _lastFrame = now;
        _frameSw.Restart();

        if (_frames++ == 0)
            return;

        if (gap > StallThreshold)
            _stalls++;

        if (gap > _longestGap)
        {
            _longestGap = gap;
            _longestGapSection = _currentSectionName;
        }
    }

    private void StarlightFinish()
    {
        LoadingPump.Stop();
        Thread.CurrentThread.Priority = _originalPriority;

        var total = _totalSw.Elapsed;
        var slowest = string.Join(", ", Times
            .OrderByDescending(x => x.LoadTime)
            .Take(5)
            .Select(x => $"{x.Name} {x.LoadTime.TotalMilliseconds:N0}ms"));

        _sawmill.Debug(
            $"Loading took {total.TotalSeconds:N2}s, drew {_frames} frames ({_frames / Math.Max(total.TotalSeconds, 0.001):N0} fps avg), " +
            $"{_stalls} stalls over {StallThreshold.TotalMilliseconds:N0}ms, longest {_longestGap.TotalMilliseconds:N0}ms in '{_longestGapSection}'. " +
            $"Slowest sections: {slowest}");
    }
}

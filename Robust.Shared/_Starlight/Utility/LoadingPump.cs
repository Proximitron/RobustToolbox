using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Robust.Shared._Starlight.Utility;

/// <summary>
/// A utility for pumping a loading screen while waiting for a task to complete. The pump is started on the render thread, and then <see cref="Tick"/> is called from the render loop to draw frames. <see cref="Wait(Task)"/> and <see cref="RunBlocking(Action)"/> can be used to block until a task or action completes, drawing frames in the meantime.
/// </summary>
internal static class LoadingPump
{
    private static Action? _pump;
    private static int _threadId;
    private static long _interval;
    private static long _nextFrame;
    private static bool _inPump;

    public static bool Active => Volatile.Read(ref _pump) != null;

    private static bool OnRenderThread => Environment.CurrentManagedThreadId == _threadId;

    /// <summary>
    /// Starts pumping the loading screen. Must be called from the render thread.
    /// </summary>
    public static void Start(Action pump, TimeSpan interval)
    {
        _threadId = Environment.CurrentManagedThreadId;
        _interval = (long) (interval.TotalSeconds * Stopwatch.Frequency);
        _nextFrame = 0;
        Volatile.Write(ref _pump, pump);
    }

    public static void Stop()
        => Volatile.Write(ref _pump, null);

    /// <summary>
    /// Ticks the loading pump, drawing a frame if enough time has passed since the last frame. Must be called from the render thread.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Tick()
    {
        var pump = Volatile.Read(ref _pump);
        if (pump != null)
            TickSlow(pump);
    }

    private static void TickSlow(Action pump)
    {
        if (!OnRenderThread || _inPump)
            return;

        if (Stopwatch.GetTimestamp() < _nextFrame)
            return;

        _inPump = true;
        try
        {
            pump();
        }
        catch
        {
            // A broken loading screen must never break loading itself.
            Stop();
        }
        finally
        {
            _inPump = false;
            _nextFrame = Stopwatch.GetTimestamp() + _interval;
        }
    }

    /// <summary>
    /// Waits for <paramref name="task"/> to complete, drawing frames meanwhile.
    /// </summary>
    public static void Wait(Task task)
    {
        if (Active && OnRenderThread)
        {
            while (!task.IsCompleted)
            {
                Tick();
                ((IAsyncResult) task).AsyncWaitHandle.WaitOne(5);
            }
        }

        task.Wait();
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a background thread, blocking the current thread until it completes, drawing frames meanwhile. If the loading pump is not active or the current thread is not the render thread, <paramref name="action"/> is executed directly on the current thread.
    /// </summary>
    public static void RunBlocking(Action action)
    {
        if (!Active || !OnRenderThread)
        {
            action();
            return;
        }

        var task = Task.Run(action);
        while (!task.IsCompleted)
        {
            Tick();
            ((IAsyncResult) task).AsyncWaitHandle.WaitOne(5);
        }

        task.GetAwaiter().GetResult();
    }

    /// <inheritdoc cref="RunBlocking(Action)"/>
    public static T RunBlocking<T>(Func<T> func)
    {
        var result = default(T)!;
        RunBlocking(() => { result = func(); });
        return result;
    }
}

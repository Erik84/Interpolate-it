using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InterpolatIt.Core.Native;

public static unsafe partial class Dwm
{
    public const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAK = 13, DWMWA_CLOAKED = 14,
        DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_DEFAULT = 0, DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, uint attribute, void* value, uint size);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, void* value, uint size);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmFlush();

    /// <summary>The window's visible bounds in physical pixels (no invisible resize border, unlike GetWindowRect).</summary>
    public static bool TryGetExtendedFrameBounds(nint hwnd, out RECT rect)
    {
        RECT r;
        int hr = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &r, (uint)sizeof(RECT));
        rect = r;
        return hr >= 0 && !r.IsEmpty;
    }

    public static bool IsCloaked(nint hwnd)
    {
        int cloaked = 0;
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(int)) >= 0 && cloaked != 0;
    }

    /// <summary>Cloaks or uncloaks one of our own windows: instantly invisible without any window messages.</summary>
    public static void SetCloak(nint hwnd, bool cloak)
    {
        int value = cloak ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, &value, sizeof(int));
    }

    public static void SetCornerPreference(nint hwnd, int preference)
        => DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, &preference, sizeof(int));
}

public static unsafe partial class Kernel32
{
    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003;
    public const uint INFINITE = 0xFFFFFFFF;
    public const int ProcessPowerThrottling = 4;
    public const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4, PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version, ControlMask, StateMask;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWaitableTimer(nint timer, in long dueTime, int period, nint completion, nint arg,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForMultipleObjects(uint count, nint* handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds);

    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    public static partial nint GetModuleHandleW(nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessInformation(nint process, int infoClass, void* info, uint size);

    /// <summary>
    /// Windows 11 stops honouring timer resolution and may throttle a process whose windows are all occluded or
    /// minimised; this engine keeps presenting behind a control window that is often exactly that.
    /// </summary>
    public static void OptOutOfPowerThrottling()
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = 1,
            ControlMask = PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION | PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = 0,
        };
        SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, &state, (uint)sizeof(PROCESS_POWER_THROTTLING_STATE));
    }
}

public static partial class Gdi32
{
    public const int D3DKMT_SCHEDULINGPRIORITYCLASS_NORMAL = 2, D3DKMT_SCHEDULINGPRIORITYCLASS_ABOVE_NORMAL = 3,
        D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH = 4, D3DKMT_SCHEDULINGPRIORITYCLASS_REALTIME = 5;

    /// <summary>Sets the GPU scheduling priority class of a process (returns an NTSTATUS; 0 = success).</summary>
    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTSetProcessSchedulingPriorityClass(nint process, int priorityClass);
}

public static partial class Avrt
{
    [LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AvRevertMmThreadCharacteristics(nint handle);
}

public static partial class DComp
{
    /// <summary>Blocks until the compositor's next tick (the start of DWM's frame, at the display's vblank).</summary>
    [LibraryImport("dcomp.dll")]
    public static partial uint DCompositionWaitForCompositorClock(uint count, nint handles, uint timeoutInMs);
}

/// <summary>
/// One time base for everything: seconds on the QueryPerformanceCounter clock. Capture timestamps
/// (SystemRelativeTime), DXGI frame statistics (SyncQPCTime) and Stopwatch all live on it.
/// </summary>
public static class Qpc
{
    public static readonly double TicksToSeconds = 1.0 / Stopwatch.Frequency;

    public static double Now => Stopwatch.GetTimestamp() * TicksToSeconds;

    public static double FromTicks(long qpcTicks) => qpcTicks * TicksToSeconds;

    /// <summary>WinRT reports QPC time as a TimeSpan (100 ns units since the QPC epoch).</summary>
    public static double FromTimeSpan(TimeSpan relativeTime) => relativeTime.Ticks * 1e-7;
}

/// <summary>A waitable timer with sub-millisecond resolution for the short waits inside a refresh interval.</summary>
public sealed class HighResolutionTimer : IDisposable
{
    private nint _handle;

    public HighResolutionTimer()
    {
        _handle = Kernel32.CreateWaitableTimerExW(0, 0, Kernel32.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Kernel32.TIMER_ALL_ACCESS);
        if (_handle == 0) _handle = Kernel32.CreateWaitableTimerExW(0, 0, 0, Kernel32.TIMER_ALL_ACCESS);
        if (_handle == 0) throw new InvalidOperationException("CreateWaitableTimerEx failed");
    }

    /// <summary>Sleeps until the QPC clock reads <paramref name="time"/> seconds (returns at once if it is past).</summary>
    public void WaitUntil(double time)
    {
        double remaining = time - Qpc.Now;
        if (remaining <= 0) return;
        if (remaining > 0.0003)
        {
            long due = -(long)((remaining - 0.0002) * 1e7);     // relative, 100 ns units; wake slightly early
            if (Kernel32.SetWaitableTimer(_handle, in due, 0, 0, 0, false))
                Kernel32.WaitForSingleObject(_handle, Kernel32.INFINITE);
        }
        var spin = new SpinWait();
        while (Qpc.Now < time) spin.SpinOnce(-1);
    }

    /// <summary>
    /// Sleeps until the QPC clock reads <paramref name="time"/> or until <paramref name="other"/> (an event
    /// handle) is signalled, whichever comes first. Returns true when it was the handle.
    /// </summary>
    public unsafe bool WaitUntil(double time, nint other)
    {
        double remaining = time - Qpc.Now;
        if (remaining <= 0.00005) return Kernel32.WaitForSingleObject(other, 0) == 0;
        long due = -(long)(remaining * 1e7);
        if (!Kernel32.SetWaitableTimer(_handle, in due, 0, 0, 0, false))
            return Kernel32.WaitForSingleObject(other, (uint)Math.Max(1, remaining * 1000)) == 0;
        nint* handles = stackalloc nint[2] { other, _handle };
        return Kernel32.WaitForMultipleObjects(2, handles, false, Kernel32.INFINITE) == 0;
    }

    public void Dispose()
    {
        if (_handle != 0) Kernel32.CloseHandle(_handle);
        _handle = 0;
    }
}

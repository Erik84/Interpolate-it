using System.Runtime.InteropServices;
using InterpolatIt.Core.Native;

namespace InterpolatIt.Core.Capture;

/// <summary>What the target window is doing right now, as far as the overlay is concerned.</summary>
public readonly record struct TargetState(bool Exists, bool Minimized, bool Visible, bool InMoveSize, bool Hung,
    bool FillsMonitor, RECT Bounds)
{
    /// <summary>The overlay may cover the target: it is there, shown, responsive and not being dragged or resized.</summary>
    public bool Coverable => Exists && Visible && !Minimized && !InMoveSize && !Hung && !Bounds.IsEmpty;
}

/// <summary>
/// Polls the target window once per display refresh (a handful of cheap calls; no hooks, so nothing runs inside
/// the target process and nothing can be missed).
/// </summary>
public sealed class WindowTracker
{
    private readonly nint _hwnd;
    private readonly uint _threadId;

    public WindowTracker(nint hwnd)
    {
        _hwnd = hwnd;
        _threadId = User32.GetWindowThreadProcessId(hwnd, out _);
    }

    public nint Window => _hwnd;

    public TargetState Poll()
    {
        if (!User32.IsWindow(_hwnd)) return default;
        bool minimized = User32.IsIconic(_hwnd);
        bool visible = User32.IsWindowVisible(_hwnd) && !Dwm.IsCloaked(_hwnd);
        bool hung = User32.IsHungAppWindow(_hwnd);

        var gui = new GUITHREADINFO { Size = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        bool moving = User32.GetGUIThreadInfo(_threadId, ref gui) && (gui.Flags & User32.GUI_INMOVESIZE) != 0;

        // Visible bounds in physical pixels: this is also the origin and size of the captured picture.
        if (!Dwm.TryGetExtendedFrameBounds(_hwnd, out RECT bounds)) User32.GetWindowRect(_hwnd, out bounds);

        bool fills = false;
        nint monitor = User32.MonitorFromWindow(_hwnd, User32.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEXW { Size = (uint)Marshal.SizeOf<MONITORINFOEXW>() };
        if (monitor != 0 && User32.GetMonitorInfoW(monitor, ref info))
        {
            var m = info.Monitor;
            fills = bounds.Left <= m.Left && bounds.Top <= m.Top && bounds.Right >= m.Right && bounds.Bottom >= m.Bottom;
        }
        return new TargetState(true, minimized, visible, moving, hung, fills || User32.IsZoomed(_hwnd), bounds);
    }
}

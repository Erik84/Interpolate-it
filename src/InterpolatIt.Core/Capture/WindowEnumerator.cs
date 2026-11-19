using System.Diagnostics;
using System.Runtime.InteropServices;
using InterpolatIt.Core.Native;

namespace InterpolatIt.Core.Capture;

public sealed record WindowInfo(nint Handle, string Title, string ClassName, uint ProcessId, string ProcessName, RECT Bounds)
{
    public override string ToString() => $"0x{Handle:X} [{ProcessName}] {Title}";
}

/// <summary>Lists the top-level windows a user would expect in a "pick a window" list.</summary>
public static unsafe class WindowEnumerator
{
    public static IReadOnlyList<WindowInfo> GetCapturableWindows()
    {
        var handles = new List<nint>();
        var gch = GCHandle.Alloc(handles);
        try
        {
            User32.EnumWindows(&Collect, GCHandle.ToIntPtr(gch));
        }
        finally
        {
            gch.Free();
        }

        uint self = (uint)Environment.ProcessId;
        var result = new List<WindowInfo>();
        foreach (nint hwnd in handles)
        {
            if (!IsCapturable(hwnd)) continue;
            User32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == self) continue;
            var info = Describe(hwnd);
            if (info is not null) result.Add(info);
        }
        return result;
    }

    [UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint lParam)
    {
        ((List<nint>)GCHandle.FromIntPtr(lParam).Target!).Add(hwnd);
        return 1;
    }

    /// <summary>
    /// The usual capture-picker rules: a visible, enabled, titled root window that is not a tool window, not
    /// the shell and not cloaked (cloaked windows live on another virtual desktop or are suspended apps).
    /// </summary>
    public static bool IsCapturable(nint hwnd)
    {
        if (hwnd == 0 || hwnd == User32.GetShellWindow()) return false;
        if (!User32.IsWindowVisible(hwnd)) return false;
        if (User32.GetAncestor(hwnd, User32.GA_ROOT) != hwnd) return false;
        if ((User32.GetStyle(hwnd) & User32.WS_DISABLED) != 0) return false;
        if ((User32.GetExStyle(hwnd) & User32.WS_EX_TOOLWINDOW) != 0) return false;
        if (User32.GetWindowTextLengthW(hwnd) == 0) return false;
        if (Dwm.IsCloaked(hwnd)) return false;
        string cls = User32.GetClassName(hwnd);
        return cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow");
    }

    public static WindowInfo? Describe(nint hwnd)
    {
        if (!User32.IsWindow(hwnd)) return null;
        User32.GetWindowThreadProcessId(hwnd, out uint pid);
        string process = "";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            process = p.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        if (!Dwm.TryGetExtendedFrameBounds(hwnd, out RECT bounds)) User32.GetWindowRect(hwnd, out bounds);
        return new WindowInfo(hwnd, User32.GetWindowText(hwnd), User32.GetClassName(hwnd), pid, process, bounds);
    }

    /// <summary>First capturable window whose title contains <paramref name="text"/> (case-insensitive).</summary>
    public static WindowInfo? FindByTitle(string text)
        => GetCapturableWindows().FirstOrDefault(w => w.Title.Contains(text, StringComparison.OrdinalIgnoreCase));
}

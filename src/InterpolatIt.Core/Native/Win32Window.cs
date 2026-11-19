using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace InterpolatIt.Core.Native;

/// <summary>
/// A plain Win32 window owned by the creating thread. Messages go to <see cref="Handler"/>; returning null from
/// it falls through to DefWindowProc. The creating thread must pump messages (<see cref="PumpPending"/> or
/// <see cref="RunMessageLoop"/>).
/// </summary>
public sealed unsafe class Win32Window : IDisposable
{
    public delegate nint? MessageHandler(nint hwnd, uint message, nuint wParam, nint lParam);

    private static readonly ConcurrentDictionary<nint, Win32Window> Windows = new();
    private static readonly ConcurrentDictionary<string, bool> Classes = new();
    [ThreadStatic] private static Win32Window? t_creating;

    public nint Handle { get; private set; }
    public MessageHandler? Handler { get; set; }
    public uint ThreadId { get; }

    public Win32Window(string className, string title, uint style, uint exStyle, int x, int y, int width, int height,
        nint owner = 0, MessageHandler? handler = null)
    {
        Handler = handler;
        ThreadId = Kernel32.GetCurrentThreadId();
        nint instance = Kernel32.GetModuleHandleW(0);
        if (Classes.TryAdd(className, true))
        {
            nint name = Marshal.StringToHGlobalUni(className);      // must outlive the class: never freed
            var wc = new WNDCLASSEXW
            {
                Size = (uint)sizeof(WNDCLASSEXW),
                Style = User32.CS_HREDRAW | User32.CS_VREDRAW,
                WndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WndProc,
                Instance = instance,
                Cursor = User32.LoadCursorW(0, User32.IDC_ARROW),
                ClassName = name,
            };
            if (User32.RegisterClassExW(in wc) == 0)
                throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()})");
        }

        t_creating = this;
        try
        {
            Handle = User32.CreateWindowExW(exStyle, className, title, style, x, y, width, height, owner, 0, instance, 0);
        }
        finally
        {
            t_creating = null;
        }
        if (Handle == 0)
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()})");
        Windows[Handle] = this;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (!Windows.TryGetValue(hwnd, out var window)) window = t_creating;
        try
        {
            nint? handled = window?.Handler?.Invoke(hwnd, message, wParam, lParam);
            if (handled.HasValue) return handled.Value;
        }
        catch (Exception e)
        {
            // An exception must never unwind into the window manager.
            Console.Error.WriteLine($"window message handler failed: {e.GetType().Name}: {e.Message}");
        }
        if (message == User32.WM_DESTROY) Windows.TryRemove(hwnd, out _);
        return User32.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>Dispatches everything that is queued for this thread. Returns false once WM_QUIT was seen.</summary>
    public static bool PumpPending()
    {
        while (User32.PeekMessageW(out MSG msg, 0, 0, 0, User32.PM_REMOVE))
        {
            if (msg.Message == User32.WM_QUIT) return false;
            User32.TranslateMessage(in msg);
            User32.DispatchMessageW(in msg);
        }
        return true;
    }

    /// <summary>Blocks dispatching messages until WM_QUIT.</summary>
    public static void RunMessageLoop()
    {
        while (User32.GetMessageW(out MSG msg, 0, 0, 0) > 0)
        {
            User32.TranslateMessage(in msg);
            User32.DispatchMessageW(in msg);
        }
    }

    public void Dispose()
    {
        if (Handle != 0)
        {
            Windows.TryRemove(Handle, out _);
            User32.DestroyWindow(Handle);
            Handle = 0;
        }
    }
}

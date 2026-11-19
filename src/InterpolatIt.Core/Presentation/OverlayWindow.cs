using InterpolatIt.Core.Native;

namespace InterpolatIt.Core.Presentation;

/// <summary>
/// The window that shows the generated picture exactly on top of the target.
/// <list type="bullet">
/// <item>WS_POPUP owned by the target: it stays directly above the target in z-order (not above other apps), and
/// menus or tooltips the target opens later draw above it natively.</item>
/// <item>WS_EX_LAYERED | WS_EX_TRANSPARENT: mouse input falls through to the real window. Such a window is also
/// not counted as an occluder by Chromium and Firefox, so a browser keeps rendering underneath it.</item>
/// <item>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW: never takes the focus, not in the taskbar or Alt+Tab.</item>
/// <item>Alpha stays 255: anything less than fully opaque rules out direct flip.</item>
/// </list>
/// A cross-process owner attaches the two threads' input queues, so the window lives on its own thread that does
/// nothing but pump messages, and it is moved asynchronously: a hung target must not stall presentation.
/// It is hidden by cloaking (no window messages, instant), never by ShowWindow.
/// </summary>
public sealed class OverlayWindow : IDisposable
{
    private const uint WM_OVERLAY_QUIT = User32.WM_APP + 1;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _created = new();
    private Win32Window? _window;
    private Exception? _error;
    private volatile bool _cloaked = true;
    private RECT _bounds;

    public nint Handle { get; private set; }
    public nint Owner { get; }
    public bool IsCloaked => _cloaked;
    public RECT Bounds => _bounds;

    public OverlayWindow(nint owner, RECT bounds)
    {
        Owner = owner;
        _bounds = bounds;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "InterpolatIt overlay" };
        _thread.Start();
        _created.Wait();
        if (_error is not null) throw new InvalidOperationException("Could not create the overlay window: " + _error.Message, _error);
    }

    private void ThreadMain()
    {
        try
        {
            const uint exStyle = User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT | User32.WS_EX_NOACTIVATE
                                 | User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOREDIRECTIONBITMAP;
            _window = new Win32Window("InterpolatItOverlay", "Interpolate-it overlay", User32.WS_POPUP, exStyle,
                _bounds.Left, _bounds.Top, Math.Max(1, _bounds.Width), Math.Max(1, _bounds.Height), Owner, Handler);
            Handle = _window.Handle;
            User32.SetLayeredWindowAttributes(Handle, 0, 255, User32.LWA_ALPHA);
            Dwm.SetCloak(Handle, true);                                 // visible to the system, invisible on screen
            User32.ShowWindow(Handle, User32.SW_SHOWNOACTIVATE);
            User32.ShowWindow(Handle, User32.SW_SHOWNOACTIVATE);        // the first call can be overridden by STARTUPINFO
        }
        catch (Exception e)
        {
            _error = e;
        }
        finally
        {
            _created.Set();
        }
        if (_error is not null) return;
        Win32Window.RunMessageLoop();
        _window?.Dispose();
    }

    private nint? Handler(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case User32.WM_MOUSEACTIVATE: return User32.MA_NOACTIVATE;
            case User32.WM_NCHITTEST: return User32.HTTRANSPARENT;
            case User32.WM_ERASEBKGND: return 1;
            case WM_OVERLAY_QUIT:
                User32.PostQuitMessage(0);
                return 0;
        }
        return null;
    }

    /// <summary>Moves/resizes the overlay (physical pixels). Asynchronous: never waits for another thread.</summary>
    public void SetBounds(RECT bounds)
    {
        _bounds = bounds;
        User32.SetWindowPos(Handle, 0, bounds.Left, bounds.Top, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height),
            User32.SWP_NOACTIVATE | User32.SWP_NOZORDER | User32.SWP_NOOWNERZORDER | User32.SWP_ASYNCWINDOWPOS);
    }

    /// <summary>Cloaked = the real window shows through instantly; uncloaked = our picture covers it.</summary>
    public void SetCloaked(bool cloaked)
    {
        if (_cloaked == cloaked) return;
        _cloaked = cloaked;
        Dwm.SetCloak(Handle, cloaked);
    }

    /// <summary>
    /// Is the overlay really where the engine thinks it is? The engine has no other feedback on this: presents
    /// to a window that is cloaked, hidden or underneath its target are reported as shown all the same. Checks
    /// the window's actual state against the intended one, repairs what it can, and returns a description of
    /// what was wrong (null when everything is in order).
    /// </summary>
    public string? Verify()
    {
        if (Handle == 0) return null;
        string? problem = null;
        if (!User32.IsWindowVisible(Handle))
        {
            User32.ShowWindow(Handle, User32.SW_SHOWNOACTIVATE);
            problem = "it was hidden";
        }
        bool cloaked = Dwm.IsCloaked(Handle);
        if (cloaked != _cloaked)
        {
            Dwm.SetCloak(Handle, _cloaked);
            problem = cloaked ? "it was still cloaked" : "it was not cloaked";
        }
        if (!_cloaked && !IsAboveOwner())
        {
            // Directly above the target, not above everything: other applications' windows keep their place.
            nint above = User32.GetWindow(Owner, User32.GW_HWNDPREV);
            User32.SetWindowPos(Handle, above, 0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_ASYNCWINDOWPOS);
            problem = "it was underneath its target";
        }
        return problem;
    }

    private bool IsAboveOwner()
    {
        nint window = Owner;
        for (int i = 0; i < 64 && window != 0; i++)
        {
            window = User32.GetWindow(window, User32.GW_HWNDPREV);
            if (window == Handle) return true;
        }
        // Not found within reach above the target: walk down from the overlay to be sure it is below.
        window = Handle;
        for (int i = 0; i < 512 && window != 0; i++)
        {
            window = User32.GetWindow(window, User32.GW_HWNDNEXT);
            if (window == Owner) return true;
        }
        return false;
    }

    /// <summary>Rounded corners like the target's (Windows 11 rounds framed windows), or square when it fills the screen.</summary>
    public void SetRoundedCorners(bool rounded)
        => Dwm.SetCornerPreference(Handle, rounded ? Dwm.DWMWCP_ROUND : Dwm.DWMWCP_DONOTROUND);

    public void Dispose()
    {
        if (Handle != 0)
        {
            Dwm.SetCloak(Handle, true);
            User32.PostMessageW(Handle, WM_OVERLAY_QUIT, 0, 0);
            if (!_thread.Join(2000))
                Console.Error.WriteLine("overlay thread did not exit in time");
            Handle = 0;
        }
        _created.Dispose();
    }
}

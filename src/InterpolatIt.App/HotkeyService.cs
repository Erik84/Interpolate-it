using System.Windows;
using System.Windows.Interop;
using InterpolatIt.Core.Native;

namespace InterpolatIt.App;

/// <summary>System-wide hotkeys delivered to a WPF window (they work while another application has the focus).</summary>
public sealed class HotkeyService : IDisposable
{
    public const uint Alt = 0x1, Control = 0x2, Shift = 0x4, NoRepeat = 0x4000;

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = [];
    private int _nextId = 1;

    public HotkeyService(Window window)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle())
                  ?? throw new InvalidOperationException("The window has no handle yet.");
        _source.AddHook(Hook);
    }

    /// <summary>Registers a hotkey; returns false when another application already owns the combination.</summary>
    public bool Register(uint modifiers, uint virtualKey, Action action)
    {
        int id = _nextId++;
        if (!User32.RegisterHotKey(_source.Handle, id, modifiers | NoRepeat, virtualKey)) return false;
        _actions[id] = action;
        return true;
    }

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == (int)User32.WM_HOTKEY && _actions.TryGetValue((int)wParam, out var action))
        {
            action();
            handled = true;
        }
        return 0;
    }

    public void Dispose()
    {
        foreach (int id in _actions.Keys) User32.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
        _source.RemoveHook(Hook);
    }
}

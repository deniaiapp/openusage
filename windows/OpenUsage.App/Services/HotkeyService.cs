using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using OpenUsage.Core.Support;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace OpenUsage.App.Services;

public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x4F55; // "OU"
    private const int WmHotkey = 0x0312;
    private HwndSource? _source;
    private bool _registered;

    public event Action? Activated;

    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(Hook);
    }

    public void Apply(string? gesture)
    {
        Unregister();
        if (string.IsNullOrWhiteSpace(gesture) || _source is null) return;
        if (!TryParse(gesture, out var modifiers, out var vk))
        {
            AppLog.Warn("hotkey", $"could not parse shortcut '{gesture}'");
            return;
        }
        _registered = RegisterHotKey(_source.Handle, HotkeyId, modifiers | 0x4000, vk);
        if (!_registered)
            AppLog.Warn("hotkey", "RegisterHotKey failed — the combo may already be in use");
    }

    public static string? Capture(KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System)
            return null;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var parts = new List<string>();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        if (parts.Count == 0) return null;
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        Key? key = null;
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control": modifiers |= 2; break;
                case "alt": modifiers |= 1; break;
                case "shift": modifiers |= 4; break;
                case "win":
                case "windows": modifiers |= 8; break;
                default:
                    if (Enum.TryParse<Key>(part, true, out var parsed)) key = parsed;
                    break;
            }
        }
        if (key is null) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
        return vk != 0 && modifiers != 0;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Activated?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void Unregister()
    {
        if (_registered && _source is not null)
            UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(Hook);
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

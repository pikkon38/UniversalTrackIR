using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace UniversalTrackIR.Services;

/// <summary>A key plus modifiers, stored as text like "Ctrl+Shift+F12".</summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public bool IsEmpty => Key == Key.None;

    public override string ToString()
    {
        if (IsEmpty) return "None";
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "None") return default;
        var mods = ModifierKeys.None;
        Key key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win": mods |= ModifierKeys.Windows; break;
                default:
                    if (Enum.TryParse(raw, true, out Key k)) key = k;
                    else if (raw.Length == 1 && char.IsDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
                    break;
            }
        }
        return new Hotkey(mods, key);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        _ => key.ToString(),
    };
}

/// <summary>
/// System-wide hotkeys through RegisterHotKey, so they work while the game has focus.
/// Windows hands the key to us instead of the game, so pick combos the game doesn't use.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0xB000;

    public HotkeyService(IntPtr hwnd)
    {
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("No window for hotkeys.");
        _source.AddHook(WndProc);
    }

    /// <summary>Replaces all hotkeys. Returns the ones Windows refused (usually taken by another app).</summary>
    public List<Hotkey> SetAll(IEnumerable<(Hotkey key, Action action)> bindings)
    {
        Clear();
        var failed = new List<Hotkey>();
        foreach (var (key, action) in bindings)
        {
            if (key.IsEmpty) continue;
            int id = _nextId++;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key.Key);
            if (RegisterHotKey(_source.Handle, id, ToNative(key.Modifiers) | MOD_NOREPEAT, vk))
                _actions[id] = action;
            else
                failed.Add(key);
        }
        return failed;
    }

    public void Clear()
    {
        foreach (int id in _actions.Keys.ToList()) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static uint ToNative(ModifierKeys m) =>
        (m.HasFlag(ModifierKeys.Alt) ? MOD_ALT : 0) |
        (m.HasFlag(ModifierKeys.Control) ? MOD_CONTROL : 0) |
        (m.HasFlag(ModifierKeys.Shift) ? MOD_SHIFT : 0) |
        (m.HasFlag(ModifierKeys.Windows) ? MOD_WIN : 0);

    public void Dispose()
    {
        Clear();
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

using System.Runtime.InteropServices;
using ReplayRecorder.Core;

namespace ReplayRecorder.Recorder;

/// <summary>Global hotkeys via RegisterHotKey (no keyboard hook, nothing touches the game).</summary>
sealed class Hotkeys : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    readonly Dictionary<int, Action> _actions = new();
    int _nextId = 1;

    public Hotkeys() => CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */ });

    /// <summary>Registers e.g. "Alt+F10" or "Ctrl+Shift+S". Returns false if taken or invalid.</summary>
    public bool Register(string combo, Action action)
    {
        if (string.IsNullOrWhiteSpace(combo)) return false;
        if (!TryParse(combo, out var mods, out var key))
        {
            Log.Warn($"Hotkey \"{combo}\" is not valid");
            return false;
        }
        int id = _nextId++;
        if (!RegisterHotKey(Handle, id, mods | MOD_NOREPEAT, (uint)key))
        {
            Log.Warn($"Hotkey \"{combo}\" is already used by another app");
            return false;
        }
        _actions[id] = Logged(combo, action);

        // On layouts with AltGr (Turkish, German, ...) the right Alt key reaches Windows as
        // Ctrl+Alt, so "Alt+X" pressed with the right Alt would never match. Register that too.
        if (mods == MOD_ALT)
        {
            int altGrId = _nextId++;
            if (RegisterHotKey(Handle, altGrId, MOD_ALT | MOD_CONTROL | MOD_NOREPEAT, (uint)key))
                _actions[altGrId] = Logged(combo + " (right Alt)", action);
        }
        Log.Info($"Hotkey {combo} registered (key code 0x{(int)key:X2})");
        return true;
    }

    static Action Logged(string combo, Action action) => () =>
    {
        Log.Info($"Hotkey pressed: {combo}");
        action();
    };

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(Handle, id);
        _actions.Clear();
    }

    /// <summary>
    /// A single punctuation character ("." "," "-" ...) means "the key that types it on the
    /// current keyboard layout", so "Alt+." is the period key on Turkish Q and US layouts alike.
    /// </summary>
    static bool TryCharKey(string raw, out Keys key)
    {
        key = Keys.None;
        if (raw.Length != 1 || char.IsLetterOrDigit(raw[0])) return false;
        short r = VkKeyScan(raw[0]);
        if (r == -1 || (r >> 8 & 0xFF) != 0) return false; // not typeable without Shift/AltGr
        key = (Keys)(r & 0xFF);
        return true;
    }

    public static bool TryParse(string combo, out uint mods, out Keys key)
    {
        mods = 0; key = Keys.None;
        // "Alt++" style combos: a trailing '+' after a separator is the plus key itself.
        if (combo.EndsWith("++")) combo = combo[..^2] + "+=";
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "alt": mods |= MOD_ALT; break;
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default:
                    if (TryCharKey(raw, out var k)) key = k;
                    else if (!Enum.TryParse(raw, true, out key)) return false;
                    break;
            }
        }
        return key != Keys.None;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _actions.TryGetValue((int)m.WParam, out var a)) a();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern short VkKeyScan(char ch);
}

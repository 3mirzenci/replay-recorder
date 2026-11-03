using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ReplayRecorder.Library.SettingsUi;

/// <summary>
/// Click, then press a key combination to record a hotkey ("Alt+.", "Ctrl+Shift+F10").
/// Esc cancels, Backspace/Delete clears (when clearing is allowed).
/// The text format matches what the recorder parses.
/// </summary>
sealed class HotkeyBox : Border
{
    readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
    string _value = "";
    bool _listening;

    public bool AllowEmpty { get; set; }
    public event Action? Changed;

    public string Value
    {
        get => _value;
        set { _value = value ?? ""; Render(); }
    }

    public HotkeyBox()
    {
        Child = _text;
        Focusable = true;
        Cursor = Cursors.Hand;
        Height = 32;
        Padding = new Thickness(11, 0, 11, 0);
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        Background = (Brush)Application.Current.FindResource("Card");
        BorderBrush = (Brush)Application.Current.FindResource("Line");
        MouseLeftButtonDown += (_, e) => { Focus(); _listening = true; Render(); e.Handled = true; };
        LostKeyboardFocus += (_, _) => { _listening = false; Render(); };
        Render();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_listening) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { Stop(); return; }
        if ((key == Key.Back || key == Key.Delete) && Keyboard.Modifiers == ModifierKeys.None && AllowEmpty)
        {
            _value = ""; Stop(); Changed?.Invoke(); return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed) return; // wait for the real key

        var mods = Keyboard.Modifiers;
        // AltGr arrives as Ctrl+Alt; the recorder accepts both Alt keys anyway, so store plain Alt.
        if (Keyboard.IsKeyDown(Key.RightAlt)) mods &= ~ModifierKeys.Control;
        if (mods == ModifierKeys.None && !(key >= Key.F1 && key <= Key.F24)) return; // a bare letter would fire while typing in chat

        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(key));
        _value = string.Join('+', parts);
        Stop();
        Changed?.Invoke();
    }

    void Stop() { _listening = false; Render(); Keyboard.ClearFocus(); }

    /// <summary>Punctuation keys are stored as the character they type on this layout (".", "-", ...).</summary>
    static string KeyName(Key key)
    {
        int vk = KeyInterop.VirtualKeyFromKey(key);
        bool oem = (vk >= 0xBA && vk <= 0xC0) || (vk >= 0xDB && vk <= 0xDF) || vk == 0xE2;
        if (oem)
        {
            uint ch = MapVirtualKey((uint)vk, 2 /* MAPVK_VK_TO_CHAR */) & 0x7FFF;
            if (ch > 32 && ch != '+') return ((char)ch).ToString();
        }
        return key.ToString(); // F10, A, D5, NumPad1... same names as the recorder's parser
    }

    void Render()
    {
        if (_listening)
        {
            _text.Text = AllowEmpty ? "Press keys…  (Esc cancel, Backspace clear)" : "Press keys…  (Esc cancel)";
            _text.Foreground = (Brush)Application.Current.FindResource("Accent");
            BorderBrush = (Brush)Application.Current.FindResource("Accent");
        }
        else
        {
            _text.Text = _value.Length > 0 ? _value.Replace("+", " + ") : "Not set";
            _text.Foreground = (Brush)Application.Current.FindResource(_value.Length > 0 ? "Text" : "Subtle");
            BorderBrush = (Brush)Application.Current.FindResource("Line");
        }
    }

    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
}

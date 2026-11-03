using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ReplayRecorder.Library.Player;

/// <summary>
/// A plain child HWND for mpv to render into. It is created disabled so that clicking the
/// video never moves keyboard focus away from the WPF window (shortcuts keep working).
/// </summary>
sealed class VideoHost : HwndHost
{
    const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_DISABLED = 0x08000000;

    public IntPtr Hwnd { get; private set; }
    public event Action? Created;

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        Hwnd = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_DISABLED,
            0, 0, 16, 9, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Dispatcher.BeginInvoke(() => Created?.Invoke());
        return new HandleRef(this, Hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
}

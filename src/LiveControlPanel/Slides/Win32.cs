using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace LiveControlPanel.Slides;

public sealed record WindowInfo(long Handle, string ClassName, string Title, bool Visible);

/// <summary>
/// P/Invoke surface for window discovery and keystroke delivery (FR 5.3).
///
/// Classic <c>DllImport</c> rather than the source-generated <c>LibraryImport</c>: these signatures
/// need <see cref="StringBuilder"/> out-buffers and IUnknown marshalling, neither of which the
/// generator supports.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Win32
{
    internal const uint WM_KEYDOWN = 0x0100;
    internal const uint WM_KEYUP = 0x0101;

    internal const ushort VK_LEFT = 0x25;
    internal const ushort VK_RIGHT = 0x27;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] INPUT[] inputs, int size);

    /// <summary>Synthesizes a key press at the input-queue level; goes to whatever has focus.</summary>
    internal static void SendKey(ushort virtualKey)
    {
        var inputs = new[]
        {
            new INPUT
            {
                Type = INPUT_KEYBOARD,
                Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = virtualKey } },
            },
            new INPUT
            {
                Type = INPUT_KEYBOARD,
                Data = new InputUnion
                {
                    Keyboard = new KEYBDINPUT { VirtualKey = virtualKey, Flags = KEYEVENTF_KEYUP },
                },
            },
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    internal static List<WindowInfo> EnumerateTopLevelWindows()
    {
        var windows = new List<WindowInfo>();

        EnumWindows((hWnd, _) =>
        {
            var className = new StringBuilder(256);
            GetClassName(hWnd, className, className.Capacity);

            var title = new StringBuilder(512);
            GetWindowText(hWnd, title, title.Capacity);

            windows.Add(new WindowInfo(
                hWnd.ToInt64(),
                className.ToString(),
                title.ToString(),
                IsWindowVisible(hWnd)));

            return true;
        }, IntPtr.Zero);

        return windows;
    }
}

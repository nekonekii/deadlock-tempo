using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Text;

namespace DeadlockTempo
{
    // Only this application's window styles are changed. The foreground game's
    // title and client bounds are queried solely to select visible screen pixels.
    internal static class Native
    {
        internal const int GWL_EXSTYLE = -20;
        internal const long Transparent = 0x20, Layered = 0x80000, NoActivate = 0x08000000, ToolWindow = 0x80;
        [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW", SetLastError=true)] private static extern IntPtr Get64(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint="GetWindowLongW", SetLastError=true)] private static extern int Get32(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW", SetLastError=true)] private static extern IntPtr Set64(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint="SetWindowLongW", SetLastError=true)] private static extern int Set32(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll", SetLastError=true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", SetLastError=true)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint key);
        [DllImport("user32.dll", SetLastError=true)] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct Origin { public int X, Y; }
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int length);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Bounds rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Origin point);
        internal static bool GameBounds(out IntPtr hwnd, out System.Drawing.Rectangle rect)
        {
            hwnd = GetForegroundWindow(); rect = System.Drawing.Rectangle.Empty;
            var title = new StringBuilder(256); GetWindowText(hwnd, title, title.Capacity);
            if (!string.Equals(title.ToString().Trim(), "Deadlock", StringComparison.OrdinalIgnoreCase)) return false;
            Bounds bounds; var origin = new Origin();
            if (!GetClientRect(hwnd, out bounds) || !ClientToScreen(hwnd, ref origin)) return false;
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            if (width < 800 || height < 600 || width > 10000 || height > 5000) return false;
            rect = new System.Drawing.Rectangle(origin.X, origin.Y, width, height);
            return System.Windows.Forms.SystemInformation.VirtualScreen.Contains(rect);
        }
        internal static long GetStyle(IntPtr hwnd) { return IntPtr.Size == 8 ? Get64(hwnd, GWL_EXSTYLE).ToInt64() : Get32(hwnd, GWL_EXSTYLE); }
        internal static void KeepOnTop(Window window)
        {
            SetWindowPos(new WindowInteropHelper(window).Handle, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
        }
        internal static void OverlayStyle(Window window, bool editing)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            long style = GetStyle(hwnd) | Layered | ToolWindow | NoActivate;
            style = editing ? style & ~Transparent : style | Transparent;
            if (IntPtr.Size == 8) Set64(hwnd, GWL_EXSTYLE, new IntPtr(style)); else Set32(hwnd, GWL_EXSTYLE, (int)style);
            if (!SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x20)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((GetStyle(hwnd) & (Layered | NoActivate | ToolWindow | Transparent)) != (style & (Layered | NoActivate | ToolWindow | Transparent)))
                throw new InvalidOperationException("Windows не применила режим оверлея.");
        }
    }
}

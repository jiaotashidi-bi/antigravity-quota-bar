using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace GeminiQuota
{
    internal static class Native
    {
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr order, int x, int y, int w, int h, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowRect { public int Left, Top, Right, Bottom; }

        public static void EnableDpi() { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        public static float Scale(IntPtr hwnd) { return Math.Max(96, GetDpiForWindow(hwnd)) / 96f; }
        public static Rectangle Bounds(IntPtr hwnd)
        {
            WindowRect r;
            return GetWindowRect(hwnd, out r) ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : Rectangle.Empty;
        }
        public static bool IsAntigravity(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || IsIconic(hwnd)) return false;
            int pid;
            GetWindowThreadProcessId(hwnd, out pid);
            try
            {
                using (var process = Process.GetProcessById(pid))
                    return String.Equals(process.ProcessName, "Antigravity", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
        public static void SetOwner(IntPtr hwnd, IntPtr owner)
        {
            // A non-topmost owned window stays above the composer and follows its owner's z-order.
            SetWindowLongPtr(hwnd, -8, owner);
        }
        public static void Place(IntPtr hwnd, Rectangle r)
        {
            SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, 0x0010 | 0x0200);
        }
    }
}

using System;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Diagnostics;
//using Microsoft.VisualBasic.Devices;
using System.Threading;

namespace MDDFoundation
{
    public enum KMMouseButton
    {
        None, Left, Right
    }
    public class KMMouseEventArgs : EventArgs
    {
        public KMMouseButton Button { get; set; }
        //public int ClickCount { get; set; }
        public Point Location { get; set; }
        public short MouseDelta { get; set; }
        public bool Handled { get; set; }
    }
    public class KMKeyboardEventArgs : EventArgs
    {
        public int VirtualKeyCode { get; set; }
        public bool Handled { get; set; }
    }
    public interface IKeyboard
    {
        void SendKeys(string str);
    }
    public static partial class KeyMouse
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool LockWorkStation();
        public static IKeyboard Keyboard { get; set; }
        public static bool SendKeysExt(string text, int processid = 0, int inactivems = 1000, bool wait = true, IntPtr switchPtr = default)
        {
            if (PrepareForInput(inactivems, wait, switchPtr, processid))
            {
                if (Keyboard == null) throw new Exception("please inject dependency for keyboard");
                Keyboard.SendKeys(text);
                return true;
            }
            return false;
        }
        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("user32", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "FindWindow", SetLastError = true)]
        public static extern IntPtr FindWindowByCaption(IntPtr ZeroOnly, string lpWindowName);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, int dwExtraInfo);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetCursorPos(int x, int y);
        /// <summary>
        /// Specify either hwnd or processid (hwnd will take precedence)
        /// </summary>
        /// <param name="hwnd"></param>
        /// <param name="processid"></param>
        /// <param name="inactivems"></param>
        /// <param name="wait"></param>
        /// <returns></returns>
        public static IntPtr SetForegroundWindow(IntPtr hwnd = default, int processid = 0, int inactivems = 1000, bool wait = true)
        {
            var inactive = GetInactiveTime().TotalMilliseconds;
            if (hwnd == default)
            {
                var p = Process.GetProcessById(processid);
                if (p != null) hwnd = FindWindowByCaption(IntPtr.Zero, p.MainWindowTitle);
            }
            if (hwnd == default) throw new Exception("Unable to find Window Handle");
            if (wait && inactive < inactivems)
            {
                while (inactive < inactivems)
                {
                    Thread.Sleep(100);
                    inactive = GetInactiveTime().TotalMilliseconds;
                }
            }
            if (SetForegroundWindow(hwnd)) return hwnd;
            return IntPtr.Zero;
        }
        public static List<Process> FindProcess(string processsearch)
        {
            return Process.GetProcesses()
                .Where(x =>
                    x.ProcessName.IndexOf(processsearch, StringComparison.OrdinalIgnoreCase) != -1
                    || x.MainWindowTitle.IndexOf(processsearch, StringComparison.OrdinalIgnoreCase) != -1
                ).ToList();
        }
        public static Process GetProcessByHandle(IntPtr hWnd)
        {
            GetWindowThreadProcessId(hWnd, out uint uipid);
            Process p = Process.GetProcessById((int)uipid);
            return p;
        }
        public static bool LockWorkStation(int processid = 0, int inactivems = 1000, bool wait = true, IntPtr switchPtr = default)
        {
            if (PrepareForInput(inactivems, wait, switchPtr, processid))
            {
                return LockWorkStation();
            }
            return false;
        }
        //private static Keyboard keyboard;
        //public static bool SendKeysExt(string text, int processid = 0, int inactivems = 1000, bool wait = true, IntPtr switchPtr = default)
        //{
        //    if (PrepareForInput(inactivems, wait, switchPtr, processid))
        //    {
        //        keyboard = keyboard ?? new Keyboard();
        //        keyboard.SendKeys(text);
        //        return true;
        //    }
        //    return false;
        //}

        private static bool PrepareForInput(int inactivems, bool wait, IntPtr switchPtr, int processid)
        {
            var inactive = GetInactiveTime().TotalMilliseconds;
            if (wait && inactive < inactivems)
            {
                while (inactive < inactivems)
                {
                    Thread.Sleep(100);
                    inactive = GetInactiveTime().TotalMilliseconds;
                    if (switchPtr != default && inactive > (inactivems * 0.75) && switchPtr != GetForegroundWindow()) SetForegroundWindow(switchPtr);
                }
            }
            if (switchPtr != default && switchPtr != GetForegroundWindow()) SetForegroundWindow(switchPtr);
            if (GetInactiveTime().TotalMilliseconds >= inactivems)
            {
                uint pid = 0;
                if (processid != pid) GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                if (pid == processid)
                {
                    return true;
                }
            }
            return false;
        }

        public static bool DoMouseClick(int X, int Y, int processid = 0, int inactivems = 1000, bool wait = true, IntPtr switchPtr = default)
        {
            if (PrepareForInput(inactivems, wait, switchPtr, processid))
            {
                SetCursorPos(X, Y);
                Thread.Sleep(100);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 1);
                Thread.Sleep(100);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 1);
                return true;
            }
            return false;
        }
        public static bool DoMouseMove(int X, int Y, int processid = 0, int inactivems = 1000, bool wait = true, IntPtr switchPtr = default)
        {
            if (PrepareForInput(inactivems, wait, switchPtr, processid))
            {
                SetCursorPos(X, Y);
                return true;
            }
            return false;
        }
        public static bool DoMouseDoubleClick(int X, int Y, int processid = 0, int inactivems = 1000, bool wait = true, IntPtr switchPtr = default)
        {
            if (PrepareForInput(inactivems, wait, switchPtr, processid))
            {
                SetCursorPos(X, Y);
                Thread.Sleep(100);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 1);
                Thread.Sleep(75);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 1);
                Thread.Sleep(75);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 1);
                Thread.Sleep(75);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 1);
                return true;
            }
            return false;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }
        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
        public static TimeSpan GetInactiveTime()
        {
            LASTINPUTINFO info = new LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(info);
            if (GetLastInputInfo(ref info))
                return TimeSpan.FromMilliseconds((uint)Environment.TickCount - info.dwTime);
            else
                return TimeSpan.FromMilliseconds(0);
        }
        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern Int32 ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern uint GetPixel(IntPtr hdc, int nXPos, int nYPos);
        public static Color GetPixelColor(int x, int y)
        {
            IntPtr hdc = GetDC(IntPtr.Zero);
            uint pixel = GetPixel(hdc, x, y);
            ReleaseDC(IntPtr.Zero, hdc);
            Color color__1 = Color.FromArgb(System.Convert.ToInt32(pixel & 0xFF), System.Convert.ToInt32(pixel & 0xFF00) >> 8, System.Convert.ToInt32(pixel & 0xFF0000) >> 16);
            return color__1;
        }
        const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        const uint MOUSEEVENTF_LEFTUP = 0x0004;
        const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        const uint MOUSEEVENTF_MOVE = 0x0001;
        const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        const uint MOUSEEVENTF_XDOWN = 0x0080;
        const uint MOUSEEVENTF_XUP = 0x0100;
        const uint MOUSEEVENTF_WHEEL = 0x0800;
        const uint MOUSEEVENTF_HWHEEL = 0x01000;
    }
}

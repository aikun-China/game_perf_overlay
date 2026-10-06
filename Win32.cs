using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PerfMonitor
{
    // Focused P/Invoke surface. user32 for window positioning / no-activate,
    // kernel32 for memory, winmm for timer resolution, user32 EnumDisplaySettings
    // for the per-screen refresh-rate fallback used by the FPS monitor.
    internal static class Win32
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;     // 0..100 percent of physical memory in use
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        // DEVMODE: we only need dmSize and dmDisplayFrequency; place fields at their
        // documented offsets so the rest of the (large) struct is passed as a buffer.
        [StructLayout(LayoutKind.Explicit, Size = 220, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [FieldOffset(0)]   [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            [FieldOffset(68)]  public ushort dmSize;
            [FieldOffset(72)]  public uint dmFields;
            [FieldOffset(184)] public uint dmDisplayFrequency;
        }

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOPMOST = 0x00000008;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public const uint ENUM_CURRENT_SETTINGS = 0xFFFFFFFF;
        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder className, int maxCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseKernelHandle(IntPtr handle);

        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

        public static HashSet<int> GetProcessTree(int rootProcessId)
        {
            HashSet<int> result = new HashSet<int>();
            if (rootProcessId <= 0) return result;
            result.Add(rootProcessId);

            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == INVALID_HANDLE_VALUE)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                List<PROCESSENTRY32> entries = new List<PROCESSENTRY32>();
                PROCESSENTRY32 entry = new PROCESSENTRY32();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (!Process32First(snapshot, ref entry))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                do
                {
                    entries.Add(entry);
                    entry = new PROCESSENTRY32();
                    entry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                }
                while (Process32Next(snapshot, ref entry));

                bool added;
                do
                {
                    added = false;
                    foreach (PROCESSENTRY32 process in entries)
                    {
                        if (result.Contains(unchecked((int)process.th32ParentProcessID))
                            && result.Add(unchecked((int)process.th32ProcessID)))
                            added = true;
                    }
                }
                while (added);
            }
            finally
            {
                CloseKernelHandle(snapshot);
            }
            return result;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public static IntPtr GetWindowLongCompat(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size != 8) return IntPtr.Zero;
            return GetWindowLongPtr64(hWnd, nIndex);
        }

        public static IntPtr SetWindowLongCompat(IntPtr hWnd, int nIndex, IntPtr value)
        {
            if (IntPtr.Size != 8) return IntPtr.Zero;
            return SetWindowLongPtr64(hWnd, nIndex, value);
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplaySettingsW(string lpszDeviceName, uint dwMode, ref DEVMODE lpDevMode);

        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint uPeriod);

        // Add WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW so the overlay never steals focus
        // and never appears in the taskbar/Alt-Tab list.
        public static void MakeOverlayNoActivate(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            IntPtr ex = GetWindowLongCompat(hwnd, GWL_EXSTYLE);
            IntPtr newEx = (IntPtr)((long)ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            SetWindowLongCompat(hwnd, GWL_EXSTYLE, newEx);
        }
    }
}

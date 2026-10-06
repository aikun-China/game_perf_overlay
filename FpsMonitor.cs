using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ConstrainedExecution;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PerfMonitor
{
    // FPS:
    //   FOCUSED WINDOW: Intel PresentMon ETW events for the focused process on the selected display.
    //   DESKTOP: DXGI desktop duplication, with DWM / DXGI statistics fallbacks.
    //   PRIMARY: IDXGIOutput::GetFrameStatistics for the selected display output.
    //   FALLBACK: DWM composition timing for systems where DXGI statistics are unavailable.
    //
    // The DWM backend reads DWM_TIMING_INFO.ullPresentCount per HMONITOR. This counter
    // increments once for every real present DWM sends to the monitor's scanout. On
    // Windows 10/11 this directly corresponds to the number of new desktop frames
    // composed per second (= "desktop FPS") which matches what the user sees when
    // scrolling documents, watching videos, playing windowed games, etc.
    internal sealed class FpsMonitor : IDisposable
    {
        // ---- Fine-grained trace logger (always writes file before/after each COM call)
        private static readonly object _traceLock = new object();
        private static void FpsTrace(string msg)
        {
            try
            {
                lock (_traceLock)
                {
                    SessionFiles.AppendLog(
                        "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "][T" + Thread.CurrentThread.ManagedThreadId + "] " + msg + "\r\n");
                }
            }
            catch { }
        }

        // ---- Win32 / DWM interop (fallback FPS backend) --------------------------
        private const int MONITOR_DEFAULTTONULL = 0;
        private const int MONITOR_DEFAULTTOPRIMARY = 1;
        private const int MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private class MONITORINFOEX
        {
            public int cbSize;
            public Win32.RECT rcMonitor;
            public Win32.RECT rcWork;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
            public MONITORINFOEX() { cbSize = 104; } // C++ sizeof(MONITORINFOEXW) on x64
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref Win32.RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, [In, Out] MONITORINFOEX lpmi);

        // Tiny anchor HWND management for DWM. DwmGetCompositionTimingInfo takes
        // an HWND (NOT an HMONITOR) and returns timing for the monitor that HWND
        // lives on. We create a single 1x1 transparent layered message-only-style
        // window per FpsMonitor, and simply MoveWindow it onto the target monitor's
        // rect whenever the target changes. Zero COM involvement.
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_NOZORDER = 0x0004;
        private const int SWP_NOACTIVATE = 0x0010;
        private const int SWP_SHOWWINDOW = 0x0040;
        private const int LWA_ALPHA = 0x00000002;
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
        private const int CW_USEDEFAULT = unchecked((int)0x80000000);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassW([In] ref WNDCLASSEXW lpWndClass);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // DwmGetCompositionTimingInfo — first argument is HWND (not HMONITOR).
        // DWM returns the timing info for the monitor on which that HWND resides.
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmGetCompositionTimingInfo(IntPtr hWnd, IntPtr pTimingInfo);

        // DWM_TIMING_INFO is large (>= 360 bytes on Win10) and its field offsets are
        // stable within a Windows major release. We don't need every field — only the
        // 64-bit present counter. According to MS public symbols the two ULONGLONG
        // counters we care about sit at the following offsets from the start of
        // DWM_TIMING_INFO on Windows 8.1 / 10 / 11:
        //   offset 0x58 (88):  ULONGLONG ullVBlankCount;       // vblank interrupt counter
        //   offset 0x60 (96):  ULONGLONG ullPresentCount;      // number of real presents to scanout
        //   offset 0x68 (104): ULONGLONG ullCompositionCount;  // DWM compositions run
        // All three monotonically increase. We use ullPresentCount (0x60) as our
        // "desktop FPS" ground truth — DWM increments it once per scan-out when a
        // new frame actually appears on the monitor.
        private const int DWM_TIMINGINFO_SIZE = 512;     // generous, oversized on purpose
        private const int DWM_TI_PRESENTCOUNT_OFFSET = 0x60;

        private static readonly Guid IID_IUnknown = new Guid(0x00000000, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46);
        // IDXGIOutput1 (adds DuplicateOutput).
        private static readonly Guid IID_IDXGIOutput1 = new Guid(0x00CDDEA8, 0x939B, 0x4B83, 0xA3, 0x40, 0xA6, 0x85, 0x22, 0x66, 0x66, 0xCC);

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

        [DllImport("d3d11.dll", PreserveSig = true)]
        private static extern int D3D11CreateDevice(
            IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
            IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
            out IntPtr ppDevice, IntPtr pFeatureLevel, IntPtr ppImmediateContext);

        private const int S_OK = 0;
        private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0006);
        private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0001);
        private const int D3D_DRIVER_TYPE_UNKNOWN = 0;   // used when an adapter is supplied
        private const int D3D_DRIVER_TYPE_HARDWARE = 1;   // used when no adapter is supplied
        public enum FailStage
        {
            None = 0,
            Factory,      // CreateDXGIFactory1 failed
            Output,       // FindOutputByDeviceName / FindHmonitorByDeviceName returned zero
            Device,       // D3D11CreateDevice failed
            Dup,          // DuplicateOutput failed
            Stats,        // IDXGIOutput::GetFrameStatistics (primary backend) failed
            Dwm,          // DwmGetCompositionTimingInfo failed (DWM backend)
            AccessLost    // runtime: DXGI_ERROR_ACCESS_LOST repeatedly
        }

        public FailStage LastFail { get { return _lastFail; } }
        public int LastHr { get { return _lastHr; } }

        private volatile FailStage _lastFail = FailStage.None;
        private volatile int _lastHr = 0;
        private volatile bool _usingWindow;
        private volatile bool _windowAvailable;
        private volatile int _windowFps;
        private volatile string _windowTitle = "";
        private int _presentMonFrameCount;
        private readonly object _trackedProcessLock = new object();
        private HashSet<int> _trackedProcessIds = new HashSet<int>();

        private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x00200000u; // required for DuplicateOutput surface sharing

        private const uint D3D11_SDK_VERSION = 7;

        // Vtable slots (0-based, IUnknown occupies 0..2).
        private const int SLOT_QueryInterface = 0;             // IUnknown
        private const int SLOT_AddRef = 1;                      // IUnknown
        private const int SLOT_Release = 2;                     // IUnknown
        private const int SLOT_Factory_EnumAdapters1 = 12;       // IDXGIFactory1
        private const int SLOT_Adapter_EnumOutputs = 7;           // IDXGIAdapter(1)
        private const int SLOT_Output_GetDesc = 7;                // IDXGIOutput inherits IDXGIObject directly
        private const int SLOT_Output_GetFrameStatistics = 18;    // IDXGIOutput (base) — Vista+
        private const int SLOT_Output1_DuplicateOutput = 22;      // IDXGIOutput1
        private const int SLOT_Dup_AcquireNextFrame = 8;          // IDXGIOutputDuplication
        private const int SLOT_Dup_ReleaseFrame = 14;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int QueryInterfaceDel(IntPtr This, ref Guid riid, out IntPtr ppvObject);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseDel(IntPtr This);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdapters1Del(IntPtr This, int Adapter, out IntPtr ppAdapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumOutputsDel(IntPtr This, int Output, out IntPtr ppOutput);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int OutputGetDescDel(IntPtr This, IntPtr pDesc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int OutputGetFrameStatisticsDel(IntPtr This, IntPtr pStats);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DuplicateOutputDel(IntPtr This, IntPtr pDevice, out IntPtr ppDup);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AcquireNextFrameDel(IntPtr This, uint TimeoutMs, IntPtr pFrameInfo, ref IntPtr ppResource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReleaseFrameDel(IntPtr This);

        private static T GetDelegate<T>(IntPtr comObj, int slot, string dbgName) where T : class
        {
            FpsTrace("GetDelegate.ENTER name=" + dbgName + " comObj=0x" + comObj.ToString("X8") + " slot=" + slot);
            if (comObj == IntPtr.Zero) { FpsTrace("GetDelegate.NULL comObj -> return null"); return null; }
            IntPtr vtbl;
            try { vtbl = Marshal.ReadIntPtr(comObj); }
            catch (Exception ex)
            {
                FpsTrace("GetDelegate.AV reading vtbl@comObj -> " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }
            FpsTrace("GetDelegate.vtbl=0x" + vtbl.ToString("X8"));
            IntPtr fn;
            try { fn = Marshal.ReadIntPtr(vtbl, checked(slot * IntPtr.Size)); }
            catch (Exception ex)
            {
                FpsTrace("GetDelegate.AV reading slot#" + slot + "@vtbl -> " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }
            FpsTrace("GetDelegate.fn=0x" + fn.ToString("X8"));
            T result;
            try { result = (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T)); }
            catch (Exception ex)
            {
                FpsTrace("GetDelegate.GetDelegateForFunctionPointer FAIL -> " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }
            FpsTrace("GetDelegate.LEAVE name=" + dbgName + " OK");
            return result;
        }

        private static void ReleaseCom(IntPtr comObj)
        {
            if (comObj == IntPtr.Zero) return;
            try
            {
                ReleaseDel del = GetDelegate<ReleaseDel>(comObj, SLOT_Release, "IUnknown.Release");
                if (del == null) return;
                del(comObj);
            }
            catch { }
        }

        // Query a raw IDXGIOutput pointer for the IDXGIOutput1 interface that
        // actually exposes DuplicateOutput. Returns S_OK on success and sets
        // *ppOutput1 to a QI'd (addRef'd) IDXGIOutput1* pointer; the caller must
        // release both the new pointer AND (separately) the original base output.
        private static int QueryOutput1(IntPtr outputBase, out IntPtr ppOutput1)
        {
            ppOutput1 = IntPtr.Zero;
            if (outputBase == IntPtr.Zero) return unchecked((int)0x80004003u); // E_POINTER
            try
            {
                QueryInterfaceDel qi = GetDelegate<QueryInterfaceDel>(outputBase, SLOT_QueryInterface, "IUnknown.QueryInterface");
                if (qi == null) return unchecked((int)0x80004003u);
                Guid iid = IID_IDXGIOutput1;
                int hr = qi(outputBase, ref iid, out ppOutput1);
                FpsTrace("QueryOutput1 hr=0x" + unchecked((uint)hr).ToString("X8") + " ppOutput1=0x" + ppOutput1.ToString("X8"));
                return hr;
            }
            catch (Exception e)
            {
                FpsTrace("QueryOutput1 EXC " + e.GetType().Name + ": " + e.Message);
                if (e is NullReferenceException) return unchecked((int)0x80004003u);
                return unchecked((int)0x80004005u); // E_FAIL
            }
        }

        // ---- IDXGIOutput::GetFrameStatistics PRIMARY FPS backend ------------------
        //
        // This is the ground-truth "present-to-monitor" counter exported by every
        // IDXGIOutput since Vista. It lives on the BASE IDXGIOutput interface at
        // vtable slot 18. No IDXGIOutput1 QI, no D3D11CreateDevice, no BGRA flag,
        // no exclusive OutputDuplication lock. The counter is a monotonically
        // increasing UINT (PresentCount) incremented each time DWM sends a new
        // frame to the output's VidPN scanout; sampling the delta per second
        // yields the real desktop FPS seen by the user.
        //
        // Returns true if the loop ran until cancellation; false on persistent
        // failure so Run() can fall through to the DWM path.

        // DXGI_FRAME_STATISTICS layout (bytes):
        //   0..3  UINT PresentCount           ← what we sample
        //   4..7  UINT PresentRefreshCount
        //   8..11 UINT SyncRefreshCount
        //  12..19 LARGE_INTEGER SyncQPCTime
        //  20..27 LARGE_INTEGER SyncGPUTime
        // Total 28 bytes.
        private const int DXGI_FRAME_STATISTICS_SIZE = 28;
        private const int DXGI_FS_PRESENTCOUNT_OFFSET = 0;

        // Poll PresentCount from an already-opened IDXGIOutput pointer.
        private static int ReadOutputPresentCount(IntPtr output, out uint presentCount)
        {
            presentCount = 0;
            if (output == IntPtr.Zero) { FpsTrace("ReadOutputPresentCount: null output"); return unchecked((int)0x80004003u); }
            FpsTrace("ReadOutputPresentCount: ENTER");
            byte[] buf = new byte[DXGI_FRAME_STATISTICS_SIZE];
            GCHandle pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                OutputGetFrameStatisticsDel fn = GetDelegate<OutputGetFrameStatisticsDel>(
                    output, SLOT_Output_GetFrameStatistics, "IDXGIOutput.GetFrameStatistics");
                if (fn == null) { FpsTrace("ReadOutputPresentCount: null delegate -> E_FAIL"); return unchecked((int)0x80004005u); }
                FpsTrace("ReadOutputPresentCount: invoking delegate, pStats=0x" + pin.AddrOfPinnedObject().ToString("X8"));
                int hr;
                try { hr = fn(output, pin.AddrOfPinnedObject()); }
                catch (Exception ex)
                {
                    FpsTrace("ReadOutputPresentCount: INVOKE EXC " + ex.GetType().Name + ": " + ex.Message);
                    throw;
                }
                FpsTrace("ReadOutputPresentCount: hr=0x" + unchecked((uint)hr).ToString("X8"));
                if (hr != S_OK) return hr;
                presentCount = BitConverter.ToUInt32(buf, DXGI_FS_PRESENTCOUNT_OFFSET);
                FpsTrace("ReadOutputPresentCount: LEAVE count=" + presentCount);
                return S_OK;
            }
            finally { pin.Free(); }
        }

        // Main GetFrameStatistics session. Returns true if exited via cancellation;
        // false on persistent failure so Run() can try DWM before retrying DXGI.
        private bool TryRunFrameStats(out int lastFailHr)
        {
            lastFailHr = 0;
            IntPtr factory = IntPtr.Zero;
            IntPtr adapter = IntPtr.Zero;
            IntPtr output = IntPtr.Zero;
            int consecutiveFails = 0;
            Stopwatch sw = Stopwatch.StartNew();
            uint prevCount = 0;
            bool haveBaseline = false;

            try
            {
                Guid iidUnk = IID_IUnknown;
                FpsTrace("TryRunFrameStats: CreateDXGIFactory1 #1");
                int hr = CreateDXGIFactory1(ref iidUnk, out factory);
                FpsTrace("TryRunFrameStats: CreateDXGIFactory1 hr=0x" + unchecked((uint)hr).ToString("X8") + " factory=0x" + factory.ToString("X8"));
                if (hr != S_OK)
                {
                    _lastFail = FailStage.Factory;
                    _lastHr = hr;
                    lastFailHr = hr;
                    return false;
                }
                output = FindOutputByDeviceName(factory, _targetDevice, out adapter);
                if (output == IntPtr.Zero)
                {
                    _lastFail = FailStage.Output;
                    _lastHr = 0;
                    lastFailHr = 0;
                    return false;
                }

                while (!_cts.IsCancellationRequested)
                {
                    if (_rebuild)
                    {
                        _rebuild = false;
                        FpsTrace("TryRunFrameStats: rebuild triggered");
                        ReleaseCom(output); output = IntPtr.Zero;
                        ReleaseCom(adapter); adapter = IntPtr.Zero;
                        ReleaseCom(factory); factory = IntPtr.Zero;
                        FpsTrace("TryRunFrameStats: CreateDXGIFactory1 #rebuild");
                        hr = CreateDXGIFactory1(ref iidUnk, out factory);
                        FpsTrace("TryRunFrameStats: rebuild factory hr=0x" + unchecked((uint)hr).ToString("X8"));
                        if (hr != S_OK) { _lastFail = FailStage.Factory; _lastHr = hr; lastFailHr = hr; return false; }
                        FpsTrace("TryRunFrameStats: rebuild FindOutputByDeviceName");
                        output = FindOutputByDeviceName(factory, _targetDevice, out adapter);
                        FpsTrace("TryRunFrameStats: rebuild output=0x" + output.ToString("X8") + " adapter=0x" + adapter.ToString("X8"));
                        if (output == IntPtr.Zero) { _lastFail = FailStage.Output; _lastHr = 0; lastFailHr = 0; return false; }
                        haveBaseline = false;
                    }
                    uint now;
                    hr = ReadOutputPresentCount(output, out now);
                    if (hr != S_OK)
                    {
                        _available = false;
                        consecutiveFails++;
                        if (consecutiveFails >= 30) { lastFailHr = hr; return false; }
                        _lastFail = FailStage.Stats;
                        _lastHr = hr;
                        Thread.Sleep(50);
                        continue;
                    }
                    consecutiveFails = 0;
                    _available = true;
                    if (!haveBaseline)
                    {
                        prevCount = now;
                        sw.Restart();
                        haveBaseline = true;
                    }
                    else if (now < prevCount)
                    {
                        // 32-bit wrap / mode change / DWM restart.
                        prevCount = now;
                        sw.Restart();
                    }
                    else if (sw.ElapsedMilliseconds >= 1000)
                    {
                        double deltaMs = sw.Elapsed.TotalMilliseconds;
                        long deltaP = unchecked((long)now - (long)prevCount);
                        if (deltaP < 0) deltaP = 0;
                        double rawFps = deltaP * 1000.0 / deltaMs;
                        int v = (int)Math.Round(rawFps);
                        if (v < 0) v = 0;
                        if (v > 999) v = 999;
                        _currentFps = v;
                        prevCount = now;
                        sw.Restart();
                    }
                    Thread.Sleep(16);
                }
                return true;
            }
            finally
            {
                ReleaseCom(output);
                ReleaseCom(adapter);
                ReleaseCom(factory);
            }
        }

        // ---- DeviceName <-> HMONITOR lookup + DWM present counter ----------------

        // Returns the HMONITOR whose szDevice matches target (e.g. \\.\DISPLAY2).
        // Returns IntPtr.Zero if not found. Uses EnumDisplayMonitors + GetMonitorInfo.
        private static IntPtr FindHmonitorByDeviceName(string target, out Win32.RECT rcMonitor)
        {
            rcMonitor = default(Win32.RECT);
            if (string.IsNullOrEmpty(target)) return IntPtr.Zero;
            string t = StripNulls(target).Replace("\\", "").Replace(".", "").Trim().ToUpperInvariant();
            IntPtr found = IntPtr.Zero;
            Win32.RECT foundRc = default(Win32.RECT);
            MonitorEnumDelegate callback = delegate(IntPtr hMon, IntPtr hdc, ref Win32.RECT rc, IntPtr data)
            {
                MONITORINFOEX mi = new MONITORINFOEX();
                if (GetMonitorInfoW(hMon, mi) && !string.IsNullOrEmpty(mi.szDevice))
                {
                    string d = StripNulls(mi.szDevice).Replace("\\", "").Replace(".", "").Trim().ToUpperInvariant();
                    if (string.Equals(d, t, StringComparison.Ordinal) ||
                        (!string.IsNullOrEmpty(d) && d.EndsWith(t, StringComparison.Ordinal)) ||
                        (!string.IsNullOrEmpty(t) && t.EndsWith(d, StringComparison.Ordinal)))
                    {
                        System.Threading.Interlocked.Exchange(ref found, hMon);
                        foundRc = mi.rcMonitor;
                    }
                }
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            if (found != IntPtr.Zero) rcMonitor = foundRc;
            return found;
        }

        // Read ullPresentCount from DWM_TIMING_INFO for the given HWND.
        // DWM returns the timing for the monitor that owns this HWND.
        // Returns S_OK on success; counter is written via the out parameter.
        private static int ReadDwmPresentCount(IntPtr hWnd, out long presentCount)
        {
            presentCount = 0;
            if (hWnd == IntPtr.Zero) return unchecked((int)0x80004003u); // E_POINTER
            byte[] buf = new byte[DWM_TIMINGINFO_SIZE];
            BitConverter.GetBytes(DWM_TIMINGINFO_SIZE).CopyTo(buf, 0);
            GCHandle pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                int hr = DwmGetCompositionTimingInfo(hWnd, pin.AddrOfPinnedObject());
                if (hr != S_OK) return hr;
                presentCount = unchecked((long)BitConverter.ToUInt64(buf, DWM_TI_PRESENTCOUNT_OFFSET));
                return S_OK;
            }
            finally { pin.Free(); }
        }

        // ---- DWM anchor HWND (1x1 transparent helper, moved onto the target monitor)
        private static volatile bool _wndClassRegistered;
        private static readonly object _wndClassLock = new object();
        private static IntPtr _wndClassAtom = IntPtr.Zero;
        private static WndProcDelegate _cachedWndProc; // must stay rooted so delegate isn't GC'd

        private IntPtr _dwmAnchorHwnd;

        private static IntPtr AnchorWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // Do nothing — we only care about keeping a valid HWND that DWM can
            // resolve to a monitor via its virtual desktop coordinate.
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        private static void EnsureAnchorWndClassRegistered()
        {
            if (_wndClassRegistered) return;
            lock (_wndClassLock)
            {
                if (_wndClassRegistered) return;
                try
                {
                    _cachedWndProc = new WndProcDelegate(AnchorWndProc);
                    WNDCLASSEXW wc = new WNDCLASSEXW();
                    wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEXW));
                    wc.style = 0;
                    wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_cachedWndProc);
                    wc.cbClsExtra = 0;
                    wc.cbWndExtra = 0;
                    wc.hInstance = Process.GetCurrentProcess().Handle;
                    wc.hIcon = IntPtr.Zero;
                    wc.hCursor = IntPtr.Zero;
                    wc.hbrBackground = IntPtr.Zero;
                    wc.lpszMenuName = null;
                    wc.lpszClassName = "PerfMonitorFpsAnchor_" + Guid.NewGuid().ToString("N");
                    wc.hIconSm = IntPtr.Zero;
                    ushort atom = RegisterClassW(ref wc);
                    if (atom == 0)
                    {
                        FpsTrace("EnsureAnchorWndClassRegistered: RegisterClassW FAILED gle=" + Marshal.GetLastWin32Error());
                        return;
                    }
                    _wndClassAtom = (IntPtr)atom;
                    FpsTrace("EnsureAnchorWndClassRegistered: OK class=[" + wc.lpszClassName + "] atom=0x" + _wndClassAtom.ToString("X8"));
                }
                finally { _wndClassRegistered = true; }
            }
        }

        // Creates (if needed) the 1x1 visible but impossible-to-notice anchor HWND
        // and positions it at (rcMonitor.right - 2, rcMonitor.bottom - 2) so it
        // occupies the bottom-right 1px corner of the target monitor.
        //
        // NO WS_EX_LAYERED / NO SetLayeredWindowAttributes. Previous alpha=0 /
        // alpha=1 layered windows caused DwmGetCompositionTimingInfo to return
        // 0x88980090 because DWM appears to skip windows with no composited pixels.
        // Using a plain (non-layered) 1x1 WS_POPUP | WS_VISIBLE window guarantees
        // DWM considers it for composition; WS_EX_TRANSPARENT makes it click-through
        // and WS_EX_TOOLWINDOW keeps it out of Alt-Tab/taskbar. A single black 1x1
        // pixel in the screen corner is truly invisible to the human eye.
        private IntPtr EnsureDwmAnchorOnMonitor(ref Win32.RECT rcMonitor)
        {
            try { EnsureAnchorWndClassRegistered(); } catch { }

            if (_dwmAnchorHwnd == IntPtr.Zero)
            {
                const int WS_EX = WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                const int STYLE = WS_POPUP | WS_VISIBLE;
                int w = rcMonitor.Right - rcMonitor.Left;
                int h = rcMonitor.Bottom - rcMonitor.Top;
                // Anchor to bottom-right 1px corner. Clamp to valid positive coords
                // (monitors can start at negative virtual-desktop offsets).
                int x = rcMonitor.Right - 2;
                int y = rcMonitor.Bottom - 2;
                if (x < rcMonitor.Left) x = rcMonitor.Left;
                if (y < rcMonitor.Top) y = rcMonitor.Top;
                try
                {
                    _dwmAnchorHwnd = CreateWindowExW(
                        WS_EX,
                        "STATIC",
                        "",
                        STYLE,
                        x, y, 1, 1,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero);
                }
                catch (Exception ex)
                {
                    FpsTrace("EnsureDwmAnchorOnMonitor: CreateWindowExW EXC " + ex.GetType().Name + ": " + ex.Message);
                }
                if (_dwmAnchorHwnd == IntPtr.Zero)
                {
                    FpsTrace("EnsureDwmAnchorOnMonitor: CreateWindowExW FAILED gle=" + Marshal.GetLastWin32Error());
                    return IntPtr.Zero;
                }
                FpsTrace("EnsureDwmAnchorOnMonitor: CREATED (non-layered 1x1) hwnd=0x" + _dwmAnchorHwnd.ToString("X8") + " at (" + x + "," + y + ") size=" + w + "x" + h);
                return _dwmAnchorHwnd;
            }
            else
            {
                int x = rcMonitor.Right - 2;
                int y = rcMonitor.Bottom - 2;
                if (x < rcMonitor.Left) x = rcMonitor.Left;
                if (y < rcMonitor.Top) y = rcMonitor.Top;
                bool ok = SetWindowPos(_dwmAnchorHwnd, IntPtr.Zero, x, y, 1, 1,
                    (uint)(SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW));
                if (!ok)
                {
                    FpsTrace("EnsureDwmAnchorOnMonitor: SetWindowPos FAILED gle=" + Marshal.GetLastWin32Error());
                }
                return _dwmAnchorHwnd;
            }
        }

        private void DestroyDwmAnchor()
        {
            if (_dwmAnchorHwnd != IntPtr.Zero)
            {
                try { DestroyWindow(_dwmAnchorHwnd); }
                catch (Exception ex) { FpsTrace("DestroyDwmAnchor: EXC " + ex.GetType().Name + ": " + ex.Message); }
                FpsTrace("DestroyDwmAnchor: Destroyed hwnd=0x" + _dwmAnchorHwnd.ToString("X8"));
                _dwmAnchorHwnd = IntPtr.Zero;
            }
        }

        // ---- Public state ---------------------------------------------------------

        private bool _available;
        public bool Available { get { return _usingWindow ? _windowAvailable : _available; } }
        private volatile int _currentFps;
        public int CurrentFps { get { return _usingWindow ? _windowFps : _currentFps; } }
        public string CurrentSourceLabel
        {
            get
            {
                return _usingWindow && !string.IsNullOrEmpty(_windowTitle)
                    ? FormatWindowTitle(_windowTitle)
                    : "桌面";
            }
        }

        private static string FormatWindowTitle(string title)
        {
            string formatted = Regex.Replace(title.Trim(), @"\s*和另外\s*\d+\s*个页面\s*$", "");
            string[] browserSuffixes = new string[]
            {
                " - Microsoft Edge",
                " - Google Chrome",
                " - Mozilla Firefox",
                " - Opera",
                " - Brave"
            };

            foreach (string suffix in browserSuffixes)
            {
                if (!formatted.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                formatted = formatted.Substring(0, formatted.Length - suffix.Length);
                if (suffix.Equals(" - Microsoft Edge", StringComparison.OrdinalIgnoreCase)
                    || suffix.Equals(" - Google Chrome", StringComparison.OrdinalIgnoreCase))
                {
                    int profileSeparator = formatted.LastIndexOf(" - ", StringComparison.Ordinal);
                    if (profileSeparator >= 0)
                        formatted = formatted.Substring(0, profileSeparator);
                }
                break;
            }

            string[] siteSuffixes = new string[]
            {
                "_哔哩哔哩_bilibili",
                " - YouTube",
                " - Twitch"
            };
            foreach (string suffix in siteSuffixes)
            {
                if (formatted.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    formatted = formatted.Substring(0, formatted.Length - suffix.Length);
                    break;
                }
            }

            formatted = formatted.Trim();
            const int maxLength = 12;
            if (formatted.Length > maxLength)
            {
                int end = maxLength - 1;
                if (char.IsHighSurrogate(formatted[end - 1])) end--;
                formatted = formatted.Substring(0, end) + "…";
            }
            return string.IsNullOrEmpty(formatted) ? title : formatted;
        }

        private Thread _windowThread;
        private bool _orphanSessionsChecked;

        private static bool IsDesktopWindow(IntPtr hwnd)
        {
            StringBuilder className = new StringBuilder(128);
            int length = Win32.GetClassNameW(hwnd, className, className.Capacity);
            if (length <= 0) return false;
            string name = className.ToString();
            return name.Equals("Progman", StringComparison.OrdinalIgnoreCase)
                || name.Equals("WorkerW", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase);
        }

        private void WindowThreadProc()
        {
            Process capture = null;
            int selectedPid = 0;
            int capturePid = 0;
            DateTime nextCaptureAttempt = DateTime.MinValue;
            DateTime nextProcessTreeUpdate = DateTime.MinValue;
            int selfPid = Process.GetCurrentProcess().Id;
            int lastLoggedFps = -1;
            Stopwatch fpsWindow = Stopwatch.StartNew();

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    IntPtr hwnd = Win32.GetForegroundWindow();
                    uint foregroundPid = 0;
                    if (hwnd != IntPtr.Zero)
                        Win32.GetWindowThreadProcessId(hwnd, out foregroundPid);

                    string title = "";
                    int targetPid = 0;
                    try
                    {
                        if (hwnd != IntPtr.Zero && foregroundPid != 0 && foregroundPid != selfPid
                            && !IsDesktopWindow(hwnd)
                            && System.Windows.Forms.Screen.FromHandle(hwnd).DeviceName.Equals(
                                _targetDevice, StringComparison.OrdinalIgnoreCase))
                        {
                            StringBuilder titleBuffer = new StringBuilder(512);
                            Win32.GetWindowTextW(hwnd, titleBuffer, titleBuffer.Capacity);
                            title = titleBuffer.ToString().Trim();
                            if (string.IsNullOrEmpty(title)) title = "未命名窗口";
                            targetPid = unchecked((int)foregroundPid);
                        }
                    }
                    catch (Exception ex)
                    {
                        FpsTrace("WindowFps: foreground-window lookup failed: " + ex.Message);
                    }

                    if (targetPid != selectedPid)
                    {
                        if (targetPid == 0)
                        {
                            StopPresentMon(capture);
                            capture = null;
                            capturePid = 0;
                            lock (_trackedProcessLock) _trackedProcessIds.Clear();
                        }
                        selectedPid = targetPid;
                        capturePid = targetPid;
                        _usingWindow = false;
                        _windowAvailable = false;
                        _windowFps = 0;
                        _windowTitle = "";
                        Interlocked.Exchange(ref _presentMonFrameCount, 0);
                        fpsWindow.Restart();
                        if (targetPid != 0 && capture == null)
                            nextCaptureAttempt = DateTime.MinValue;
                        nextProcessTreeUpdate = DateTime.MinValue;
                    }

                    if (targetPid != 0 && capture == null && DateTime.Now >= nextCaptureAttempt)
                    {
                        capture = StartPresentMon();
                        nextCaptureAttempt = DateTime.Now.AddSeconds(3);
                        if (capture == null)
                        {
                            FpsTrace("WindowFps: PresentMon unavailable for PID=" + targetPid + "; using desktop FPS");
                        }
                        else
                        {
                            capturePid = Process.GetCurrentProcess().Id;
                            nextCaptureAttempt = DateTime.MaxValue;
                            FpsTrace("WindowFps: all-process capture started for foreground PID=" + targetPid + " title=[" + title + "]");
                        }
                    }

                    if (targetPid != 0 && DateTime.Now >= nextProcessTreeUpdate)
                    {
                        try
                        {
                            HashSet<int> processIds = Win32.GetProcessTree(targetPid);
                            bool changed;
                            lock (_trackedProcessLock)
                            {
                                changed = !_trackedProcessIds.SetEquals(processIds);
                                _trackedProcessIds = processIds;
                            }
                            if (changed)
                            {
                                FpsTrace("WindowFps: tracking PID tree for " + targetPid + ": [" +
                                    string.Join(",", processIds.OrderBy(delegate(int id) { return id; }).Select(delegate(int id) { return id.ToString(); }).ToArray()) + "]");
                            }
                        }
                        catch (Exception ex)
                        {
                            lock (_trackedProcessLock)
                                _trackedProcessIds = new HashSet<int>(new int[] { targetPid });
                            FpsTrace("WindowFps: process tree lookup failed for PID=" + targetPid + ": " + ex.Message);
                        }
                        nextProcessTreeUpdate = DateTime.Now.AddSeconds(1);
                    }

                    if (targetPid != 0 && capture != null)
                    {
                        _usingWindow = true;
                        _windowAvailable = true;
                        _windowTitle = title;
                    }
                    else if (targetPid == 0 || capture == null)
                    {
                        _usingWindow = false;
                        _windowAvailable = false;
                        if (targetPid == 0) _windowTitle = "";
                    }

                    if (capture != null)
                    {
                        bool exited;
                        try { exited = capture.HasExited; }
                        catch (InvalidOperationException) { exited = true; }
                        if (exited)
                        {
                            int exitCode = -1;
                            try { exitCode = capture.ExitCode; } catch { }
                            FpsTrace("WindowFps: PresentMon exited (PID=" + capturePid + ", exitCode=" + exitCode + ")");
                            StopPresentMon(capture);
                            capture = null;
                            capturePid = 0;
                            nextCaptureAttempt = DateTime.Now.AddSeconds(3);
                            _usingWindow = false;
                            _windowAvailable = false;
                            _windowTitle = "";
                        }
                    }

                    if (capture != null && fpsWindow.ElapsedMilliseconds >= 1000)
                    {
                        int frames = Interlocked.Exchange(ref _presentMonFrameCount, 0);
                        _windowFps = Math.Max(0, Math.Min(999, frames));
                        _windowAvailable = true;
                        if (_windowFps != lastLoggedFps)
                        {
                            FpsTrace("WindowFps: PID=" + capturePid + " FPS=" + _windowFps);
                            lastLoggedFps = _windowFps;
                        }
                        fpsWindow.Restart();
                    }

                    Thread.Sleep(200);
                }
            }
            catch (Exception ex)
            {
                FpsTrace("WindowFps: worker failed: " + ex.GetType().FullName + ": " + ex.Message);
                _usingWindow = false;
                _windowAvailable = false;
                _windowTitle = "";
            }
            finally
            {
                StopPresentMon(capture);
                _usingWindow = false;
                _windowAvailable = false;
                _windowTitle = "";
            }
        }

        private Process StartPresentMon()
        {
            string exePath = GetPresentMonPath();
            if (!File.Exists(exePath))
            {
                FpsTrace("WindowFps: PresentMon executable not found: " + exePath);
                return null;
            }

            Process process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--output_stdout --no_console_stats --v2_metrics --session_name "
                    + GetPresentMonSessionName() + " --stop_existing_session",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            int processIdColumn = -1;
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                string line = e.Data;
                if (string.IsNullOrWhiteSpace(line)) return;
                line = line.TrimStart('\uFEFF');
                string[] columns = line.Split(',');
                bool isHeader = false;
                for (int i = 0; i < columns.Length; i++)
                {
                    string column = columns[i].Trim().Trim('"');
                    if (column.Equals("ProcessID", StringComparison.OrdinalIgnoreCase))
                    {
                        processIdColumn = i;
                        isHeader = true;
                        break;
                    }
                }
                if (isHeader || processIdColumn < 0)
                {
                    if (isHeader)
                        FpsTrace("WindowFps: PresentMon CSV ProcessID column=" + processIdColumn);
                    return;
                }

                if (columns.Length <= processIdColumn) return;
                int rowPid;
                if (!int.TryParse(columns[processIdColumn].Trim().Trim('"'), out rowPid)) return;
                bool tracked;
                lock (_trackedProcessLock) tracked = _trackedProcessIds.Contains(rowPid);
                if (tracked)
                {
                    Interlocked.Increment(ref _presentMonFrameCount);
                }
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    FpsTrace("WindowFps: PresentMon: " + e.Data);
            };

            try
            {
                if (!process.Start())
                {
                    FpsTrace("WindowFps: failed to start PresentMon capture");
                    process.Dispose();
                    return null;
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                return process;
            }
            catch (Exception ex)
            {
                FpsTrace("WindowFps: could not start PresentMon capture: " + ex.Message);
                process.Dispose();
                return null;
            }
        }

        private static void StopPresentMon(Process process)
        {
            if (process == null) return;
            try
            {
                StopPresentMonSession();
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(1000);
                }
            }
            catch (Exception ex)
            {
                FpsTrace("WindowFps: failed to stop PresentMon: " + ex.Message);
            }
            finally
            {
                process.Dispose();
            }
        }

        private static string GetPresentMonPath()
        {
            return Path.Combine(
                Path.GetDirectoryName(typeof(FpsMonitor).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory,
                "PresentMon-2.6.0-x64.exe");
        }

        private static string GetPresentMonSessionName()
        {
            return "game_perf_overlay_" + Process.GetCurrentProcess().Id;
        }

        private static void StopPresentMonSession()
        {
            StopNamedPresentMonSession(GetPresentMonSessionName());
        }

        private static void StopNamedPresentMonSession(string sessionName)
        {
            string exePath = GetPresentMonPath();
            if (!File.Exists(exePath)) return;
            Process stopSession = new Process();
            stopSession.StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--session_name " + sessionName + " --terminate_existing_session",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            try
            {
                if (!stopSession.Start())
                {
                    FpsTrace("WindowFps: failed to start PresentMon ETW session cleanup");
                    return;
                }
                if (!stopSession.WaitForExit(3000))
                {
                    FpsTrace("WindowFps: timed out stopping PresentMon ETW session");
                    try { stopSession.Kill(); } catch { }
                }
                else if (stopSession.ExitCode != 0)
                {
                    FpsTrace("WindowFps: ETW session cleanup exited with code " + stopSession.ExitCode);
                }
            }
            catch (Exception ex)
            {
                FpsTrace("WindowFps: could not stop PresentMon ETW session: " + ex.Message);
            }
            finally
            {
                stopSession.Dispose();
            }
        }

        private static void CleanupOrphanedPresentMonSessions()
        {
            Process query = new Process();
            query.StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "logman.exe"),
                Arguments = "query -ets",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            try
            {
                if (!query.Start())
                {
                    FpsTrace("WindowFps: failed to enumerate ETW sessions for cleanup");
                    return;
                }
                string output = query.StandardOutput.ReadToEnd();
                string errors = query.StandardError.ReadToEnd();
                if (!query.WaitForExit(5000))
                {
                    FpsTrace("WindowFps: timed out enumerating ETW sessions");
                    try { query.Kill(); } catch { }
                    return;
                }
                if (query.ExitCode != 0)
                {
                    FpsTrace("WindowFps: ETW session enumeration failed: " + errors.Trim());
                    return;
                }

                string[] lines = output.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    const string currentPrefix = "game_perf_overlay_";
                    const string legacyPrefix = "PerfMonitor_";
                    string[] fields = trimmed.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length == 0) continue;
                    string prefix = fields[0].StartsWith(currentPrefix, StringComparison.OrdinalIgnoreCase)
                        ? currentPrefix
                        : (fields[0].StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase) ? legacyPrefix : null);
                    if (prefix == null) continue;

                    int ownerPid;
                    if (!int.TryParse(fields[0].Substring(prefix.Length), out ownerPid)) continue;
                    try
                    {
                        using (Process.GetProcessById(ownerPid)) { }
                    }
                    catch (ArgumentException)
                    {
                        FpsTrace("WindowFps: cleaning orphaned ETW session [" + fields[0] + "]");
                        StopNamedPresentMonSession(fields[0]);
                    }
                }
            }
            catch (Exception ex)
            {
                FpsTrace("WindowFps: could not enumerate orphaned ETW sessions: " + ex.Message);
            }
            finally
            {
                query.Dispose();
            }
        }

        // ---- Threaded sampling ----------------------------------------------------

        [HandleProcessCorruptedStateExceptions, SecurityCritical, ReliabilityContract(Consistency.MayCorruptProcess, Cer.MayFail)]
        private void ThreadProc()
        {
            FpsTrace("ThreadProc.ENTER");
            try
            {
                Run();
                FpsTrace("ThreadProc.Run returned normally -> EXIT");
            }
            catch (System.Exception ex)
            {
                try
                {
                    FpsTrace("ThreadProc.EXC " + ex.GetType().FullName + ": " + ex.Message + "\r\nStack:\r\n" + ex.StackTrace);
                    string txt = "[" + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] FpsMonitor thread CSE catch"
                        + "\r\nType: " + ex.GetType().FullName
                        + "\r\nMsg : " + ex.Message
                        + "\r\nStack:\r\n" + ex.StackTrace + "\r\n----------------\r\n";
                    SessionFiles.AppendLog(txt);
                }
                catch { }
                // Do NOT rethrow (would kill process). Instead mark FPS unavailable so
                // user sees FPS: NA in overlay.
                _available = false;
                _lastFail = FailStage.Stats;
                _lastHr = unchecked((int)0x80004005u);
                FpsTrace("ThreadProc.EXC handled (not rethrown)");
            }
        }

        private Thread _thread;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private volatile string _targetDevice = "";
        private volatile bool _rebuild;

        public void Start(string deviceName)
        {
            if (_cts.IsCancellationRequested)
            {
                if (_thread != null && _thread.IsAlive) _thread.Join();
                if (_windowThread != null && _windowThread.IsAlive) _windowThread.Join();
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }
            FpsTrace("Start: BEGIN target=[" + (deviceName ?? "(null)") + "] _threadIsAlive=" + (_thread != null && _thread.IsAlive));
            if (!_orphanSessionsChecked)
            {
                CleanupOrphanedPresentMonSessions();
                _orphanSessionsChecked = true;
            }
            _targetDevice = deviceName ?? "";
            _currentFps = 0;
            _available = false;
            _usingWindow = false;
            _windowAvailable = false;
            _windowFps = 0;
            _windowTitle = "";
            if (_thread == null || !_thread.IsAlive)
            {
                _thread = new Thread(ThreadProc);
                _thread.IsBackground = true;
                _thread.Name = "FpsMonitor";
                _thread.Start();
            }
            else
            {
                _rebuild = true; // force re-init on the next loop iteration
            }
            if (_windowThread == null || !_windowThread.IsAlive)
            {
                _windowThread = new Thread(WindowThreadProc);
                _windowThread.IsBackground = true;
                _windowThread.Name = "FpsWindowMonitor";
                _windowThread.Start();
            }
        }

        public void Stop()
        {
            try { _cts.Cancel(); }
            catch { }
        }

        // Optional: the caller (OverlayWindow) can pass us the HWND of the WPF
        // overlay itself. If the overlay happens to live on the target monitor
        // (the 99% case: user monitors FPS on the same screen the overlay is on),
        // we use the overlay HWND directly instead of creating a synthetic anchor
        // window when the DWM fallback is used.
        private IntPtr _overlayHwnd;
        public void SetOverlayHwndHint(IntPtr hwnd)
        {
            Interlocked.Exchange(ref _overlayHwnd, hwnd);
            FpsTrace("SetOverlayHwndHint: hwnd=0x" + hwnd.ToString("X8"));
        }

        private void Run()
        {
            Win32.timeBeginPeriod(1);
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    int lastFailHr;
                    RunSession();
                    if (_cts.IsCancellationRequested) break;

                    FpsTrace("Run: desktop duplication ended (stage=" + _lastFail + ", hr=0x" + unchecked((uint)_lastHr).ToString("X8") + "); trying DWM");
                    if (TryRunDwm(out lastFailHr)) return;
                    _lastFail = FailStage.Dwm;
                    _lastHr = lastFailHr;

                    FpsTrace("Run: DWM unavailable (hr=0x" + unchecked((uint)lastFailHr).ToString("X8") + "); trying DXGI frame statistics");
                    if (TryRunFrameStats(out lastFailHr)) return;

                    for (int i = 0; i < 10 && !_cts.IsCancellationRequested; i++)
                        Thread.Sleep(100);
                }
            }
            finally
            {
                Win32.timeEndPeriod(1);
                DestroyDwmAnchor();
            }
        }

        // DWM backend — samples ullPresentCount via DwmGetCompositionTimingInfo on
        // a tiny 1x1 anchor HWND moved onto the target monitor. Returns true if the
        // session ran until cancellation; returns false if a persistent DWM-level
        // failure occurred after 20 consecutive failed calls.
        private bool TryRunDwm(out int lastFailHr)
        {
            lastFailHr = 0;
            int consecutiveFails = 0;
            Stopwatch sw = Stopwatch.StartNew();
            long prevCount = 0;
            bool haveBaseline = false;
            string lastDevice = null;
            int sampleIdx = 0;
            int lastLoggedFps = -999999;

            while (!_cts.IsCancellationRequested)
            {
                if (_rebuild) { _rebuild = false; haveBaseline = false; lastDevice = null; /* re-lookup monitor below */ }

                Win32.RECT rcMon;
                IntPtr hMon = FindHmonitorByDeviceName(_targetDevice, out rcMon);
                if (hMon == IntPtr.Zero)
                {
                    _available = false;
                    _lastFail = FailStage.Output;
                    _lastHr = 0;
                    consecutiveFails++;
                    if ((sampleIdx & 0x3F) == 0) FpsTrace("TryRunDwm: hMon=ZERO device=[" + _targetDevice + "] fail#" + consecutiveFails);
                    if (consecutiveFails >= 20) { lastFailHr = 0; return false; }
                    Thread.Sleep(100);
                    sampleIdx++;
                    continue;
                }

                // --- Choose which HWND to pass to DwmGetCompositionTimingInfo --------
                // Priority A: OverlayWindow's own HWND, if it sits on the target monitor.
                //   (This is the 99% case — user put the overlay on DISPLAY1 and wants
                //    FPS for DISPLAY1. Using a real visible top-level window GUARANTEES
                //    DWM returns valid timing; no 0x88980090 ever.)
                // Priority B: synthetic 1x1 non-layered anchor HWND moved onto the
                //   target monitor (for when the user chooses an FPS monitor different
                //   from where the overlay lives).
                IntPtr sampleHwnd = IntPtr.Zero;
                IntPtr overlayHwndSnap = Interlocked.CompareExchange(ref _overlayHwnd, IntPtr.Zero, IntPtr.Zero);
                bool usedOverlay = false;
                if (overlayHwndSnap != IntPtr.Zero)
                {
                    IntPtr overlayHmon = MonitorFromWindow(overlayHwndSnap, (uint)MONITOR_DEFAULTTONULL);
                    if (overlayHmon == hMon)
                    {
                        sampleHwnd = overlayHwndSnap;
                        usedOverlay = true;
                    }
                }

                // Need anchor: create or move it onto the target monitor.
                if (!usedOverlay)
                {
                    if (lastDevice != _targetDevice)
                    {
                        FpsTrace("TryRunDwm: target monitor switch [" + (lastDevice ?? "(null)") + "] -> [" + _targetDevice + "] rcMon=[" + rcMon.Left + "," + rcMon.Top + " " + rcMon.Right + "x" + rcMon.Bottom + "] (overlay not on target, using anchor)");
                    }
                    IntPtr hwnd = EnsureDwmAnchorOnMonitor(ref rcMon);
                    if (hwnd == IntPtr.Zero)
                    {
                        _available = false;
                        _lastFail = FailStage.Dwm;
                        _lastHr = unchecked((int)0x80004005u);
                        consecutiveFails++;
                        if (consecutiveFails >= 20) { lastFailHr = _lastHr; return false; }
                        Thread.Sleep(100);
                        sampleIdx++;
                        continue;
                    }
                    sampleHwnd = _dwmAnchorHwnd;
                }
                else
                {
                    // Using overlay HWND; destroy anchor if any (no longer needed).
                    if (_dwmAnchorHwnd != IntPtr.Zero) DestroyDwmAnchor();
                    if (lastDevice != _targetDevice)
                    {
                        FpsTrace("TryRunDwm: target monitor switch [" + (lastDevice ?? "(null)") + "] -> [" + _targetDevice + "] (overlay ON target monitor, using overlay hwnd=0x" + overlayHwndSnap.ToString("X8") + ")");
                    }
                }
                lastDevice = _targetDevice;

                if (sampleHwnd == IntPtr.Zero)
                {
                    _available = false;
                    _lastFail = FailStage.Dwm;
                    _lastHr = unchecked((int)0x80004005u);
                    consecutiveFails++;
                    if (consecutiveFails >= 20) { lastFailHr = _lastHr; return false; }
                    Thread.Sleep(100);
                    sampleIdx++;
                    continue;
                }

                long nowCount;
                int hr = ReadDwmPresentCount(sampleHwnd, out nowCount);
                if (hr != S_OK)
                {
                    _available = false;
                    consecutiveFails++;
                    if (consecutiveFails == 1 || (consecutiveFails & 0x3) == 0)
                        FpsTrace("TryRunDwm: DwmGet hr=0x" + unchecked((uint)hr).ToString("X8") + " fail#" + consecutiveFails + " hwnd=0x" + sampleHwnd.ToString("X8") + " usedOverlay=" + usedOverlay);
                    if (consecutiveFails >= 20) { lastFailHr = hr; return false; }
                    _lastFail = FailStage.Dwm;
                    _lastHr = hr;
                    Thread.Sleep(50);
                    sampleIdx++;
                    continue;
                }

                consecutiveFails = 0;
                _available = true;
                if (!haveBaseline)
                {
                    prevCount = nowCount;
                    sw.Restart();
                    haveBaseline = true;
                    FpsTrace("TryRunDwm: BASELINE presentCount=" + nowCount + " (hwnd=0x" + sampleHwnd.ToString("X8") + " usedOverlay=" + usedOverlay + ")");
                }
                else if (nowCount < prevCount)
                {
                    prevCount = nowCount;
                    sw.Restart();
                }
                else if (sw.ElapsedMilliseconds >= 1000)
                {
                    double deltaMs = sw.Elapsed.TotalMilliseconds;
                    long deltaP = unchecked(nowCount - prevCount);
                    if (deltaP < 0) deltaP = 0;
                    double rawFps = deltaP * 1000.0 / deltaMs;
                    int v = (int)Math.Round(rawFps);
                    if (v < 0) v = 0;
                    if (v > 999) v = 999;
                    _currentFps = v;
                    if (v != lastLoggedFps)
                    {
                        FpsTrace("TryRunDwm: FPS=" + v + " (deltaP=" + deltaP + " deltaMs=" + deltaMs.ToString("0.0") + " usedOverlay=" + usedOverlay + ")");
                        lastLoggedFps = v;
                    }
                    prevCount = nowCount;
                    sw.Restart();
                }
                Thread.Sleep(16);
                sampleIdx++;
            }
            return true;
        }

        // One duplication session: create factory/adapter/output/device/dup, then
        // loop AcquireNextFrame counting real (non-cursor) frames per second.
        private void RunSession()
        {
            IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero, output = IntPtr.Zero;
            IntPtr output1 = IntPtr.Zero; // QI'd IDXGIOutput1 for DuplicateOutput call
            IntPtr device = IntPtr.Zero, dup = IntPtr.Zero;
            IntPtr frameBufPtr = IntPtr.Zero;
            GCHandle frameBufPin = default(GCHandle);

            try
            {
                Guid iid = IID_IUnknown;
                int hr = CreateDXGIFactory1(ref iid, out factory);
                if (hr != S_OK || factory == IntPtr.Zero)
                {
                    _lastFail = FailStage.Factory; _lastHr = hr; _available = false;
                    FpsTrace("RunSession: CreateDXGIFactory1 failed hr=0x" + unchecked((uint)hr).ToString("X8"));
                    return;
                }

                // Find the DXGI output whose DeviceName matches the target screen,
                // keeping the owning adapter alive for device creation.
                output = FindOutputByDeviceName(factory, _targetDevice, out adapter);
                if (output == IntPtr.Zero)
                {
                    _lastFail = FailStage.Output; _lastHr = 0; _available = false;
                    FpsTrace("RunSession: no DXGI output for device=[" + _targetDevice + "]");
                    return;
                }

                // Create a D3D11 device. DuplicateOutput REQUIRES BGRA_SUPPORT per
                // MSDN, but some older drivers / adapter combinations reject that
                // flag outright (returning DXGI_ERROR_SDK_COMPONENT_MISSING). We try
                // the "correct" configuration first, then fall back through ever-more
                // conservative variants:
                //   1) same adapter + BGRA (preferred)
                //   2) same adapter, no BGRA (works on most, dup may or may not)
                //   3) no specific adapter, HARDWARE driver type, BGRA
                //   4) no specific adapter, HARDWARE, no BGRA (last resort)
                uint[] flagList = new uint[] { D3D11_CREATE_DEVICE_BGRA_SUPPORT, 0 };
                int lastTryHr = 0;
                device = IntPtr.Zero;

                for (int attempt = 0; attempt < 4; attempt++)
                {
                    IntPtr tryAdapter = (attempt < 2) ? adapter : IntPtr.Zero;
                    int tryDriver  = (attempt < 2) ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE;
                    uint tryFlags  = flagList[attempt % 2];
                    IntPtr tryDev;
                    int h = D3D11CreateDevice(tryAdapter, tryDriver, IntPtr.Zero, tryFlags,
                                              IntPtr.Zero, 0, D3D11_SDK_VERSION, out tryDev,
                                              IntPtr.Zero, IntPtr.Zero);
                    if (h == S_OK && tryDev != IntPtr.Zero) { device = tryDev; break; }
                    lastTryHr = h;
                    if (tryDev != IntPtr.Zero)
                    {
                        try { ReleaseCom(tryDev); } catch { }
                    }
                }
                if (device == IntPtr.Zero)
                {
                    _lastFail = FailStage.Device; _lastHr = lastTryHr; _available = false;
                    FpsTrace("RunSession: D3D11CreateDevice failed hr=0x" + unchecked((uint)lastTryHr).ToString("X8"));
                    return;
                }

                // CRITICAL: EnumOutputs returns an IDXGIOutput* (base interface).
                // DuplicateOutput is defined on IDXGIOutput1 — a derived interface.
                // We MUST explicitly QueryInterface for IDXGIOutput1 and use THAT
                // pointer for DuplicateOutput, otherwise we're calling slot 23 on
                // the wrong vtable (garbage return like 0x00000001 or crash).
                int qiHr = QueryOutput1(output, out output1);
                if (qiHr != S_OK || output1 == IntPtr.Zero)
                {
                    _lastFail = FailStage.Dup; _lastHr = qiHr; _available = false;
                    FpsTrace("RunSession: IDXGIOutput1 query failed hr=0x" + unchecked((uint)qiHr).ToString("X8"));
                    return;
                }

                DuplicateOutputDel dupFn = GetDelegate<DuplicateOutputDel>(output1, SLOT_Output1_DuplicateOutput, "IDXGIOutput1.DuplicateOutput");
                hr = dupFn(output1, device, out dup);
                if (hr != S_OK || dup == IntPtr.Zero)
                {
                    _lastFail = FailStage.Dup; _lastHr = hr; _available = false;
                    FpsTrace("RunSession: DuplicateOutput failed hr=0x" + unchecked((uint)hr).ToString("X8"));
                    return;
                }

                FpsTrace("RunSession: desktop duplication started for [" + _targetDevice + "]");

                // Pinned frame-info buffer (native writes DXGI_OUTDUPL_FRAME_INFO here).
                // Field layout (x64, 8-byte aligned):
                //   0..7   LARGE_INTEGER LastPresentTime
                //   8..15  LARGE_INTEGER LastMouseUpdateTime
                //  16..19  UINT AccumulatedFrames
                //  20..35  RECT LastPresentRectDirty
                //  36..51  RECT LastMovedRectRegion (the new rect + source point)
                //  52..55  UINT AccumulatedMouseMoves
                //  56..63  padding
                // Total ~64 bytes. We allocate 256 to be safe across Win10/11 variants
                // that may extend the struct with fields at the end.
                byte[] frameBuf = new byte[256];
                frameBufPin = GCHandle.Alloc(frameBuf, GCHandleType.Pinned);
                frameBufPtr = frameBufPin.AddrOfPinnedObject();

                AcquireNextFrameDel acquire = GetDelegate<AcquireNextFrameDel>(dup, SLOT_Dup_AcquireNextFrame, "IDXGIOutputDuplication.AcquireNextFrame");
                ReleaseFrameDel releaseFrame = GetDelegate<ReleaseFrameDel>(dup, SLOT_Dup_ReleaseFrame, "IDXGIOutputDuplication.ReleaseFrame");

                _available = true;
                // "AccumulatedFrames" at offset 16 (UINT) counts NEW presents
                // submitted by apps since the previous acquire — exactly what we want
                // for FPS. It already compensates when the caller misses frames, so
                // summing it per second gives presents/sec directly. This avoids the
                // common pitfall of counting scan-outs or re-scans of the same
                // backbuffer as new frames (which caused the old "always 60" bug).
                long presents = 0;
                Stopwatch sw = Stopwatch.StartNew();

                while (!_cts.IsCancellationRequested && !_rebuild)
                {
                    IntPtr resource = IntPtr.Zero;
                    hr = acquire(dup, 0, frameBufPtr, ref resource);

                    if (hr == S_OK)
                    {
                        // struct layout: LastPresentTime(8) @ 0, LastMouseUpdateTime(8) @ 8,
                        // AccumulatedFrames(UINT 4) @ 16.
                        bool releaseFailed = false;
                        try
                        {
                            uint acc = (uint)Marshal.ReadInt32(frameBufPtr, 16);
                            if (acc > 0) presents += acc;
                        }
                        finally
                        {
                            ReleaseCom(resource);
                            int releaseHr = releaseFrame(dup);
                            if (releaseHr != S_OK)
                            {
                                _lastFail = FailStage.Dup;
                                _lastHr = releaseHr;
                                FpsTrace("RunSession: ReleaseFrame failed hr=0x" + unchecked((uint)releaseHr).ToString("X8"));
                                releaseFailed = true;
                            }
                        }
                        if (releaseFailed) break;
                    }
                    else if (hr == DXGI_ERROR_WAIT_TIMEOUT)
                    {
                        // No new frame yet; continue.
                    }
                    else if (hr == DXGI_ERROR_ACCESS_LOST)
                    {
                        _lastFail = FailStage.AccessLost;
                        _lastHr = hr;
                        FpsTrace("RunSession: AcquireNextFrame access lost");
                        break; // mode change / lost access -> re-init
                    }
                    else
                    {
                        _lastFail = FailStage.Dup;
                        _lastHr = hr;
                        FpsTrace("RunSession: AcquireNextFrame failed hr=0x" + unchecked((uint)hr).ToString("X8"));
                        break; // unknown error -> bail out of this session
                    }

                    if (sw.ElapsedMilliseconds >= 1000)
                    {
                        // Clamp for sanity: on some setups AccumulatedFrames reports
                        // wildly implausible numbers (>1000) due to driver quirks.
                        long fpsVal = presents;
                        if (fpsVal < 0) fpsVal = 0;
                        if (fpsVal > 999) fpsVal = 999;
                        _currentFps = unchecked((int)fpsVal);
                        FpsTrace("RunSession: FPS=" + _currentFps + " accumulatedFrames=" + fpsVal);
                        presents = 0;
                        sw.Restart();
                    }

                    Thread.Sleep(4); // ~250 Hz sampling (cheap) to avoid missing frames
                }
                FpsTrace("RunSession: acquisition loop ended (cancelled=" + _cts.IsCancellationRequested + ", rebuild=" + _rebuild + ")");
            }
            catch (Exception ex)
            {
                _available = false;
                _lastFail = FailStage.Dup;
                _lastHr = unchecked((int)0x80004005u);
                FpsTrace("RunSession: exception " + ex.GetType().FullName + ": " + ex.Message);
            }
            finally
            {
                _available = false;
                if (frameBufPin.IsAllocated) frameBufPin.Free();
                ReleaseCom(dup);
                ReleaseCom(device);
                ReleaseCom(output1); // QI'd IDXGIOutput1 (separate refcount from base)
                ReleaseCom(output);
                ReleaseCom(adapter);
                ReleaseCom(factory);
            }
        }

        // Enumerate every adapter's outputs and return the one matching the target
        // DeviceName (e.g. \\.\DISPLAY1). The owning adapter is returned via
        // owningAdapter (kept alive for the caller to release); non-matched outputs
        // and adapters are released here.
        private IntPtr FindOutputByDeviceName(IntPtr factory, string target, out IntPtr owningAdapter)
        {
            owningAdapter = IntPtr.Zero;
            if (factory == IntPtr.Zero) { FpsTrace("FindOutputByDeviceName: null factory"); return IntPtr.Zero; }
            FpsTrace("FindOutputByDeviceName: ENTER target=[" + target + "] factory=0x" + factory.ToString("X8"));
            EnumAdapters1Del enumAdapters1 = GetDelegate<EnumAdapters1Del>(factory, SLOT_Factory_EnumAdapters1, "IDXGIFactory1.EnumAdapters1");
            if (enumAdapters1 == null) return IntPtr.Zero;

            IntPtr firstAdapter = IntPtr.Zero;
            IntPtr firstOutput = IntPtr.Zero;
            bool firstPairCaptured = false;

            for (int a = 0; ; a++)
            {
                IntPtr adapter = IntPtr.Zero;
                FpsTrace("FindOutputByDeviceName: enumAdapters1(a=" + a + ")");
                int hr = enumAdapters1(factory, a, out adapter);
                FpsTrace("FindOutputByDeviceName: a=" + a + " hr=0x" + unchecked((uint)hr).ToString("X8") + " adapter=0x" + adapter.ToString("X8"));
                if (hr == DXGI_ERROR_NOT_FOUND || adapter == IntPtr.Zero) break;

                bool keepAdapter = false;
                try
                {
                    EnumOutputsDel enumOutputs = GetDelegate<EnumOutputsDel>(adapter, SLOT_Adapter_EnumOutputs, "IDXGIAdapter.EnumOutputs");
                    if (enumOutputs == null) break;
                    for (int o = 0; ; o++)
                    {
                        IntPtr output = IntPtr.Zero;
                        FpsTrace("FindOutputByDeviceName: a=" + a + " enumOutputs(o=" + o + ")");
                        hr = enumOutputs(adapter, o, out output);
                        FpsTrace("FindOutputByDeviceName: a=" + a + " o=" + o + " hr=0x" + unchecked((uint)hr).ToString("X8") + " output=0x" + output.ToString("X8"));
                        if (hr == DXGI_ERROR_NOT_FOUND || output == IntPtr.Zero) break;

                        // Save the first (adapter, output) pair for fallback — this is
                        // almost always the primary display for the LUID adapter 0.
                        if (!firstPairCaptured)
                        {
                            firstAdapter = adapter;
                            firstOutput = output;
                            firstPairCaptured = true;
                            // we deliberately do NOT release here; either we use it
                            // as fallback, or we release below when adapter is freed.
                        }
                        else if (output != firstOutput || adapter != firstAdapter)
                        {
                            // not the captured pair — release if we don't match
                        }

                        bool matched = !string.IsNullOrEmpty(target)
                                       && NamesMatch(GetOutputDescDeviceName(output), target);

                        if (matched)
                        {
                            if (output == firstOutput && adapter == firstAdapter)
                            {
                                // Exactly the fallback pair — don't release them;
                                // matched return path owns them.
                                firstPairCaptured = false;
                            }
                            else
                            {
                                // Matched a different (adapter, output). Must release
                                // the captured fallback pair since nothing else will.
                                if (firstOutput != IntPtr.Zero) ReleaseCom(firstOutput);
                                if (firstAdapter != IntPtr.Zero) ReleaseCom(firstAdapter);
                                firstPairCaptured = false;
                            }
                            owningAdapter = adapter;
                            keepAdapter = true;
                            return output;
                        }
                        // Release unless this output+adapter is our captured fallback
                        // (we'll either return those later or release them in
                        // finally / post-loop).
                        if (output != firstOutput || adapter != firstAdapter)
                            ReleaseCom(output);
                    }
                }
                finally
                {
                    if (!keepAdapter && adapter != firstAdapter) ReleaseCom(adapter);
                }
            }

            // No match by name — return the first (adapter, output) we found as a
            // fallback. This at least lets the user see real FPS instead of "NA"
            // when DeviceName naming differs between WinForms Screen and DXGI.
            if (firstPairCaptured && firstAdapter != IntPtr.Zero && firstOutput != IntPtr.Zero)
            {
                owningAdapter = firstAdapter;
                return firstOutput;
            }
            return IntPtr.Zero;
        }

        private static string GetOutputDescDeviceName(IntPtr output)
        {
            if (output == IntPtr.Zero) return "";
            try
            {
                byte[] buf = new byte[512];
                GCHandle pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
                try
                {
                    OutputGetDescDel getDesc = GetDelegate<OutputGetDescDel>(output, SLOT_Output_GetDesc, "IDXGIOutput.GetDesc");
                    if (getDesc == null) return "";
                    FpsTrace("GetOutputDescDeviceName: invoking GetDesc output=0x" + output.ToString("X8") + " pDesc=0x" + pin.AddrOfPinnedObject().ToString("X8"));
                    int hr;
                    try { hr = getDesc(output, pin.AddrOfPinnedObject()); }
                    catch (Exception ex)
                    {
                        FpsTrace("GetOutputDescDeviceName: INVOKE EXC " + ex.GetType().Name + ": " + ex.Message);
                        throw;
                    }
                    FpsTrace("GetOutputDescDeviceName: hr=0x" + unchecked((uint)hr).ToString("X8"));
                    if (hr != S_OK) return "";
                    string name = Marshal.PtrToStringUni(pin.AddrOfPinnedObject(), 32) ?? "";
                    FpsTrace("GetOutputDescDeviceName: name=[" + StripNulls(name) + "]");
                    return name;
                }
                finally { pin.Free(); }
            }
            catch (Exception ex) { FpsTrace("GetOutputDescDeviceName: OUTER EXC " + ex.GetType().Name + ": " + ex.Message); return ""; }
        }

        private static string StripNulls(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOf('\0');
            return (i >= 0 ? s.Substring(0, i) : s).Trim();
        }

        private static bool NamesMatch(string dxgiName, string target)
        {
            if (string.IsNullOrEmpty(dxgiName) || string.IsNullOrEmpty(target)) return false;
            string a = StripNulls(dxgiName).Replace("\\", "").Replace(".", "").Trim().ToUpperInvariant();
            string b = StripNulls(target).Replace("\\", "").Replace(".", "").Trim().ToUpperInvariant();
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;
            // Last-resort suffix match: some drivers expose longer paths, but
            // "DISPLAY1" suffix should still match. E.g. if a="DISPLAY2" and
            // b="DISPLAY2" they already matched above; if a="\??\DISPLAY2" and
            // user's target is "DISPLAY2", the suffix fallback kicks in.
            if (!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b))
            {
                if (a.EndsWith(b, StringComparison.Ordinal)) return true;
                if (b.EndsWith(a, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // Fallback: the monitor's current refresh rate (Hz). Used when DXGI sampling
        // is unavailable. Returns 0 if it cannot be read.
        public static int GetRefreshRateHz(string deviceName)
        {
            try
            {
                Win32.DEVMODE dm = new Win32.DEVMODE();
                dm.dmDeviceName = new string('\0', 32);
                dm.dmSize = 220;
                if (!Win32.EnumDisplaySettingsW(deviceName, Win32.ENUM_CURRENT_SETTINGS, ref dm))
                    return 0;
                return (int)dm.dmDisplayFrequency;
            }
            catch { return 0; }
        }

        public void Dispose()
        {
            Stop();
            if (_thread != null && _thread.IsAlive) _thread.Join();
            if (_windowThread != null && _windowThread.IsAlive) _windowThread.Join();
            _cts.Dispose();
            DestroyDwmAnchor();
        }
    }
}

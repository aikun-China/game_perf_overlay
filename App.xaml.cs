using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace PerfMonitor
{
    // Application entry point. No main window: on first run it shows two picker
    // dialogs (FPS screen + overlay screen), then minimizes to the tray. The only
    // always-visible UI is the translucent overlay; the tray offers Settings / Exit.
    public partial class App : Application
    {
        // Previously we forced a black console window (AllocConsole) for startup
        // diagnostics. User has explicitly asked to hide it; we now log silently
        // to the current session log without creating any visible console HWND.
        // The AttachConsole/AllocConsole P/Invokes are intentionally left declared
        // (they're harmless when never called) so nothing breaks at JIT time.
        [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);
        [DllImport("kernel32.dll")] private static extern bool AllocConsole();
        private const int ATTACH_PARENT_PROCESS = -1;
        private const bool CONSOLE_VISIBLE = false;

        private static readonly string DebugMarkerPath = Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? @"D:\work\perfmon",
            "game_perf_overlay_debug.txt");

        private static bool _consoleReady;
        private static void TraceLine(string msg)
        {
            if (CONSOLE_VISIBLE && !_consoleReady)
            {
                try { if (!AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole(); } catch { }
                _consoleReady = true;
            }
            string line = "[" + System.DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg;
            if (CONSOLE_VISIBLE) { try { System.Console.WriteLine(line); } catch { } }
            try
            {
                SessionFiles.AppendLog(line + "\r\n");
            }
            catch { }
        }

        internal static void WriteCrashLog(string tag, System.Exception ex)
        {
            try
            {
                string text = "[" + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + tag
                    + "\r\nType: " + (ex == null ? "(null)" : ex.GetType().FullName)
                    + "\r\nMsg : " + (ex == null ? "" : ex.Message)
                    + "\r\nStack:\r\n" + (ex == null ? "" : ex.StackTrace)
                    + "\r\n----------------\r\n";
                SessionFiles.AppendLog(text);
                TraceLine("!! CRASH " + tag + ": " + (ex == null ? "null" : ex.GetType().Name) + " " + (ex == null ? "" : ex.Message));
                try { WinForms.MessageBox.Show(text, "game_perf_overlay CRASH (" + tag + ")",
                    WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Stop); } catch { }
            }
            catch { }
        }

        private AppConfig _cfg;
        private CpuMonitor _cpu;
        private MemoryMonitor _mem;
        private GpuMonitor _gpu;
        private FpsMonitor _fps;
        private MetricHistory _history;
        private CsvRecorder _csvRecorder;
        private DispatcherTimer _metricsTimer;
        private string _lastCsvError;
        private OverlayWindow _overlay;
        private List<ScreenInfo> _screens;
        private WinForms.NotifyIcon _tray;
        private SettingsWindow _settingsWin;
        private Mutex _singleMutex;
        private bool _exiting;

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                SessionFiles.Initialize(10, 0);
            }
            catch (System.Exception ex)
            {
                WinForms.MessageBox.Show("无法在程序目录创建本次运行的日志和 CSV 文件：\r\n" + ex.Message,
                    "game_perf_overlay 启动失败", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
                Shutdown();
                return;
            }
            TraceLine("App.OnStartup ENTER");
            // ---- Global crash catchers (attach BEFORE any user code) -------------
            System.AppDomain.CurrentDomain.UnhandledException += delegate (object s, System.UnhandledExceptionEventArgs ea)
            {
                WriteCrashLog("AppDomain.UnhandledException isTerminating=" + ea.IsTerminating,
                    ea.ExceptionObject as System.Exception ?? new System.Exception("non-exception object: " + (ea.ExceptionObject == null ? "null" : ea.ExceptionObject.ToString())));
            };
            DispatcherUnhandledException += delegate (object s, DispatcherUnhandledExceptionEventArgs ea)
            {
                WriteCrashLog("DispatcherUnhandledException", ea.Exception);
                ea.Handled = false; // still crash-after-log so we know it happened
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += delegate (object s, System.Threading.Tasks.UnobservedTaskExceptionEventArgs ea)
            {
                WriteCrashLog("UnobservedTaskException", ea.Exception);
                ea.SetObserved();
            };

            try { base.OnStartup(e); TraceLine("base.OnStartup OK"); }
            catch (System.Exception ex) { WriteCrashLog("base.OnStartup", ex); throw; }

            try
            {
                bool createdNew;
                TraceLine("creating single-instance mutex...");
                _singleMutex = new Mutex(true, @"Local\game_perf_overlay_SingleInstance_2026", out createdNew);
                if (!createdNew)
                {
                    TraceLine("another instance running -> Shutdown");
                    Shutdown();
                    return;
                }
                TraceLine("mutex OK");

                TraceLine("ConfigStore.Load...");
                _cfg = ConfigStore.Load();
                SessionFiles.ApplyRetention(_cfg.LogRetentionCount, _cfg.CsvRetentionCount);
                if (File.Exists(DebugMarkerPath)) { _cfg.FirstRunCompleted = true; TraceLine("debug marker present -> forced FirstRunCompleted=true (skipped dialogs)"); }
                TraceLine("config loaded: FirstRunCompleted=" + _cfg.FirstRunCompleted + " FpsEnabled=" + _cfg.FpsEnabled);

                TraceLine("ScreenService.Enumerate...");
                _screens = ScreenService.Enumerate();
                TraceLine("screens: " + _screens.Count);
                for (int i = 0; i < _screens.Count; i++) TraceLine("  #" + i + ": " + _screens[i].FriendlyName + " @ " + _screens[i].DeviceName);

                if (!_cfg.FirstRunCompleted)
                {
                    TraceLine("first run — showing pickers");
                    RunFirstRunPickers();
                    TraceLine("pickers done");
                    _cfg.FirstRunCompleted = true;
                    ConfigStore.Save(_cfg);
                    TraceLine("config saved");
                }

                TraceLine("creating CPU/MEM/GPU/FPS monitors...");
                _cpu = new CpuMonitor(); TraceLine("  CPU OK");
                _mem = new MemoryMonitor(); TraceLine("  MEM OK");
                _gpu = new GpuMonitor(); TraceLine("  GPU OK");
                _fps = new FpsMonitor(); TraceLine("  FPS OK");
                _history = new MetricHistory(_cpu, _mem, _gpu, _fps);
                _csvRecorder = new CsvRecorder();

                if (_cfg.FpsEnabled)
                {
                    TraceLine("StartFps(dev=" + _cfg.FpsDeviceName + ")");
                    StartFps();
                    TraceLine("StartFps OK");
                }

                TraceLine("creating OverlayWindow...");
                _overlay = new OverlayWindow(_cfg, _history, _fps, _screens);
                TraceLine("overlay.Show...");
                _overlay.Show();
                TraceLine("overlay shown");

                TraceLine("SetupTray...");
                SetupTray();
                _metricsTimer = new DispatcherTimer(DispatcherPriority.Background);
                _metricsTimer.Interval = TimeSpan.FromSeconds(1);
                _metricsTimer.Tick += OnMetricsTick;
                _metricsTimer.Start();
                TraceLine("CSV recording and 60-second FPS history started");
                TraceLine("OnStartup COMPLETE (no crash)");
            }
            catch (System.Exception ex) { WriteCrashLog("OnStartup body", ex); throw; }
        }

        // ---- First run: two option cards ----------------------------------------

        private void RunFirstRunPickers()
        {
            ScreenInfo primary = _screens.FirstOrDefault(s => s.IsPrimary);
            if (primary == null) primary = _screens.FirstOrDefault();

            // Card 1: which screen's FPS to monitor.
            if (_screens.Count > 0)
            {
                List<ScreenPickerDialog.PickerOption> fpsOptions = _screens
                    .Select(s => new ScreenPickerDialog.PickerOption { Label = s.FriendlyName, Value = s.DeviceName })
                    .ToList();
                string def = !string.IsNullOrEmpty(_cfg.FpsDeviceName)
                    ? _cfg.FpsDeviceName
                    : (primary != null ? primary.DeviceName : _screens[0].DeviceName);
                ScreenPickerDialog d1 = new ScreenPickerDialog("选择 FPS 监控屏幕",
                    "请选择要监控哪块屏幕的 FPS（桌面内容每秒刷新帧数）：", fpsOptions, def);
                bool? ok1 = d1.ShowDialog();
                _cfg.FpsDeviceName = (ok1 == true && !string.IsNullOrEmpty(d1.Result)) ? d1.Result : def;
            }

            // Card 2: which monitor the overlay floats on.
            List<ScreenPickerDialog.PickerOption> ovOptions = new List<ScreenPickerDialog.PickerOption>
            {
                new ScreenPickerDialog.PickerOption { Label = "自动 (检测小副屏)", Value = "Auto" },
                new ScreenPickerDialog.PickerOption { Label = "主屏",               Value = "Main" }
            };
            foreach (ScreenInfo s in _screens)
                ovOptions.Add(new ScreenPickerDialog.PickerOption { Label = s.FriendlyName, Value = s.DeviceName });

            string ovDef = !string.IsNullOrEmpty(_cfg.OverlayDeviceName)
                ? _cfg.OverlayDeviceName
                : (_cfg.OverlayTargetMode == "Main" ? "Main" : "Auto");
            ScreenPickerDialog d2 = new ScreenPickerDialog("选择悬浮窗显示器",
                "请选择悬浮窗显示在哪个显示器上（之后可左键拖动调整，重启自动恢复位置）：",
                ovOptions, ovDef);
            bool? ok2 = d2.ShowDialog();
            string r = (ok2 == true && !string.IsNullOrEmpty(d2.Result)) ? d2.Result : ovDef;
            if (r == "Auto" || r == "Main")
            {
                _cfg.OverlayTargetMode = r;
                _cfg.OverlayDeviceName = "";
            }
            else
            {
                _cfg.OverlayTargetMode = "Specific";
                _cfg.OverlayDeviceName = r;
            }
        }

        // ---- FPS lifecycle ------------------------------------------------------

        private void StartFps()
        {
            string dev = _cfg.FpsDeviceName;
            if (string.IsNullOrEmpty(dev))
            {
                ScreenInfo primary = _screens.FirstOrDefault(s => s.IsPrimary);
                if (primary == null) primary = _screens.FirstOrDefault();
                dev = primary != null ? primary.DeviceName : "";
            }
            _fps.Start(dev);
        }

        // ---- Tray ---------------------------------------------------------------

        private void SetupTray()
        {
            _tray = new WinForms.NotifyIcon();
            _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? System.Drawing.SystemIcons.Application;
            _tray.Visible = true;
            _tray.Text = "game_perf_overlay";
            WinForms.ContextMenuStrip menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("设置...", null, new System.EventHandler(delegate (object s, System.EventArgs ea) { OpenSettings(); }));
            menu.Items.Add("打开 CSV 记录文件夹", null, new System.EventHandler(delegate (object s, System.EventArgs ea)
            {
                try { Process.Start(CsvRecorder.CsvDirectory); }
                catch (System.Exception ex) { ShowCsvError("打开 CSV 文件夹失败", ex); }
            }));
            menu.Items.Add("导出本次 CSV...", null, new System.EventHandler(delegate (object s, System.EventArgs ea) { ExportCsv(); }));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("退出", null, new System.EventHandler(delegate (object s, System.EventArgs ea) { ExitApp(); }));
            _tray.ContextMenuStrip = menu;
            _tray.MouseClick += new WinForms.MouseEventHandler(delegate (object s, WinForms.MouseEventArgs ea)
            {
                if (ea.Button == WinForms.MouseButtons.Left) OpenSettings();
            });
        }

        private void OpenSettings()
        {
            if (_settingsWin != null && _settingsWin.IsLoaded)
            {
                _settingsWin.Activate();
                return;
            }

            // Re-enumerate in case the monitor layout changed since launch.
            _screens = ScreenService.Enumerate();
            if (_overlay != null) _overlay.SetScreens(_screens);

            _settingsWin = new SettingsWindow(_cfg, _screens, _history, _csvRecorder);
            bool? ok = _settingsWin.ShowDialog();
            SettingsWindow win = _settingsWin;
            _settingsWin = null;
            if (ok != true) return;
            ApplySettings(win.Result);
        }

        private void ApplySettings(AppConfig r)
        {
            if (r == null) return;
            bool fpsScreenChanged = r.FpsDeviceName != _cfg.FpsDeviceName;
            bool fpsEnabledChanged = r.FpsEnabled != _cfg.FpsEnabled;
            bool overlayScreenChanged = r.OverlayTargetMode != _cfg.OverlayTargetMode
                                        || r.OverlayDeviceName != _cfg.OverlayDeviceName;

            _cfg.UpdateFrom(r);
            if (overlayScreenChanged) _cfg.HasLastPosition = false; // drop old-screen coords; use default placement on the new screen
            ConfigStore.Save(_cfg);
            try
            {
                SessionFiles.ApplyRetention(_cfg.LogRetentionCount, _cfg.CsvRetentionCount);
            }
            catch (System.Exception ex)
            {
                TraceLine("file retention cleanup failed: " + ex.GetType().FullName + ": " + ex.Message);
                ShowCsvError("清理旧日志或 CSV 文件失败", ex);
            }

            if (_overlay != null) _overlay.ApplyAppearance();
            if (overlayScreenChanged && _overlay != null) _overlay.Reposition();

            if (_cfg.FpsEnabled)
            {
                if (fpsScreenChanged || fpsEnabledChanged) StartFps();
            }
            else
            {
                if (fpsEnabledChanged) _fps.Stop();
            }
        }

        private void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;
            try { if (_metricsTimer != null) { _metricsTimer.Stop(); _metricsTimer.Tick -= OnMetricsTick; } } catch { }
            try { if (_csvRecorder != null) _csvRecorder.Dispose(); } catch (System.Exception ex) { TraceLine("CSV close failed: " + ex.Message); }
            try { if (_fps != null) _fps.Dispose(); } catch { }
            try { if (_gpu != null) _gpu.Dispose(); } catch { }
            try
            {
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            }
            catch { }
            try { if (_overlay != null) _overlay.Close(); } catch { }
            Shutdown();
        }

        internal void ExitForUpdate()
        {
            ExitApp();
        }

        private void OnMetricsTick(object sender, EventArgs e)
        {
            try
            {
                MetricSample sample = _history.Capture();
                _csvRecorder.Write(sample);
                if (_lastCsvError != null)
                {
                    TraceLine("CSV recording recovered");
                    _lastCsvError = null;
                }
            }
            catch (System.Exception ex)
            {
                string error = ex.GetType().FullName + ": " + ex.Message;
                if (!string.Equals(error, _lastCsvError, StringComparison.Ordinal))
                {
                    TraceLine("CSV recording failed: " + error);
                    ShowCsvError("CSV 记录失败", ex);
                    _lastCsvError = error;
                }
            }
        }

        private void ExportCsv()
        {
            Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog();
            dialog.Title = "导出本次运行的监控 CSV";
            dialog.Filter = "CSV 文件 (*.csv)|*.csv";
            dialog.FileName = Path.GetFileName(_csvRecorder.CurrentPath);
            if (dialog.ShowDialog() != true) return;
            try
            {
                _csvRecorder.ExportTo(dialog.FileName);
                WinForms.MessageBox.Show("CSV 已导出到：\r\n" + dialog.FileName, "导出完成",
                    WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Information);
            }
            catch (System.Exception ex)
            {
                ShowCsvError("导出 CSV 失败", ex);
            }
        }

        private void ShowCsvError(string message, System.Exception ex)
        {
            string details = message + "：" + ex.Message;
            if (_tray != null && _tray.Visible)
                _tray.ShowBalloonTip(5000, "game_perf_overlay", details, WinForms.ToolTipIcon.Error);
            else
                WinForms.MessageBox.Show(details, "game_perf_overlay", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
        }
    }
}

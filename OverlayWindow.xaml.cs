using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using System.Windows.Threading;

namespace PerfMonitor
{
    // The translucent always-on-top overlay. Frameless, no taskbar icon, topmost but
    // never steals focus (WS_EX_NOACTIVATE). Left-drag to move; position is remembered
    // in virtual-desktop pixels and restored on next launch. Content is one compact
    // row: CPU / MEM / GPU / FPS, each coloured by load (or a single custom colour).
    public partial class OverlayWindow : Window
    {
        private readonly AppConfig _cfg;
        private readonly MetricHistory _history;
        private readonly FpsMonitor _fps;
        private List<ScreenInfo> _screens;

        private readonly DispatcherTimer _timer;
        private IntPtr _hwnd;
        private bool _dragging;
        private Win32.POINT _dragOffset;
        private string _lastRender = "";

        private static readonly Color Green = Color.FromRgb(0x00, 0xE6, 0x76);
        private static readonly Color Yellow = Color.FromRgb(0xFF, 0xD6, 0x00);
        private static readonly Color Red = Color.FromRgb(0xFF, 0x52, 0x52);
        private static readonly Color Dim = Color.FromRgb(0x80, 0x80, 0x80);

        internal OverlayWindow(AppConfig cfg, MetricHistory history, FpsMonitor fps, List<ScreenInfo> screens)
        {
            InitializeComponent();
            _cfg = cfg; _history = history; _fps = fps; _screens = screens;
            FpsChart.History = history;
            _timer = new DispatcherTimer(DispatcherPriority.Background);
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += OnTick;
            ApplyAppearance();
        }

        // Flip between horizontal (one row, many items) and vertical (one per row).
        // Also adjusts inter-item spacing: horizontal gaps on the left; vertical gaps on the top.
        private void ApplyLayoutMode()
        {
            bool vertical = string.Equals(_cfg.LayoutMode, "Vertical", StringComparison.OrdinalIgnoreCase);
            ContentPanel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
            UIElementCollection children = ContentPanel.Children;
            // Find the first visible child so we can skip its spacing (prevents leading gap).
            int firstVisible = -1;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i].Visibility != Visibility.Collapsed) { firstVisible = i; break; }
            }
            for (int i = 0; i < children.Count; i++)
            {
                FrameworkElement fe = children[i] as FrameworkElement;
                if (fe == null) continue;
                if (i == firstVisible)
                {
                    fe.Margin = new Thickness(0);
                }
                else if (vertical)
                {
                    fe.Margin = new Thickness(0, 4, 0, 0);
                }
                else
                {
                    fe.Margin = new Thickness(12, 0, 0, 0);
                }
            }
        }

        // Re-apply font size / background opacity / refresh interval / layout without repositioning.
        public void ApplyAppearance()
        {
            this.FontSize = Util.Clamp(_cfg.FontSize, 8, 32);
            byte a = Util.ClampToByte((int)Math.Round(_cfg.BgOpacity * 255));
            Root.Background = new SolidColorBrush(Color.FromArgb(a, 0, 0, 0));
            _timer.Interval = TimeSpan.FromMilliseconds(Util.Clamp(_cfg.RefreshMs, 500, 2000));
            ApplyLayoutMode();
        }

        public void SetScreens(List<ScreenInfo> screens) { _screens = screens; }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32.MakeOverlayNoActivate(_hwnd);
            Reposition();
            try { _fps.SetOverlayHwndHint(_hwnd); } catch { }
            _timer.Start();
        }

        public void Reposition()
        {
            if (_hwnd == IntPtr.Zero) return;
            ScreenInfo screen = ScreenService.ResolveOverlayScreen(_cfg, _screens);
            int x, y;
            if (_cfg.HasLastPosition && IsOnVirtualDesktop(_cfg.OverlayX, _cfg.OverlayY))
            {
                x = _cfg.OverlayX; y = _cfg.OverlayY;
            }
            else
            {
                System.Drawing.Point p = ScreenService.DefaultPlacement(screen);
                x = p.X; y = p.Y;
            }
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        }

        private static bool IsOnVirtualDesktop(int x, int y)
        {
            try
            {
                System.Windows.Forms.Screen vsSrc = System.Windows.Forms.Screen.PrimaryScreen;
                System.Drawing.Rectangle vs = System.Windows.Forms.SystemInformation.VirtualScreen;
                return x >= vs.Left && x <= vs.Right && y >= vs.Top && y <= vs.Bottom;
            }
            catch { return false; }
        }

        private struct Cell
        {
            public string Text;
            public Color Color;
            public bool Visible;
            public bool NumericFps;  // true = treat as number; false = N/A etc.
            public int FpsValue;
        }

        private void OnTick(object sender, EventArgs e)
        {
            bool custom = string.Equals(_cfg.ColorMode, "Custom", StringComparison.OrdinalIgnoreCase);
            Color customColor = ParseColor(_cfg.TextColor);

            MetricSample sample = _history != null ? _history.Latest : null;
            float cpu = sample != null ? sample.Cpu : -1f;
            float mem = sample != null ? sample.Memory : -1f;
            float gpu = sample != null ? sample.Gpu : -1f;
            int fps = sample != null ? sample.Fps : 0;
            bool fpsAvailable = sample != null && sample.FpsAvailable;

            Cell cpuC = MakeCell("CPU:", _cfg.CpuEnabled, cpu, custom, customColor, true);
            Cell memC = MakeCell("MEM:", _cfg.MemEnabled, mem, custom, customColor, true);
            Cell gpuC = MakeCell("GPU:", _cfg.GpuEnabled, gpu, custom, customColor, true);
            Cell fpsC;
            if (!_cfg.FpsEnabled)
            {
                fpsC = new Cell { Visible = false };
            }
            else if (!fpsAvailable)
            {
                // Show FPS: -- until the selected display backend returns a sample.
                fpsC = new Cell
                {
                    Visible = true,
                    Text = "FPS: --",
                    Color = Dim,
                    NumericFps = false,
                    FpsValue = -1
                };
            }
            else
            {
                fpsC = new Cell
                {
                    Visible = true,
                    Text = FpsText(fps),
                    Color = custom ? customColor : FpsColor(fps),
                    NumericFps = true,
                    FpsValue = fps
                };
            }
            Cell fpsSourceC = new Cell
            {
                Visible = _cfg.FpsEnabled && _cfg.ShowFpsWindowName,
                Text = sample != null ? sample.Source : "桌面",
                Color = Dim
            };
            FpsChart.Visibility = _cfg.FpsEnabled ? Visibility.Visible : Visibility.Collapsed;
            FpsChart.InvalidateVisual();

            // Build time cells. In vertical layout, date and time go on separate
            // lines (DateTb + TimeTb). In horizontal layout, they combine into a
            // single TimeTb to stay on one row.
            Cell dateC, timeC;
            if (!_cfg.ShowTime)
            {
                dateC = new Cell { Visible = false };
                timeC = new Cell { Visible = false };
            }
            else
            {
                DateTime now = DateTime.Now;
                bool vertical = string.Equals(_cfg.LayoutMode, "Vertical", StringComparison.OrdinalIgnoreCase);
                Color timeColor = custom ? customColor : Color.FromRgb(0xB0, 0xB0, 0xB0);
                string timePart = _cfg.ShowSeconds ? now.ToString("HH:mm:ss") : now.ToString("HH:mm");

                if (vertical && _cfg.ShowDate)
                {
                    // Separate lines: date on top, time below.
                    dateC = new Cell { Visible = true, Text = now.ToString("MM-dd"), Color = timeColor };
                    timeC = new Cell { Visible = true, Text = timePart, Color = timeColor };
                }
                else
                {
                    // Horizontal, or vertical without date: merge into one cell.
                    dateC = new Cell { Visible = false };
                    StringBuilder tb = new StringBuilder();
                    if (_cfg.ShowDate) tb.Append(now.ToString("MM-dd "));
                    tb.Append(timePart);
                    timeC = new Cell { Visible = true, Text = tb.ToString(), Color = timeColor };
                }
            }

            // Signature so we only touch the UI when something actually changed.
            StringBuilder sb = new StringBuilder();
            sb.Append(cpuC.Visible).Append(cpuC.Text).Append('|');
            sb.Append(memC.Visible).Append(memC.Text).Append('|');
            sb.Append(gpuC.Visible).Append(gpuC.Text).Append('|');
            sb.Append(fpsC.Visible).Append(fpsC.Text).Append('|');
            sb.Append(fpsSourceC.Visible).Append(fpsSourceC.Text).Append('|');
            sb.Append(dateC.Visible).Append(dateC.Text).Append('|');
            sb.Append(timeC.Visible).Append(timeC.Text);
            string sig = sb.ToString();
            if (sig == _lastRender) return;
            _lastRender = sig;

            ApplyCell(CpuTb, cpuC);
            ApplyCell(MemTb, memC);
            ApplyCell(GpuTb, gpuC);
            ApplyCell(FpsTb, fpsC);
            ApplyCell(FpsSourceTb, fpsSourceC);
            FpsGroup.Visibility = fpsC.Visible ? Visibility.Visible : Visibility.Collapsed;
            ApplyCell(DateTb, dateC);
            ApplyCell(TimeTb, timeC);
            ApplyLayoutMode();
        }

        private static void ApplyCell(System.Windows.Controls.TextBlock tb, Cell c)
        {
            if (!c.Visible) { tb.Visibility = Visibility.Collapsed; return; }
            tb.Visibility = Visibility.Visible;
            tb.Text = c.Text;
            tb.Foreground = new SolidColorBrush(c.Color);
        }

        private static Cell MakeCell(string label, bool enabled, float v, bool custom, Color customColor, bool isPct)
        {
            if (!enabled) return new Cell { Visible = false };
            string num = v < 0 ? "--" : ((int)Math.Round(v)).ToString().PadLeft(3);
            string suffix = v < 0 ? " " : (isPct ? "%" : " ");
            return new Cell
            {
                Text = label + num + suffix,
                Color = custom ? customColor : PctColor(v),
                Visible = true
            };
        }

        private static string FpsText(int f)
        {
            string num = f <= 0 ? "--" : f.ToString().PadLeft(3);
            return "FPS:" + num;
        }

        private static Color PctColor(float v)
        {
            if (v < 0) return Dim;
            if (v < 50) return Green;
            if (v < 80) return Yellow;
            return Red;
        }

        private static Color FpsColor(int f)
        {
            if (f <= 0) return Dim;
            if (f >= 55) return Green;
            if (f >= 30) return Yellow;
            return Red;
        }

        private static Color ParseColor(string hex)
        {
            try
            {
                string s = (hex ?? "").TrimStart('#');
                if (s.Length == 6)
                    return Color.FromRgb(
                        Convert.ToByte(s.Substring(0, 2), 16),
                        Convert.ToByte(s.Substring(2, 2), 16),
                        Convert.ToByte(s.Substring(4, 2), 16));
                if (s.Length == 8)
                    return Color.FromArgb(
                        Convert.ToByte(s.Substring(0, 2), 16),
                        Convert.ToByte(s.Substring(2, 2), 16),
                        Convert.ToByte(s.Substring(4, 2), 16),
                        Convert.ToByte(s.Substring(6, 2), 16));
            }
            catch { }
            return Colors.White;
        }

        // ---- Drag-to-move (manual, in physical pixels via Win32) ----------------

        private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_hwnd == IntPtr.Zero) return;
            _dragging = true;
            Win32.POINT cur;
            Win32.RECT wr;
            Win32.GetCursorPos(out cur);
            Win32.GetWindowRect(_hwnd, out wr);
            _dragOffset = new Win32.POINT { X = cur.X - wr.Left, Y = cur.Y - wr.Top };
            Root.CaptureMouse();
            e.Handled = true;
        }

        private void Root_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || _hwnd == IntPtr.Zero) return;
            Win32.POINT cur;
            Win32.GetCursorPos(out cur);
            int x = cur.X - _dragOffset.X;
            int y = cur.Y - _dragOffset.Y;
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            e.Handled = true;
        }

        private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            Root.ReleaseMouseCapture();
            if (_hwnd != IntPtr.Zero)
            {
                Win32.RECT wr;
                if (Win32.GetWindowRect(_hwnd, out wr))
                {
                    _cfg.OverlayX = wr.Left;
                    _cfg.OverlayY = wr.Top;
                    _cfg.HasLastPosition = true;
                    ConfigStore.Save(_cfg);
                }
            }
            e.Handled = true;
        }
    }
}

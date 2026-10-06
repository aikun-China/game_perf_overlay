using System;
using System.Web.Script.Serialization;

namespace PerfMonitor
{
    // Persistent configuration. Stored as JSON under %AppData%\PerfMonitor\config.json
    public sealed class AppConfig
    {
        public AppConfig()
        {
            CpuEnabled = true;
            MemEnabled = true;
            GpuEnabled = true;
            FpsEnabled = true;
            ShowTime = true;
            ShowDate = true;
            ShowSeconds = true;
            FontSize = 16;
            ColorMode = "Load";
            TextColor = "#E6E6E6";
            BgOpacity = 0.5;
            RefreshMs = 1000;
            LayoutMode = "Horizontal";
            OverlayTargetMode = "Auto";
            OverlayDeviceName = "";
            FpsDeviceName = "";
            ShowFpsWindowName = false;
            HasLastPosition = false;
            OverlayX = 0;
            OverlayY = 0;
            FirstRunCompleted = false;
            LogRetentionCount = 10;
            CsvRetentionCount = 0;
        }

        // Monitor enable flags (at least one must stay on).
        public bool CpuEnabled { get; set; }
        public bool MemEnabled { get; set; }
        public bool GpuEnabled { get; set; }
        public bool FpsEnabled { get; set; }

        // Time display: master toggle + date/seconds sub-toggles.
        public bool ShowTime { get; set; }
        public bool ShowDate { get; set; }
        public bool ShowSeconds { get; set; }

        // Appearance.
        public double FontSize { get; set; }
        public string ColorMode { get; set; }
        public string TextColor { get; set; }
        public double BgOpacity { get; set; }
        public int RefreshMs { get; set; }
        // "Horizontal" = one row multiple items; "Vertical" = one item per row, multi-line
        public string LayoutMode { get; set; }

        // Overlay placement target.
        public string OverlayTargetMode { get; set; }
        public string OverlayDeviceName { get; set; }

        // Which screen's content FPS to measure (by WinForms DeviceName, e.g. "\\.\DISPLAY1").
        public string FpsDeviceName { get; set; }
        public bool ShowFpsWindowName { get; set; }

        // Last overlay position in virtual-desktop pixels (restored on next launch).
        public bool HasLastPosition { get; set; }
        public int OverlayX { get; set; }
        public int OverlayY { get; set; }

        // Whether the first-run pickers have already been completed.
        public bool FirstRunCompleted { get; set; }

        // Number of session files to retain; zero means unlimited.
        public int LogRetentionCount { get; set; }
        public int CsvRetentionCount { get; set; }

        public AppConfig Clone()
        {
            return (AppConfig)MemberwiseClone();
        }

        public void UpdateFrom(AppConfig src)
        {
            if (src == null) return;
            CpuEnabled = src.CpuEnabled;
            MemEnabled = src.MemEnabled;
            GpuEnabled = src.GpuEnabled;
            FpsEnabled = src.FpsEnabled;
            ShowTime = src.ShowTime;
            ShowDate = src.ShowDate;
            ShowSeconds = src.ShowSeconds;
            FontSize = src.FontSize;
            ColorMode = src.ColorMode;
            TextColor = src.TextColor;
            BgOpacity = src.BgOpacity;
            RefreshMs = src.RefreshMs;
            LayoutMode = src.LayoutMode;
            OverlayTargetMode = src.OverlayTargetMode;
            OverlayDeviceName = src.OverlayDeviceName;
            FpsDeviceName = src.FpsDeviceName;
            ShowFpsWindowName = src.ShowFpsWindowName;
            HasLastPosition = src.HasLastPosition;
            OverlayX = src.OverlayX;
            OverlayY = src.OverlayY;
            FirstRunCompleted = src.FirstRunCompleted;
            LogRetentionCount = src.LogRetentionCount;
            CsvRetentionCount = src.CsvRetentionCount;
        }

        private static readonly JavaScriptSerializer Js = new JavaScriptSerializer();

        public string ToJson()
        {
            return Js.Serialize(this);
        }

        public static AppConfig FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new AppConfig();
            try
            {
                AppConfig cfg = Js.Deserialize<AppConfig>(json);
                if (cfg == null) return new AppConfig();
                cfg.LogRetentionCount = Math.Max(0, cfg.LogRetentionCount);
                cfg.CsvRetentionCount = Math.Max(0, cfg.CsvRetentionCount);
                return cfg;
            }
            catch
            {
                return new AppConfig();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Point = System.Drawing.Point;

namespace PerfMonitor
{
    // Describes one physical display in virtual-desktop pixel coordinates.
    public sealed class ScreenInfo
    {
        public ScreenInfo()
        {
            DeviceName = "";
            FriendlyName = "";
            Bounds = new Rectangle(0, 0, 0, 0);
            IsPrimary = false;
            IsSmall = false;
        }

        public string DeviceName { get; set; }
        public string FriendlyName { get; set; }
        public Rectangle Bounds { get; set; }
        public bool IsPrimary { get; set; }
        public bool IsSmall { get; set; }

        public override string ToString() { return FriendlyName; }
    }

    // Enumerates monitors via WinForms Screen.AllScreens and resolves placement targets.
    internal static class ScreenService
    {
        public static List<ScreenInfo> Enumerate()
        {
            List<ScreenInfo> list = new List<ScreenInfo>();
            Screen[] all = Screen.AllScreens;
            Screen[] screens = all ?? new Screen[0];
            // Sort for a stable order: left-to-right then top-to-bottom.
            IOrderedEnumerable<Screen> ordered = screens.OrderBy(delegate (Screen s) { return s.Bounds.X; })
                                                        .ThenBy(delegate (Screen s) { return s.Bounds.Y; });
            Screen[] arr = ordered.ToArray();
            for (int i = 0; i < arr.Length; i++)
            {
                Screen s = arr[i];
                bool small = s.Bounds.Width <= 640 && s.Bounds.Height <= 480;
                string tag = s.Primary ? "(主屏)" : (small ? "(小副屏)" : "");
                list.Add(new ScreenInfo
                {
                    DeviceName = s.DeviceName ?? "",
                    FriendlyName = (string.Format("显示器 {0}  {1}x{2} {3}", i + 1, s.Bounds.Width, s.Bounds.Height, tag)).Trim(),
                    Bounds = s.Bounds,
                    IsPrimary = s.Primary,
                    IsSmall = small
                });
            }
            return list;
        }

        public static ScreenInfo ResolveOverlayScreen(AppConfig cfg, List<ScreenInfo> screens)
        {
            if (screens == null || screens.Count == 0) return null;

            ScreenInfo primary = screens.FirstOrDefault(delegate (ScreenInfo s) { return s.IsPrimary; });

            switch (cfg.OverlayTargetMode)
            {
                case "Main":
                    return primary ?? screens[0];
                case "Specific":
                    {
                        string name = cfg.OverlayDeviceName ?? "";
                        ScreenInfo byName = screens.FirstOrDefault(delegate (ScreenInfo s) { return string.Equals(s.DeviceName, name, StringComparison.OrdinalIgnoreCase); });
                        return byName ?? primary ?? screens[0];
                    }
                case "Auto":
                default:
                    ScreenInfo small = screens.FirstOrDefault(delegate (ScreenInfo s) { return s.IsSmall && !s.IsPrimary; });
                    return small ?? primary ?? screens[0];
            }
        }

        public static ScreenInfo ResolveFpsScreen(AppConfig cfg, List<ScreenInfo> screens)
        {
            if (screens == null || screens.Count == 0) return null;
            ScreenInfo byName = screens.FirstOrDefault(delegate (ScreenInfo s) { return string.Equals(s.DeviceName, cfg.FpsDeviceName, StringComparison.OrdinalIgnoreCase); });
            ScreenInfo primary = screens.FirstOrDefault(delegate (ScreenInfo s) { return s.IsPrimary; });
            return byName ?? primary ?? screens[0];
        }

        public static Point DefaultPlacement(ScreenInfo screen, int assumedWidth = 170)
        {
            if (screen == null) return new Point(8, 8);
            int x = screen.Bounds.X + Math.Max(0, (screen.Bounds.Width - assumedWidth) / 2);
            int y = screen.Bounds.Y + 2;
            return new Point(x, y);
        }
    }
}

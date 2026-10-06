using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;

namespace PerfMonitor
{
    // Settings dialog. Builds an updated AppConfig in Result on OK; the App applies it
    // (appearance, screen layout, FPS lifecycle) and persists. Position memory and the
    // first-run flag are preserved from the incoming config.
    public partial class SettingsWindow : Window
    {
        private readonly AppConfig _cfg;
        private readonly List<ScreenInfo> _screens;
        private readonly MetricHistory _history;
        private readonly CsvRecorder _csvRecorder;
        private readonly DispatcherTimer _historyTimer;
        private BackgroundWorker _updateWorker;

        private AppConfig _result;
        public AppConfig Result { get { return _result; } }

        internal SettingsWindow(AppConfig cfg, List<ScreenInfo> screens, MetricHistory history, CsvRecorder csvRecorder)
        {
            InitializeComponent();
            _cfg = cfg;
            _screens = screens;
            _history = history;
            _csvRecorder = csvRecorder;
            HistoryChart.History = history;
            CsvPathText.Text = "本次 CSV：" + _csvRecorder.CurrentPath;
            FooterVersionText.Text = "愛君_aikun  ·  " + AppVersion.Display;
            _historyTimer = new DispatcherTimer(DispatcherPriority.Background);
            _historyTimer.Interval = TimeSpan.FromSeconds(1);
            _historyTimer.Tick += delegate { HistoryChart.InvalidateVisual(); };
            Populate();
            ApplyFromConfig();
            UpdateLabels();
            UpdateColorPreview();
            UpdateCustomEnabled();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _historyTimer.Start();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _historyTimer.Stop();
        }

        private void OpenCsvFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(CsvRecorder.CsvDirectory);
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开 CSV 记录文件夹：\r\n" + ex.Message, "操作失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(SessionFiles.LogDirectory);
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开日志文件夹：\r\n" + ex.Message, "操作失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportCsv_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog();
            dialog.Title = "导出本次运行的监控 CSV";
            dialog.Filter = "CSV 文件 (*.csv)|*.csv";
            dialog.FileName = Path.GetFileName(_csvRecorder.CurrentPath);
            if (dialog.ShowDialog() != true) return;
            try
            {
                _csvRecorder.ExportTo(dialog.FileName);
                MessageBox.Show("CSV 已导出到：\r\n" + dialog.FileName, "导出完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出 CSV 失败：\r\n" + ex.Message, "操作失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (_updateWorker != null) return;
            CheckUpdatesButton.IsEnabled = false;
            CheckUpdatesButton.Content = "正在检查...";
            BackgroundWorker worker = new BackgroundWorker();
            _updateWorker = worker;
            worker.DoWork += delegate(object senderWorker, DoWorkEventArgs args)
            {
                args.Result = UpdateService.GetLatestRelease();
            };
            worker.RunWorkerCompleted += delegate(object senderWorker, RunWorkerCompletedEventArgs args)
            {
                _updateWorker = null;
                CheckUpdatesButton.IsEnabled = true;
                CheckUpdatesButton.Content = "检查更新...";
                if (args.Error != null)
                {
                    MessageBox.Show("检查更新失败：\r\n" + args.Error.Message, "检查更新",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                ReleaseInfo release = args.Result as ReleaseInfo;
                try
                {
                    if (!UpdateService.IsNewer(release))
                    {
                        MessageBox.Show("当前已是最新版本 " + AppVersion.Display + "。", "检查更新",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    UpdateDialog dialog = new UpdateDialog(release);
                    dialog.Owner = this;
                    if (dialog.ShowDialog() == true && dialog.UpdateRequested)
                        DownloadAndInstallUpdate(release);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("读取版本信息失败：\r\n" + ex.Message, "检查更新",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            worker.RunWorkerAsync();
        }

        private void DownloadAndInstallUpdate(ReleaseInfo release)
        {
            CheckUpdatesButton.IsEnabled = false;
            CheckUpdatesButton.Content = "正在下载...";
            BackgroundWorker worker = new BackgroundWorker();
            _updateWorker = worker;
            worker.DoWork += delegate(object senderWorker, DoWorkEventArgs args)
            {
                args.Result = UpdateService.DownloadInstaller(release);
            };
            worker.RunWorkerCompleted += delegate(object senderWorker, RunWorkerCompletedEventArgs args)
            {
                _updateWorker = null;
                if (args.Error != null)
                {
                    CheckUpdatesButton.IsEnabled = true;
                    CheckUpdatesButton.Content = "检查更新...";
                    MessageBox.Show("下载更新失败：\r\n" + args.Error.Message, "更新失败",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                try
                {
                    ProcessStartInfo startInfo = new ProcessStartInfo(
                        (string)args.Result, "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-");
                    startInfo.UseShellExecute = true;
                    startInfo.Verb = "runas";
                    Process.Start(startInfo);
                    App app = Application.Current as App;
                    if (app != null) app.ExitForUpdate();
                    else Application.Current.Shutdown();
                }
                catch (Exception ex)
                {
                    CheckUpdatesButton.IsEnabled = true;
                    CheckUpdatesButton.Content = "检查更新...";
                    MessageBox.Show("启动安装程序失败：\r\n" + ex.Message, "更新失败",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            worker.RunWorkerAsync();
        }

        private void Populate()
        {
            List<ScreenPickerDialog.PickerOption> overlay = new List<ScreenPickerDialog.PickerOption>
            {
                new ScreenPickerDialog.PickerOption { Label = "自动 (检测小副屏)", Value = "Auto" },
                new ScreenPickerDialog.PickerOption { Label = "主屏",               Value = "Main" }
            };
            foreach (ScreenInfo s in _screens)
                overlay.Add(new ScreenPickerDialog.PickerOption { Label = s.FriendlyName, Value = s.DeviceName });
            OverlayCombo.ItemsSource = overlay;

            List<ScreenPickerDialog.PickerOption> fps = new List<ScreenPickerDialog.PickerOption>();
            foreach (ScreenInfo s in _screens)
                fps.Add(new ScreenPickerDialog.PickerOption { Label = s.FriendlyName, Value = s.DeviceName });
            FpsCombo.ItemsSource = fps;
        }

        private void ApplyFromConfig()
        {
            CpuChk.IsChecked = _cfg.CpuEnabled;
            MemChk.IsChecked = _cfg.MemEnabled;
            GpuChk.IsChecked = _cfg.GpuEnabled;
            FpsChk.IsChecked = _cfg.FpsEnabled;
            ShowFpsWindowNameChk.IsChecked = _cfg.ShowFpsWindowName;

            TimeChk.IsChecked = _cfg.ShowTime;
            TimeDateChk.IsChecked = _cfg.ShowDate;
            TimeSecChk.IsChecked = _cfg.ShowSeconds;
            UpdateTimeSubEnabled();

            FontSlider.Value = Util.Clamp(_cfg.FontSize, 8, 32);

            if (string.Equals(_cfg.ColorMode, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                ColorCustom.IsChecked = true;
                ColorBox.Text = _cfg.TextColor;
            }
            else
            {
                ColorLoad.IsChecked = true;
                ColorBox.Text = string.IsNullOrEmpty(_cfg.TextColor) ? "#E6E6E6" : _cfg.TextColor;
            }

            OpacitySlider.Value = Util.Clamp(_cfg.BgOpacity, 0, 1);

            foreach (object item in RefreshCombo.Items)
            {
                ComboBoxItem cbi = item as ComboBoxItem;
                if (cbi != null && (string)cbi.Tag == _cfg.RefreshMs.ToString())
                { RefreshCombo.SelectedItem = cbi; break; }
            }
            if (RefreshCombo.SelectedItem == null && RefreshCombo.Items.Count > 0)
                RefreshCombo.SelectedIndex = 1;

            OverlayCombo.SelectedValue = string.IsNullOrEmpty(_cfg.OverlayDeviceName)
                ? _cfg.OverlayTargetMode
                : _cfg.OverlayDeviceName;
            if (OverlayCombo.SelectedIndex < 0) OverlayCombo.SelectedIndex = 0;

            FpsCombo.SelectedValue = _cfg.FpsDeviceName;
            if (FpsCombo.SelectedIndex < 0)
            {
                foreach (ScreenInfo screen in _screens)
                {
                    if (!screen.IsPrimary) continue;
                    FpsCombo.SelectedValue = screen.DeviceName;
                    break;
                }
            }
            if (FpsCombo.SelectedIndex < 0 && FpsCombo.Items.Count > 0)
                FpsCombo.SelectedIndex = 0;

            LogRetentionBox.Text = Math.Max(0, _cfg.LogRetentionCount).ToString();
            CsvRetentionBox.Text = Math.Max(0, _cfg.CsvRetentionCount).ToString();

            bool vertical = string.Equals(_cfg.LayoutMode, "Vertical", StringComparison.OrdinalIgnoreCase);
            LayoutHoriz.IsChecked = !vertical;
            LayoutVert.IsChecked = vertical;
        }

        private void FontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateLabels();
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateLabels();
        }

        private void UpdateLabels()
        {
            if (FontLabel != null) FontLabel.Text = string.Format("{0} pt", (int)Math.Round(FontSlider.Value));
            if (OpacityLabel != null) OpacityLabel.Text = string.Format("{0}%", (int)Math.Round(OpacitySlider.Value * 100));
        }

        private void ColorMode_Changed(object sender, RoutedEventArgs e)
        {
            UpdateCustomEnabled();
            UpdateColorPreview();
        }

        private void UpdateCustomEnabled()
        {
            bool custom = ColorCustom.IsChecked == true;
            ColorBox.IsEnabled = custom;
            ColorPreview.IsEnabled = custom;
        }

        private void ColorBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateColorPreview();
        }

        private void UpdateColorPreview()
        {
            if (ColorPreview == null) return;
            try
            {
                string s = (ColorBox.Text ?? "").TrimStart('#');
                if (s.Length == 6)
                    ColorPreview.Fill = new SolidColorBrush(Color.FromRgb(
                        Convert.ToByte(s.Substring(0, 2), 16),
                        Convert.ToByte(s.Substring(2, 2), 16),
                        Convert.ToByte(s.Substring(4, 2), 16)));
                else if (s.Length == 8)
                    ColorPreview.Fill = new SolidColorBrush(Color.FromArgb(
                        Convert.ToByte(s.Substring(0, 2), 16),
                        Convert.ToByte(s.Substring(2, 2), 16),
                        Convert.ToByte(s.Substring(4, 2), 16),
                        Convert.ToByte(s.Substring(6, 2), 16)));
            }
            catch { }
        }

        private void TimeChk_Changed(object sender, RoutedEventArgs e)
        {
            UpdateTimeSubEnabled();
        }

        private void TimeSub_Changed(object sender, RoutedEventArgs e)
        {
            // If user toggles a sub-option on while master is off, turn master on.
            if ((TimeDateChk.IsChecked == true || TimeSecChk.IsChecked == true) && TimeChk.IsChecked != true)
                TimeChk.IsChecked = true;
        }

        private void UpdateTimeSubEnabled()
        {
            bool on = TimeChk.IsChecked == true;
            TimeDateChk.IsEnabled = on;
            TimeSecChk.IsEnabled = on;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            int enabledCount =
                (CpuChk.IsChecked == true ? 1 : 0) +
                (MemChk.IsChecked == true ? 1 : 0) +
                (GpuChk.IsChecked == true ? 1 : 0) +
                (FpsChk.IsChecked == true ? 1 : 0);
            if (enabledCount == 0)
            {
                MessageBox.Show("至少需要启用一项监控。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int logRetention;
            int csvRetention;
            if (!int.TryParse(LogRetentionBox.Text, out logRetention) || logRetention < 0
                || !int.TryParse(CsvRetentionBox.Text, out csvRetention) || csvRetention < 0)
            {
                MessageBox.Show("日志和 CSV 保留份数必须是大于或等于 0 的整数；0 表示无限保留。",
                    "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AppConfig r = _cfg.Clone();

            r.CpuEnabled = CpuChk.IsChecked == true;
            r.MemEnabled = MemChk.IsChecked == true;
            r.GpuEnabled = GpuChk.IsChecked == true;
            r.FpsEnabled = FpsChk.IsChecked == true;
            r.ShowFpsWindowName = ShowFpsWindowNameChk.IsChecked == true;

            r.ShowTime = TimeChk.IsChecked == true;
            r.ShowDate = TimeDateChk.IsChecked == true;
            r.ShowSeconds = TimeSecChk.IsChecked == true;

            r.FontSize = Util.Clamp(Math.Round(FontSlider.Value), 8, 32);
            r.ColorMode = ColorCustom.IsChecked == true ? "Custom" : "Load";
            r.TextColor = (ColorBox.Text ?? "").Trim();
            r.BgOpacity = Math.Round(OpacitySlider.Value, 2);
            r.LayoutMode = LayoutVert.IsChecked == true ? "Vertical" : "Horizontal";
            r.LogRetentionCount = logRetention;
            r.CsvRetentionCount = csvRetention;

            ComboBoxItem refreshItem = RefreshCombo.SelectedItem as ComboBoxItem;
            if (refreshItem != null)
            {
                string tag = refreshItem.Tag as string;
                int ms;
                if (int.TryParse(tag, out ms)) r.RefreshMs = ms;
            }

            object ov = OverlayCombo.SelectedValue;
            string overlayVal = ov as string ?? "Auto";
            if (overlayVal == "Auto" || overlayVal == "Main")
            {
                r.OverlayTargetMode = overlayVal;
                r.OverlayDeviceName = "";
            }
            else
            {
                r.OverlayTargetMode = "Specific";
                r.OverlayDeviceName = overlayVal;
            }

            object fv = FpsCombo.SelectedValue;
            r.FpsDeviceName = fv as string ?? "";

            _result = r;
            DialogResult = true;
        }
    }
}

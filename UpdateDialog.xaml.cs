using System.Windows;

namespace PerfMonitor
{
    public partial class UpdateDialog : Window
    {
        public bool UpdateRequested { get; private set; }

        internal UpdateDialog(ReleaseInfo release)
        {
            InitializeComponent();
            VersionText.Text = "发现新版本 " + release.Tag + "（当前版本 " + AppVersion.Display + "）";
            NotesText.Text = string.IsNullOrWhiteSpace(release.Notes) ? "此版本没有提供更新说明。" : release.Notes;
            if (string.IsNullOrWhiteSpace(release.InstallerUrl))
            {
                StatusText.Text = "该版本尚未发布安装包，请稍后再试。";
                UpdateButton.IsEnabled = false;
            }
        }

        private void Update_Click(object sender, RoutedEventArgs e)
        {
            UpdateRequested = true;
            DialogResult = true;
        }
    }
}

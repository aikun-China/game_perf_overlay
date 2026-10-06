using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace PerfMonitor
{
    internal sealed class ReleaseInfo
    {
        public string Tag { get; set; }
        public string Version { get; set; }
        public string Notes { get; set; }
        public string InstallerUrl { get; set; }
    }

    internal static class UpdateService
    {
        private const int Tls12 = 3072;

        public static ReleaseInfo GetLatestRelease()
        {
            EnableTls12();
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(AppVersion.LatestReleaseApi);
            request.Method = "GET";
            request.UserAgent = "game_perf_overlay/" + AppVersion.Number;
            request.Accept = "application/vnd.github+json";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                Dictionary<string, object> data = new JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                if (data == null) throw new InvalidDataException("GitHub returned an empty release response.");

                string tag = GetString(data, "tag_name");
                if (string.IsNullOrWhiteSpace(tag))
                    throw new InvalidDataException("The latest GitHub release does not have a version tag.");

                ReleaseInfo release = new ReleaseInfo();
                release.Tag = tag;
                release.Version = NormalizeVersion(tag);
                release.Notes = GetString(data, "body");
                object assetsValue;
                if (data.TryGetValue("assets", out assetsValue))
                {
                    object[] assets = assetsValue as object[];
                    if (assets != null)
                    {
                        foreach (object item in assets)
                        {
                            Dictionary<string, object> asset = item as Dictionary<string, object>;
                            if (asset == null) continue;
                            string name = GetString(asset, "name");
                            string url = GetString(asset, "browser_download_url");
                            if (name.StartsWith(AppVersion.InstallerAssetPrefix, StringComparison.OrdinalIgnoreCase)
                                && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                && IsTrustedInstallerUrl(url))
                            {
                                release.InstallerUrl = url;
                                break;
                            }
                        }
                    }
                }
                return release;
            }
        }

        public static string DownloadInstaller(ReleaseInfo release)
        {
            if (release == null) throw new ArgumentNullException("release");
            if (!IsTrustedInstallerUrl(release.InstallerUrl))
                throw new InvalidDataException("The release does not contain a trusted game_perf_overlay installer.");

            EnableTls12();
            string directory = Path.Combine(Path.GetTempPath(), "game_perf_overlay_update");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "game_perf_overlay-Setup-" + release.Version + ".exe");
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(release.InstallerUrl);
            request.Method = "GET";
            request.UserAgent = "game_perf_overlay/" + AppVersion.Number;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
            }
            if (new FileInfo(path).Length == 0)
                throw new InvalidDataException("The downloaded installer is empty.");
            return path;
        }

        public static bool IsNewer(ReleaseInfo release)
        {
            if (release == null) throw new ArgumentNullException("release");
            Version latest;
            Version current;
            if (!Version.TryParse(release.Version, out latest)
                || !Version.TryParse(AppVersion.Number, out current))
                throw new InvalidDataException("The current version or GitHub release tag is not a valid numeric version.");
            return latest > current;
        }

        private static void EnableTls12()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)Tls12;
        }

        private static bool IsTrustedInstallerUrl(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri)
                && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith("/aikun-China/game_perf_overlay/releases/download/", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeVersion(string tag)
        {
            string version = tag.Trim();
            if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                version = version.Substring(1);
            return version;
        }

        private static string GetString(IDictionary<string, object> data, string key)
        {
            object value;
            return data.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }
    }
}

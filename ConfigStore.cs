using System;
using System.IO;

namespace PerfMonitor
{
    // Persists the AppConfig as JSON under %AppData%\PerfMonitor\config.json.
    internal static class ConfigStore
    {
        private static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerfMonitor"); }
        }

        private static string Path_
        {
            get { return System.IO.Path.Combine(Dir, "config.json"); }
        }

        public static AppConfig Load()
        {
            try
            {
                if (!File.Exists(Path_)) return new AppConfig();
                return AppConfig.FromJson(File.ReadAllText(Path_));
            }
            catch
            {
                return new AppConfig();
            }
        }

        public static void Save(AppConfig cfg)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(Path_, cfg.ToJson());
            }
            catch
            {
                // Non-fatal: settings simply won't persist.
            }
        }
    }
}

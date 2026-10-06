using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PerfMonitor
{
    internal static class SessionFiles
    {
        private sealed class SessionFile
        {
            public string Path;
            public DateTime Date;
            public int Index;
        }

        private static readonly object Sync = new object();
        private static readonly string LogDirectoryValue = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        private static readonly string CsvDirectoryValue = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "csv");
        private static string _logPath;
        private static string _csvPath;

        public static string LogDirectory { get { return LogDirectoryValue; } }
        public static string CsvDirectory { get { return CsvDirectoryValue; } }
        public static string LogPath { get { EnsureInitialized(); return _logPath; } }
        public static string CsvPath { get { EnsureInitialized(); return _csvPath; } }

        public static void Initialize(int logRetentionCount, int csvRetentionCount)
        {
            lock (Sync)
            {
                if (_logPath == null) CreateCurrentSession();
                ApplyRetentionCore(logRetentionCount, csvRetentionCount);
            }
        }

        public static void ApplyRetention(int logRetentionCount, int csvRetentionCount)
        {
            lock (Sync)
            {
                EnsureInitialized();
                ApplyRetentionCore(logRetentionCount, csvRetentionCount);
            }
        }

        public static void AppendLog(string text)
        {
            lock (Sync)
            {
                EnsureInitialized();
                File.AppendAllText(_logPath, text, new System.Text.UTF8Encoding(false));
            }
        }

        private static void EnsureInitialized()
        {
            lock (Sync)
            {
                if (_logPath == null) CreateCurrentSession();
            }
        }

        private static void CreateCurrentSession()
        {
            Directory.CreateDirectory(LogDirectoryValue);
            Directory.CreateDirectory(CsvDirectoryValue);

            DateTime today = DateTime.Now.Date;
            int index = GetNextIndex(today);
            while (true)
            {
                string stem = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-"
                    + index.ToString(CultureInfo.InvariantCulture);
                string logPath = Path.Combine(LogDirectoryValue, stem + ".log");
                string csvPath = Path.Combine(CsvDirectoryValue, stem + ".csv");
                bool createdLog = false;
                try
                {
                    using (new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
                    createdLog = true;
                    using (new FileStream(csvPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
                    _logPath = logPath;
                    _csvPath = csvPath;
                    return;
                }
                catch (IOException)
                {
                    if (createdLog && File.Exists(logPath)) File.Delete(logPath);
                    if (!File.Exists(logPath) && !File.Exists(csvPath)) throw;
                    if (index == int.MaxValue)
                        throw new IOException("The session file sequence number for today has reached its maximum.");
                    index++;
                }
            }
        }

        private static int GetNextIndex(DateTime date)
        {
            string datePrefix = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-";
            int maxIndex = 0;
            string[] paths = Directory.GetFiles(LogDirectoryValue, "*.log")
                .Concat(Directory.GetFiles(CsvDirectoryValue, "*.csv")).ToArray();
            foreach (string path in paths)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (!name.StartsWith(datePrefix, StringComparison.Ordinal)) continue;
                string number = name.Substring(datePrefix.Length);
                if (number.StartsWith("第", StringComparison.Ordinal) && number.EndsWith("份", StringComparison.Ordinal))
                    number = number.Substring(1, number.Length - 2);
                int index;
                if (int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out index)
                    && index > maxIndex)
                    maxIndex = index;
            }
            if (maxIndex == int.MaxValue)
                throw new IOException("The session file sequence number for today has reached its maximum.");
            return maxIndex + 1;
        }

        private static void ApplyRetentionCore(int logRetentionCount, int csvRetentionCount)
        {
            Prune(LogDirectoryValue, ".log", Math.Max(0, logRetentionCount));
            Prune(CsvDirectoryValue, ".csv", Math.Max(0, csvRetentionCount));
        }

        private static void Prune(string directory, string extension, int keepCount)
        {
            if (keepCount == 0 || !Directory.Exists(directory)) return;
            Regex pattern = new Regex(@"^(?<date>\d{4}-\d{2}-\d{2})-(?:第)?(?<index>\d+)(?:份)?" + Regex.Escape(extension) + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var files = Directory.GetFiles(directory, "*" + extension)
                .Select(delegate(string path)
                {
                    Match match = pattern.Match(Path.GetFileName(path));
                    DateTime date;
                    int index;
                    if (!match.Success
                        || !DateTime.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out date)
                        || !int.TryParse(match.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out index))
                        return null;
                    return new SessionFile { Path = path, Date = date, Index = index };
                })
                .Where(delegate(SessionFile file) { return file != null; })
                .OrderByDescending(delegate(SessionFile file) { return file.Date; })
                .ThenByDescending(delegate(SessionFile file) { return file.Index; })
                .ToList();

            string activePath = extension == ".log" ? _logPath : _csvPath;
            List<SessionFile> retained = files
                .Where(delegate(SessionFile file) { return string.Equals(file.Path, activePath, StringComparison.OrdinalIgnoreCase); })
                .Concat(files.Where(delegate(SessionFile file) { return !string.Equals(file.Path, activePath, StringComparison.OrdinalIgnoreCase); })
                    .Take(keepCount - 1))
                .ToList();
            foreach (SessionFile file in files)
            {
                if (!retained.Contains(file)) File.Delete(file.Path);
            }
        }
    }
}

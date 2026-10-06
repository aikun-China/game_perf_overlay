using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace PerfMonitor
{
    internal sealed class CsvRecorder : IDisposable
    {
        private StreamWriter _writer;
        private string _currentPath;
        private readonly object _sync = new object();

        public static string CsvDirectory { get { return SessionFiles.CsvDirectory; } }
        public string CurrentPath { get { lock (_sync) return _currentPath; } }

        public CsvRecorder()
        {
            _currentPath = SessionFiles.CsvPath;
            OpenSessionFile();
        }

        public void Write(MetricSample sample)
        {
            if (sample == null) throw new ArgumentNullException("sample");
            lock (_sync)
            {
                if (_writer == null) OpenSessionFile();

                string[] values = new string[]
                {
                    sample.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    FormatNumber(sample.Cpu),
                    FormatNumber(sample.Memory),
                    FormatNumber(sample.Gpu),
                    sample.FpsAvailable ? sample.Fps.ToString(CultureInfo.InvariantCulture) : "",
                    Escape(sample.Source ?? "")
                };
                _writer.WriteLine(string.Join(",", values));
                _writer.Flush();
            }
        }

        public void ExportTo(string destination)
        {
            if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("An export path is required.", "destination");
            lock (_sync)
            {
                if (_writer != null) _writer.Flush();
                string source = _currentPath;
                if (!File.Exists(source))
                    throw new FileNotFoundException("There is no CSV log to export yet.", source);
                File.Copy(source, destination, true);
            }
        }

        private void OpenSessionFile()
        {
            if (_writer != null)
            {
                _writer.Flush();
                _writer.Dispose();
                _writer = null;
            }

            bool needsHeader = !File.Exists(_currentPath) || new FileInfo(_currentPath).Length == 0;
            _writer = new StreamWriter(_currentPath, true, new UTF8Encoding(true));
            if (needsHeader)
            {
                _writer.WriteLine("Timestamp,CPU_Percent,Memory_Percent,GPU_Percent,FPS,Source");
                _writer.Flush();
            }
        }

        private static string FormatNumber(float value)
        {
            return value < 0 ? "" : value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static string Escape(string value)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_writer == null) return;
                _writer.Flush();
                _writer.Dispose();
                _writer = null;
            }
        }
    }
}

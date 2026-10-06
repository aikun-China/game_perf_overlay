using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace PerfMonitor
{
    internal sealed class MetricSample
    {
        public DateTime Timestamp;
        public float Cpu;
        public float Memory;
        public float Gpu;
        public int Fps;
        public bool FpsAvailable;
        public string Source;
    }

    internal sealed class MetricHistory
    {
        private const int Capacity = 60;
        private readonly object _sync = new object();
        private readonly List<MetricSample> _samples = new List<MetricSample>(Capacity);
        private readonly CpuMonitor _cpu;
        private readonly MemoryMonitor _memory;
        private readonly GpuMonitor _gpu;
        private readonly FpsMonitor _fps;

        public MetricHistory(CpuMonitor cpu, MemoryMonitor memory, GpuMonitor gpu, FpsMonitor fps)
        {
            _cpu = cpu;
            _memory = memory;
            _gpu = gpu;
            _fps = fps;
        }

        public MetricSample Capture()
        {
            bool fpsAvailable = _fps != null && _fps.Available;
            MetricSample sample = new MetricSample
            {
                Timestamp = DateTime.Now,
                Cpu = _cpu != null ? _cpu.Sample() : -1f,
                Memory = _memory != null ? _memory.Sample() : -1f,
                Gpu = _gpu != null ? _gpu.Sample() : -1f,
                FpsAvailable = fpsAvailable,
                Fps = fpsAvailable ? _fps.CurrentFps : 0,
                Source = _fps != null ? _fps.CurrentSourceLabel : "桌面"
            };

            lock (_sync)
            {
                if (_samples.Count == Capacity) _samples.RemoveAt(0);
                _samples.Add(sample);
            }
            return sample;
        }

        public MetricSample Latest
        {
            get
            {
                lock (_sync)
                    return _samples.Count == 0 ? null : _samples[_samples.Count - 1];
            }
        }

        public List<MetricSample> GetRecent()
        {
            lock (_sync) return _samples.ToList();
        }
    }

    public sealed class FpsHistoryChart : FrameworkElement
    {
        private static readonly Brush GridBrush = new SolidColorBrush(Color.FromArgb(80, 180, 180, 180));
        private static readonly Brush LineBrush = new SolidColorBrush(Color.FromRgb(0, 230, 118));
        private static readonly Brush MissingBrush = new SolidColorBrush(Color.FromArgb(100, 128, 128, 128));
        private MetricHistory _history;

        public FpsHistoryChart()
        {
        }

        static FpsHistoryChart()
        {
            GridBrush.Freeze();
            LineBrush.Freeze();
            MissingBrush.Freeze();
        }

        internal MetricHistory History
        {
            get { return _history; }
            set
            {
                _history = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            double width = ActualWidth;
            double height = ActualHeight;
            if (width < 2 || height < 2) return;

            Rect bounds = new Rect(0, 0, width, height);
            drawingContext.DrawRectangle(Brushes.Transparent, null, bounds);
            double top = 2;
            double bottom = Math.Max(top + 1, height - 2);
            double left = 2;
            double right = Math.Max(left + 1, width - 2);

            for (int i = 1; i <= 3; i++)
            {
                double y = top + (bottom - top) * i / 4.0;
                drawingContext.DrawLine(new Pen(GridBrush, 0.5), new Point(left, y), new Point(right, y));
            }

            List<MetricSample> samples = _history != null ? _history.GetRecent() : new List<MetricSample>();
            if (samples.Count < 2)
            {
                drawingContext.DrawLine(new Pen(MissingBrush, 1), new Point(left, bottom), new Point(right, bottom));
                return;
            }

            int maxFps = Math.Max(60, samples.Where(delegate(MetricSample s) { return s.FpsAvailable; })
                .Select(delegate(MetricSample s) { return s.Fps; }).DefaultIfEmpty(60).Max());
            maxFps = Math.Min(360, ((maxFps + 29) / 30) * 30);
            Pen line = new Pen(LineBrush, Math.Max(1, height / 18.0));
            Point? previous = null;
            for (int i = 0; i < samples.Count; i++)
            {
                MetricSample sample = samples[i];
                if (!sample.FpsAvailable)
                {
                    previous = null;
                    continue;
                }

                double x = left + (right - left) * i / Math.Max(1, samples.Count - 1);
                double normalized = Math.Max(0, Math.Min(1, (double)sample.Fps / maxFps));
                Point current = new Point(x, bottom - normalized * (bottom - top));
                if (previous.HasValue) drawingContext.DrawLine(line, previous.Value, current);
                previous = current;
            }
        }
    }
}

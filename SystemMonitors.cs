using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace PerfMonitor
{
    // CPU / memory / GPU samplers, all driven by System.Diagnostics.PerformanceCounter
    // (no WMI, no busy polling). Each Sample() returns a 0..100 percentage.
    // Construction primes the counters (first NextValue is meaningless), so callers
    // should create these once at startup and reuse them on the refresh timer.
    internal sealed class CpuMonitor
    {
        private PerformanceCounter _cpu;
        public bool Available { get { return _cpu != null; } }

        public CpuMonitor()
        {
            try
            {
                _cpu = new PerformanceCounter("Processor Information", "% Processor Time", "_Total", true);
                _cpu.NextValue(); // prime
            }
            catch { _cpu = null; }
        }

        public float Sample()
        {
            if (_cpu == null) return -1f;
            try { return Util.Clamp(_cpu.NextValue(), 0f, 100f); }
            catch { return -1f; }
        }
    }

    internal sealed class MemoryMonitor
    {
        private PerformanceCounter _availMb;
        private ulong _totalPhys;
        public bool Available { get { return _availMb != null && _totalPhys > 0; } }

        public MemoryMonitor()
        {
            try
            {
                var mem = new Win32.MEMORYSTATUSEX();
                mem.dwLength = (uint)Marshal.SizeOf(typeof(Win32.MEMORYSTATUSEX));
                if (Win32.GlobalMemoryStatusEx(ref mem)) _totalPhys = mem.ullTotalPhys;
                _availMb = new PerformanceCounter("Memory", "Available MBytes", true);
                _availMb.NextValue(); // prime
            }
            catch { _availMb = null; }
        }

        public float Sample()
        {
            if (!Available) return -1f;
            try
            {
                double availBytes = (double)_availMb.NextValue() * 1024.0 * 1024.0;
                double pct = (1.0 - availBytes / (double)_totalPhys) * 100.0;
                return (float)Util.Clamp(pct, 0f, 100f);
            }
            catch { return -1f; }
        }
    }

    internal sealed class GpuMonitor
    {
        private PerformanceCounterCategory _cat;
        private readonly Dictionary<string, PerformanceCounter> _counters = new Dictionary<string, PerformanceCounter>();
        private int _refreshCounter;

        public bool Available { get { return _cat != null; } }

        public GpuMonitor()
        {
            try
            {
                _cat = new PerformanceCounterCategory("GPU Engine");
                RebuildCounters();
            }
            catch { _cat = null; }
        }

        // Aggregate GPU usage as the maximum single-engine utilization for the primary
        // adapter (phys_0). This tracks the busiest engine, which best reflects whether
        // the GPU is working (matches the visual behaviour of most lightweight tools).
        public float Sample()
        {
            if (_cat == null) return -1f;
            try
            {
                // Refresh the instance cache periodically (processes start/stop engines).
                if (++_refreshCounter % 10 == 0) RebuildCounters();

                float max = 0f;
                foreach (var kv in _counters)
                {
                    try
                    {
                        float v = kv.Value.NextValue();
                        if (v > max) max = v;
                    }
                    catch { /* skip a dead instance this tick */ }
                }
                return Util.Clamp(max, 0f, 100f);
            }
            catch { return -1f; }
        }

        private void RebuildCounters()
        {
            if (_cat == null) return;
            string[] names;
            try { names = _cat.GetInstanceNames(); }
            catch { return; }

            var live = new HashSet<string>(names, StringComparer.Ordinal);

            // Drop counters whose instance disappeared.
            var dead = _counters.Keys.Where(k => !live.Contains(k)).ToList();
            foreach (var d in dead) { try { _counters[d].Dispose(); } catch { } _counters.Remove(d); }

            // Add new instances that belong to the primary adapter (phys_0). If none
            // qualify (e.g. single-GPU naming differs), fall back to all instances.
            List<string> primary = names.Where(delegate (string n) { return n.IndexOf("phys_0", StringComparison.OrdinalIgnoreCase) >= 0; }).ToList();
            List<string> target = primary.Count > 0 ? primary : names.ToList();

            foreach (var n in target)
            {
                if (_counters.ContainsKey(n)) continue;
                try
                {
                    var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", n, true);
                    c.NextValue(); // prime
                    _counters[n] = c;
                }
                catch { }
            }
        }

        public void Dispose()
        {
            foreach (var c in _counters.Values) { try { c.Dispose(); } catch { } }
            _counters.Clear();
        }
    }
}

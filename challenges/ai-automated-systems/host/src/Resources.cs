// Port of warden/resources.py.
// Edge resource meter: proves the power and bandwidth budgets (plan Section 6 / 8.11).
// psutil -> System.Diagnostics.Process: CPU cores from the TotalProcessorTime delta, RSS from WorkingSet64.
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace BridgeWarden
{
    public sealed class ResourceMeter
    {
        public Dictionary<string, object> budgets;
        public Process proc;
        public long ws_bytes = 0;
        public long ws_msgs = 0;
        public int intent_bytes_max = 0;
        public List<double> pipeline_ms = new List<double>();
        public double ws_rate = 0.0;
        double _t0;
        double _last_t;
        long _last_ws_bytes;
        // cpu_percent(None) state: CPU time and wall time at the previous sample
        double _cpu_t;
        double _cpu_s;
        readonly object _lock = new object();

        public ResourceMeter(Dictionary<string, object> budgets)
        {
            this.budgets = budgets;
            proc = Process.GetCurrentProcess();
            _cpu_t = Py.Perf();
            _cpu_s = proc.TotalProcessorTime.TotalSeconds;
            _t0 = Py.Now();
            _last_t = _t0;
            _last_ws_bytes = 0;
        }

        public void CountWs(int nbytes)
        {
            lock (_lock)
            {
                ws_bytes += nbytes;
                ws_msgs += 1;
            }
        }

        public void RecordPipeline(double ms)
        {
            lock (_lock)
            {
                List<double> l = new List<double>(pipeline_ms);
                l.Add(ms);
                if (l.Count > 50) l = l.GetRange(l.Count - 50, 50);
                pipeline_ms = l;
            }
        }

        // proc.cpu_percent(None) / 100: cores used since the previous call.
        double CpuCores()
        {
            proc.Refresh();
            double t = Py.Perf(), c = proc.TotalProcessorTime.TotalSeconds;
            double dt = t - _cpu_t, dc = c - _cpu_s;
            _cpu_t = t;
            _cpu_s = c;
            return dt > 0 ? Math.Max(0.0, dc / dt) : 0.0;
        }

        public Dictionary<string, object> Sample()
        {
            double cpu, rss, pipe;
            int intentMax;
            lock (_lock)
            {
                double now = Py.Now();
                double dt = Math.Max(0.001, now - _last_t);
                if (dt >= 2)
                {
                    ws_rate = (ws_bytes - _last_ws_bytes) / dt;
                    _last_t = now;
                    _last_ws_bytes = ws_bytes;
                }
                cpu = CpuCores();                                  // in cores
                rss = proc.WorkingSet64 / 1e6;
                double sum = 0;
                foreach (double m in pipeline_ms) sum += m;
                pipe = pipeline_ms.Count > 0 ? Py.Round(sum / pipeline_ms.Count, 2) : 0.0;
                intentMax = intent_bytes_max;
            }
            Dictionary<string, object> b = budgets;
            return Py.D(
                "cpu_cores", Py.Round(cpu, 2), "cpu_budget", b["cpu_cores"],
                "rss_mb", Py.Round(rss, 1), "rss_budget", b["rss_mb"],
                "ui_kbps", Py.Round(ws_rate / 1000, 1), "ui_budget", b["ui_kbps"],
                "intent_bytes_max", intentMax, "intent_budget", b["intent_bytes"],
                "pipeline_ms", pipe,
                "external_bytes", 0,   // the core loop makes no outbound network calls
                "profile", b["profile"]);
        }
    }
}

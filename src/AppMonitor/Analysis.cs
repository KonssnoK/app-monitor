using System;
using System.Collections.Generic;
using System.Linq;

namespace AppMonitor
{
    sealed class ProcStat
    {
        public string Name = "", User = "", CommandLine = "", Path = "";
        public string Pids = "";
        public readonly HashSet<int> Keys = new HashSet<int>();
        public double CpuSec, GpuSec, ReadBytes, WriteBytes, ActiveSec;
        public float PeakCpu, PeakGpu, PeakMem;
        public double AvgCpu, AvgGpu; // over the whole range
        // per-sample accumulation, so grouped peaks are the sum of all instances at that moment
        internal int CurSample = -1;
        internal float CurCpu, CurGpu, CurMem;

        internal void FlushPeak()
        {
            if (CurCpu > PeakCpu) PeakCpu = CurCpu;
            if (CurGpu > PeakGpu) PeakGpu = CurGpu;
            if (CurMem > PeakMem) PeakMem = CurMem;
            CurCpu = CurGpu = CurMem = 0;
        }
    }

    sealed class Episode
    {
        public long Start, End;
        public float PeakCpu, PeakGpu, PeakTemp = float.NaN, PeakFan;
        public string PeakFanName = "";
        public double AvgCpu, AvgGpu;
        public string Culprits = "";
        public double DurationSec => (End - Start) / (double)TimeSpan.TicksPerSecond;
    }

    static class Analysis
    {
        /// <summary>Aggregates per-process usage for samples ending in (a, b].</summary>
        public static List<ProcStat> Compute(Trace t, long a, long b, bool groupByName, out double rangeSec)
        {
            var map = new Dictionary<string, ProcStat>();
            var pidSets = new Dictionary<ProcStat, SortedSet<int>>();
            rangeSec = 0;
            lock (t.Sync)
            {
                int i0 = t.LowerBound(a + 1), i1 = t.LowerBound(b + 1);
                if (i1 <= i0) { int k = t.Nearest(b); if (k >= 0) { i0 = k; i1 = k + 1; } }
                for (int i = i0; i < i1; i++)
                {
                    var s = t.Samples[i];
                    double dt = s.IntervalSec;
                    rangeSec += dt;
                    foreach (var p in s.Procs)
                    {
                        if (!t.Procs.TryGetValue(p.Key, out var info)) continue;
                        string id = groupByName ? info.Display + "\u0001" + info.User : p.Key.ToString();
                        if (!map.TryGetValue(id, out var st))
                        {
                            st = new ProcStat { Name = info.Display, User = info.User, CommandLine = info.CommandLine, Path = info.Path };
                            map[id] = st;
                            pidSets[st] = new SortedSet<int>();
                        }
                        st.Keys.Add(p.Key);
                        pidSets[st].Add(info.Pid);
                        st.CpuSec += p.Cpu / 100.0 * dt;
                        st.GpuSec += p.Gpu / 100.0 * dt;
                        st.ReadBytes += p.ReadBps * dt;
                        st.WriteBytes += p.WriteBps * dt;
                        if (st.CurSample != i)
                        {
                            st.FlushPeak();
                            st.CurSample = i;
                            st.ActiveSec += dt;
                        }
                        st.CurCpu += p.Cpu; st.CurGpu += p.Gpu; st.CurMem += p.MemMB;
                    }
                }
            }
            foreach (var st in map.Values)
            {
                st.FlushPeak();
                st.AvgCpu = rangeSec > 0 ? st.CpuSec * 100 / rangeSec : 0;
                st.AvgGpu = rangeSec > 0 ? st.GpuSec * 100 / rangeSec : 0;
                var pids = pidSets[st];
                st.Pids = pids.Count <= 3 ? string.Join(", ", pids) : $"{pids.Count} procs";
            }
            return map.Values.OrderByDescending(s => s.CpuSec + s.GpuSec * 0.5).ToList();
        }

        /// <summary>
        /// Finds sustained busy periods (CPU or GPU above threshold for at least minSec) — the usual fan triggers.
        /// Short dips of up to 2 samples don't split a period.
        /// </summary>
        public static List<Episode> FindEpisodes(Trace t, float threshold, double minSec)
        {
            var result = new List<Episode>();
            lock (t.Sync)
            {
                var ss = t.Samples;
                int start = -1, lastActive = -1;
                for (int i = 0; i <= ss.Count; i++)
                {
                    bool end = i == ss.Count;
                    bool gap = !end && i > 0 && Trace.IsGap(ss[i - 1], ss[i]);
                    bool active = !end && (ss[i].Cpu >= threshold || ss[i].Gpu >= threshold);
                    if (start >= 0 && (end || gap || (!active && i - lastActive > 2)))
                    {
                        var ep = Build(t, start, lastActive);
                        if (ep.DurationSec >= minSec) result.Add(ep);
                        start = -1;
                    }
                    if (active)
                    {
                        if (start < 0) start = i;
                        lastActive = i;
                    }
                }
            }
            result.Reverse(); // newest first
            return result;
        }

        static Episode Build(Trace t, int i0, int i1)
        {
            var ep = new Episode { Start = t.Samples[i0].StartTicks, End = t.Samples[i1].Ticks };
            var cpuByName = new Dictionary<string, double>();
            var gpuByName = new Dictionary<string, double>();
            double dur = 0;
            for (int i = i0; i <= i1; i++)
            {
                var s = t.Samples[i];
                double dt = s.IntervalSec;
                dur += dt;
                ep.AvgCpu += s.Cpu * dt; ep.AvgGpu += s.Gpu * dt;
                ep.PeakCpu = Math.Max(ep.PeakCpu, s.Cpu);
                ep.PeakGpu = Math.Max(ep.PeakGpu, s.Gpu);
                if (!t.HasTempSensors && !float.IsNaN(s.TempC) && (float.IsNaN(ep.PeakTemp) || s.TempC > ep.PeakTemp)) ep.PeakTemp = s.TempC;
                if (s.Sensors != null)
                    for (int k = 0; k < s.Sensors.Length && k < t.Sensors.Count; k++)
                    {
                        float v = s.Sensors[k];
                        if (float.IsNaN(v)) continue;
                        if (t.Sensors[k].Kind == SensorKind.Fan && v > ep.PeakFan) { ep.PeakFan = v; ep.PeakFanName = t.Sensors[k].Name; }
                        if (t.Sensors[k].Kind == SensorKind.Temp && (float.IsNaN(ep.PeakTemp) || v > ep.PeakTemp)) ep.PeakTemp = v;
                    }
                foreach (var p in s.Procs)
                {
                    if (!t.Procs.TryGetValue(p.Key, out var info)) continue;
                    cpuByName.TryGetValue(info.Display, out var c); cpuByName[info.Display] = c + p.Cpu * dt;
                    if (p.Gpu > 0) { gpuByName.TryGetValue(info.Display, out var g); gpuByName[info.Display] = g + p.Gpu * dt; }
                }
            }
            if (dur > 0) { ep.AvgCpu /= dur; ep.AvgGpu /= dur; }
            bool gpuDriven = ep.AvgGpu > ep.AvgCpu;
            var src = gpuDriven ? gpuByName : cpuByName;
            ep.Culprits = string.Join(", ", src.OrderByDescending(kv => kv.Value).Take(3)
                .Where(kv => kv.Value / dur >= 1)
                .Select(kv => $"{kv.Key} {kv.Value / dur:0}%{(gpuDriven ? " GPU" : "")}"));
            return ep;
        }

        public static string Bytes(double b)
        {
            if (b < 1024) return $"{b:0} B";
            if (b < 1024 * 1024) return $"{b / 1024:0.#} KB";
            if (b < 1024.0 * 1024 * 1024) return $"{b / 1048576:0.#} MB";
            return $"{b / 1073741824:0.##} GB";
        }

        public static string Duration(double sec)
        {
            if (sec < 60) return $"{sec:0}s";
            if (sec < 3600) return $"{(int)sec / 60}m {(int)sec % 60:00}s";
            if (sec < 86400) return $"{(int)sec / 3600}h {(int)sec % 3600 / 60:00}m";
            return $"{(int)sec / 86400}d {(int)sec % 86400 / 3600}h";
        }
    }
}

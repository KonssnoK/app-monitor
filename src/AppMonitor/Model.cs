using System;
using System.Collections.Generic;

namespace AppMonitor
{
    /// <summary>Static facts about one process instance (pid + start time). Recorded once per trace.</summary>
    sealed class ProcInfo
    {
        public int Key;            // unique within a trace
        public int Pid;
        public long CreateTime;    // FILETIME UTC
        public string Name = "", User = "", Path = "", CommandLine = "", Services = "";

        string display;
        public string Display => display ??= string.IsNullOrEmpty(Services) ? Name : Name + " [" + Services + "]";
    }

    enum SensorKind : byte { Fan = 1, Temp = 2 }

    /// <summary>A hardware sensor recorded in the trace (fan RPM or temperature °C).</summary>
    sealed class SensorDef
    {
        public int Index;
        public SensorKind Kind;
        public string Name = "";
    }

    struct ProcSample
    {
        public int Key;
        public float Cpu;       // % of whole machine (0..100)
        public float Gpu;       // % (busiest engine)
        public float ReadBps, WriteBps;
        public float MemMB;     // private working set
    }

    sealed class Sample
    {
        public long Ticks;          // UTC DateTime ticks
        public float IntervalSec;
        public float Cpu, Gpu, MemPct, MemUsedMB;
        public float DiskReadBps, DiskWriteBps, DiskActivePct, NetBps;
        public float TempC = float.NaN, CpuClockPct = float.NaN;
        public ProcSample[] Procs = Array.Empty<ProcSample>();
        /// <summary>Values for Trace.Sensors (same order), or null when no sensors were read.</summary>
        public float[] Sensors;

        public long StartTicks => Ticks - (long)(IntervalSec * TimeSpan.TicksPerSecond);
    }

    sealed class Trace
    {
        public readonly object Sync = new object();
        public readonly List<Sample> Samples = new List<Sample>();
        public readonly Dictionary<int, ProcInfo> Procs = new Dictionary<int, ProcInfo>();
        public readonly List<SensorDef> Sensors = new List<SensorDef>();
        public bool HasFans => Sensors.Exists(d => d.Kind == SensorKind.Fan);
        public bool HasTempSensors => Sensors.Exists(d => d.Kind == SensorKind.Temp);
        public string Machine = Environment.MachineName;
        public int CpuCount = Environment.ProcessorCount;
        public long TotalMemBytes;
        public string FilePath;
        public bool IsLive;
        public bool HasTemp;
        /// <summary>Bumped on every change so views can cache.</summary>
        public int Version;
        /// <summary>For live traces: keep at most this much history in RAM (the file keeps everything).</summary>
        public long RetainTicks;

        public void AddProc(ProcInfo p)
        {
            lock (Sync) Procs[p.Key] = p;
        }

        public void AddSample(Sample s)
        {
            lock (Sync)
            {
                Samples.Add(s);
                if (!float.IsNaN(s.TempC)) HasTemp = true;
                if (RetainTicks > 0 && Samples.Count > 100)
                {
                    long cutoff = s.Ticks - RetainTicks;
                    if (Samples[Samples.Count / 20].Ticks < cutoff) // trim in ~5% batches
                    {
                        int n = LowerBound(cutoff);
                        Samples.RemoveRange(0, n);
                    }
                }
                Version++;
            }
        }

        /// <summary>First index with Ticks >= t. Caller holds the lock.</summary>
        public int LowerBound(long t)
        {
            int lo = 0, hi = Samples.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (Samples[mid].Ticks < t) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        /// <summary>Index of the sample closest to t, or -1. Caller holds the lock.</summary>
        public int Nearest(long t)
        {
            if (Samples.Count == 0) return -1;
            int i = LowerBound(t);
            if (i >= Samples.Count) return Samples.Count - 1;
            if (i > 0 && t - Samples[i - 1].Ticks < Samples[i].Ticks - t) return i - 1;
            return i;
        }

        public bool GetExtent(out long start, out long end)
        {
            lock (Sync)
            {
                if (Samples.Count == 0) { start = end = 0; return false; }
                start = Samples[0].StartTicks;
                end = Samples[Samples.Count - 1].Ticks;
                return true;
            }
        }

        public static bool IsGap(Sample prev, Sample cur)
            => cur.Ticks - prev.Ticks > (long)(Math.Max(3 * cur.IntervalSec, 5) * TimeSpan.TicksPerSecond);
    }
}

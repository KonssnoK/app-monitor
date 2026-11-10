using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace AppMonitor
{
    /// <summary>Thin wrapper around a PDH query; reads wildcard counter arrays without allocating per item.</summary>
    sealed unsafe class PdhQuery : IDisposable
    {
        IntPtr query;
        IntPtr buf; int bufSize;
        public unsafe delegate void ItemHandler(char* name, double value);

        public PdhQuery()
        {
            if (Native.PdhOpenQuery(null, IntPtr.Zero, out query) != 0) query = IntPtr.Zero;
        }

        public IntPtr Add(string path)
        {
            if (query == IntPtr.Zero) return IntPtr.Zero;
            return Native.PdhAddEnglishCounter(query, path, IntPtr.Zero, out var c) == 0 ? c : IntPtr.Zero;
        }

        public bool Collect() => query != IntPtr.Zero && Native.PdhCollectQueryData(query) == 0;

        public int Read(IntPtr counter, ItemHandler handler)
        {
            if (counter == IntPtr.Zero) return 0;
            const uint fmt = Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int size = bufSize;
                int st = Native.PdhGetFormattedCounterArray(counter, fmt, ref size, out int count, buf);
                if (st == Native.PDH_MORE_DATA)
                {
                    if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                    bufSize = size + 4096;
                    buf = Marshal.AllocHGlobal(bufSize);
                    continue;
                }
                if (st != 0) return 0;
                byte* p = (byte*)buf;
                for (int i = 0; i < count; i++, p += 24) // PDH_FMT_COUNTERVALUE_ITEM_W on x64
                {
                    uint status = *(uint*)(p + 8);
                    if (status > 1) continue; // VALID_DATA / NEW_DATA only
                    handler(*(char**)p, *(double*)(p + 16));
                }
                return count;
            }
            return 0;
        }

        public double Sum(IntPtr counter) { double s = 0; Read(counter, (n, v) => s += v); return s; }
        public double Max(IntPtr counter) { double m = double.NaN; Read(counter, (n, v) => { if (double.IsNaN(m) || v > m) m = v; }); return m; }

        public void Dispose()
        {
            if (query != IntPtr.Zero) { Native.PdhCloseQuery(query); query = IntPtr.Zero; }
            if (buf != IntPtr.Zero) { Marshal.FreeHGlobal(buf); buf = IntPtr.Zero; }
        }
    }

    /// <summary>
    /// Samples the whole system on a background thread. One NtQuerySystemInformation call gives CPU/IO/memory
    /// for every process (same source Task Manager uses); PDH gives GPU, disk, network, clock and temperature.
    /// Per-process metadata (user, command line, services) is resolved lazily, once, and only for processes
    /// that actually show up as notable.
    /// </summary>
    sealed unsafe class Collector : IDisposable
    {
        public Trace Trace { get; private set; }
        public bool Running => thread != null;
        public int IntervalMs = 2000;
        public bool CollectGpu = true;
        public string FilePath => writer?.Path;
        public event Action<Exception> Error;

        // self-overhead, for the status bar
        public volatile float SelfCpu, SelfMemMB;
        public volatile float LastSampleMs;

        static readonly int SelfPid = Process.GetCurrentProcess().Id;
        Thread thread;
        volatile bool stop;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        TraceWriter writer;

        // native state
        IntPtr spiBuf; int spiSize;
        PdhQuery pdh, gpuPdh;
        IntPtr cDiskR, cDiskW, cDiskIdle, cNet, cClock, cTemp, cGpu;
        long lastIdle, lastKernel, lastUser;
        long lastFileTime;
        readonly Stopwatch clock = new Stopwatch();
        double lastSec;

        sealed class Prev { public long Create, Cpu, Read, Write; public int Key; public int Gen; }
        readonly Dictionary<int, Prev> prev = new Dictionary<int, Prev>();
        int gen, nextKey;

        struct Cand
        {
            public int Pid; public long Create; public byte* Entry; public Prev P;
            public float Cpu, Gpu, Read, Write, Mem;
        }
        Cand[] cands = new Cand[1024];
        int[] order = new int[1024];

        readonly Dictionary<long, double> gpuPidEng = new Dictionary<long, double>();
        readonly Dictionary<int, double> gpuSysEng = new Dictionary<int, double>();
        readonly Dictionary<int, float> gpuByPid = new Dictionary<int, float>();
        PdhQuery.ItemHandler gpuHandler;

        readonly Dictionary<string, string> userCache = new Dictionary<string, string>();
        Dictionary<int, string> serviceMap = new Dictionary<int, string>();
        long serviceMapAt;

        public void Start(Trace trace, string filePath)
        {
            if (Running) return;
            Trace = trace;
            prev.Clear(); nextKey = 0;
            writer = filePath != null ? new TraceWriter(filePath, trace) : null;
            stop = false;
            thread = new Thread(Loop) { IsBackground = true, Name = "Collector", Priority = ThreadPriority.Highest };
            thread.Start();
        }

        public void Stop()
        {
            if (thread == null) return;
            stop = true; wake.Set();
            thread.Join(5000);
            thread = null;
            writer?.Dispose(); writer = null;
        }

        public void Dispose() => Stop();

        void Loop()
        {
            try
            {
                Init();
                Thread.Sleep(Math.Min(IntervalMs, 1000));
                while (!stop)
                {
                    var sw = Stopwatch.StartNew();
                    TakeSample();
                    LastSampleMs = (float)sw.Elapsed.TotalMilliseconds;
                    wake.WaitOne(Math.Max(250, IntervalMs - (int)sw.ElapsedMilliseconds));
                }
            }
            catch (Exception ex) { Error?.Invoke(ex); }
            finally
            {
                pdh?.Dispose(); gpuPdh?.Dispose(); pdh = gpuPdh = null;
                if (spiBuf != IntPtr.Zero) { Marshal.FreeHGlobal(spiBuf); spiBuf = IntPtr.Zero; spiSize = 0; }
            }
        }

        void Init()
        {
            var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)sizeof(Native.MEMORYSTATUSEX) };
            Native.GlobalMemoryStatusEx(ref ms);
            Trace.TotalMemBytes = (long)ms.ullTotalPhys;

            pdh = new PdhQuery();
            cDiskR = pdh.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
            cDiskW = pdh.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
            cDiskIdle = pdh.Add(@"\PhysicalDisk(_Total)\% Idle Time");
            cNet = pdh.Add(@"\Network Interface(*)\Bytes Total/sec");
            cClock = pdh.Add(@"\Processor Information(_Total)\% Processor Performance");
            cTemp = pdh.Add(@"\Thermal Zone Information(*)\Temperature");
            pdh.Collect();

            gpuPdh = new PdhQuery();
            cGpu = gpuPdh.Add(@"\GPU Engine(*)\Utilization Percentage");
            gpuPdh.Collect();
            gpuHandler = OnGpuItem;

            Native.GetSystemTimes(out lastIdle, out lastKernel, out lastUser);
            clock.Restart(); lastSec = 0;
            ReadProcesses(0, first: true);
        }

        void TakeSample()
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = now - lastSec;
            lastSec = now;
            if (dt <= 0.05) return;

            var s = new Sample { Ticks = DateTime.UtcNow.Ticks, IntervalSec = (float)dt };

            // total CPU (kernel time includes idle time)
            Native.GetSystemTimes(out long idle, out long kernel, out long user);
            long dI = idle - lastIdle, dK = kernel - lastKernel, dU = user - lastUser;
            lastIdle = idle; lastKernel = kernel; lastUser = user;
            long total = dK + dU;
            s.Cpu = total > 0 ? Clamp((float)(100.0 * (total - dI) / total)) : 0;

            var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)sizeof(Native.MEMORYSTATUSEX) };
            if (Native.GlobalMemoryStatusEx(ref ms))
            {
                s.MemPct = ms.dwMemoryLoad;
                s.MemUsedMB = (float)((ms.ullTotalPhys - ms.ullAvailPhys) / 1048576.0);
            }

            if (pdh.Collect())
            {
                s.DiskReadBps = (float)pdh.Sum(cDiskR);
                s.DiskWriteBps = (float)pdh.Sum(cDiskW);
                double idlePct = pdh.Max(cDiskIdle);
                s.DiskActivePct = double.IsNaN(idlePct) ? 0 : Clamp((float)(100 - idlePct));
                s.NetBps = (float)pdh.Sum(cNet);
                s.CpuClockPct = (float)pdh.Max(cClock);
                double k = pdh.Max(cTemp);
                s.TempC = double.IsNaN(k) || k <= 0 ? float.NaN : (float)(k - 273.15);
            }

            gpuByPid.Clear();
            if (CollectGpu && gpuPdh.Collect())
            {
                gpuPidEng.Clear(); gpuSysEng.Clear();
                gpuPdh.Read(cGpu, gpuHandler);
                double sys = 0;
                foreach (var kv in gpuSysEng) if (kv.Value > sys) sys = kv.Value;
                s.Gpu = Clamp((float)sys);
                foreach (var kv in gpuPidEng)
                {
                    int pid = (int)(kv.Key >> 32);
                    float v = Clamp((float)kv.Value);
                    if (!gpuByPid.TryGetValue(pid, out var cur) || v > cur) gpuByPid[pid] = v;
                }
            }

            s.Procs = ReadProcesses(dt, first: false);

            Trace.AddSample(s);
            writer?.Sample(s);
        }

        // GPU instance names look like: pid_1234_luid_0x00000000_0x0000C3B1_phys_0_eng_0_engtype_3D
        void OnGpuItem(char* name, double value)
        {
            if (value <= 0) return;
            char* p = name;
            if (p[0] != 'p' || p[1] != 'i' || p[2] != 'd' || p[3] != '_') return;
            p += 4;
            int pid = 0;
            while (*p >= '0' && *p <= '9') { pid = pid * 10 + (*p - '0'); p++; }
            // hash of LUID (adapter) + engine type => one "engine type per adapter" bucket
            int h = 17;
            char* luid = Find(p, "luid_");
            if (luid != null) for (char* q = luid; *q != 0 && !(*q == '_' && q[1] == 'p'); q++) h = h * 31 + *q;
            char* et = Find(p, "engtype_");
            if (et != null) for (char* q = et; *q != 0; q++) h = h * 31 + *q;

            long key = ((long)pid << 32) | (uint)h;
            gpuPidEng.TryGetValue(key, out var a); gpuPidEng[key] = a + value;
            gpuSysEng.TryGetValue(h, out var b); gpuSysEng[h] = b + value;
        }

        static char* Find(char* s, string token)
        {
            for (; *s != 0; s++)
            {
                int i = 0;
                while (i < token.Length && s[i] == token[i]) i++;
                if (i == token.Length) return s + i;
            }
            return null;
        }

        ProcSample[] ReadProcesses(double dt, bool first)
        {
            // one syscall snapshot of all processes
            if (spiBuf == IntPtr.Zero) { spiSize = 1 << 20; spiBuf = Marshal.AllocHGlobal(spiSize); }
            int st;
            while ((st = Native.NtQuerySystemInformation(Native.SystemProcessInformation, spiBuf, spiSize, out int need)) == Native.STATUS_INFO_LENGTH_MISMATCH)
            {
                Marshal.FreeHGlobal(spiBuf);
                spiSize = Math.Max(need, spiSize) + (128 << 10);
                spiBuf = Marshal.AllocHGlobal(spiSize);
            }
            if (st != 0) return Array.Empty<ProcSample>();

            gen++;
            int n = 0;
            int selfPid = SelfPid;
            double denom = dt * 1e7 * Trace.CpuCount; // 100ns units across all cores
            long nowFt = DateTime.UtcNow.ToFileTimeUtc();
            byte* e = (byte*)spiBuf;
            while (true)
            {
                int pid = (int)*(long*)(e + 0x50);
                long create = *(long*)(e + 0x20);
                long cpu = *(long*)(e + 0x28) + *(long*)(e + 0x30);
                long rd = *(long*)(e + 0xE8), wr = *(long*)(e + 0xF0);
                float memMB = (float)(*(long*)(e + 0x08) / 1048576.0); // private working set

                if (pid != 0)
                {
                    if (!prev.TryGetValue(pid, out var p) || p.Create != create)
                    {
                        // a process started since the last sample: all its counters are "new" usage
                        bool fresh = !first && create >= lastFileTime;
                        p = new Prev { Create = create, Cpu = fresh ? 0 : cpu, Read = fresh ? 0 : rd, Write = fresh ? 0 : wr };
                        prev[pid] = p;
                    }
                    p.Gen = gen;
                    if (!first)
                    {
                        if (n == cands.Length) { Array.Resize(ref cands, n * 2); Array.Resize(ref order, n * 2); }
                        ref var c = ref cands[n++];
                        c.Pid = pid; c.Create = create; c.Entry = e; c.P = p;
                        c.Cpu = Clamp((float)((cpu - p.Cpu) * 100.0 / denom));
                        c.Read = (float)(Math.Max(0, rd - p.Read) / dt);
                        c.Write = (float)(Math.Max(0, wr - p.Write) / dt);
                        c.Mem = memMB;
                        c.Gpu = gpuByPid.TryGetValue(pid, out var g) ? g : 0;
                        if (pid == selfPid) { SelfCpu = c.Cpu; SelfMemMB = memMB; }
                    }
                    p.Cpu = cpu; p.Read = rd; p.Write = wr;
                }

                int next = *(int*)e;
                if (next == 0) break;
                e += next;
            }
            lastFileTime = nowFt;

            // forget exited processes
            if ((gen & 31) == 0)
            {
                var dead = new List<int>();
                foreach (var kv in prev) if (kv.Value.Gen != gen) dead.Add(kv.Key);
                foreach (var d in dead) prev.Remove(d);
            }
            if (first) return null;

            return SelectNotable(n);
        }

        /// <summary>Keep only processes that matter in this sample (keeps traces small).</summary>
        ProcSample[] SelectNotable(int n)
        {
            for (int i = 0; i < n; i++) order[i] = i;
            var arr = cands;
            Array.Sort(order, 0, n, Comparer<int>.Create((a, b) => arr[b].Cpu.CompareTo(arr[a].Cpu)));

            const int MaxProcs = 32;
            var pick = new List<int>(MaxProcs);
            for (int r = 0; r < n && pick.Count < MaxProcs; r++)
            {
                ref var c = ref cands[order[r]];
                bool notable = (r < 10 && c.Cpu >= 0.05f) || c.Cpu >= 1f || c.Gpu >= 1f || c.Read + c.Write >= 256 * 1024;
                if (notable) pick.Add(order[r]);
            }
            // plus the 3 biggest memory users
            for (int k = 0; k < 3; k++)
            {
                int best = -1;
                for (int i = 0; i < n; i++)
                    if (!pick.Contains(i) && (best < 0 || cands[i].Mem > cands[best].Mem)) best = i;
                if (best >= 0) pick.Add(best);
            }

            var res = new ProcSample[pick.Count];
            for (int i = 0; i < pick.Count; i++)
            {
                ref var c = ref cands[pick[i]];
                if (c.P.Key == 0) c.P.Key = Register(c);
                res[i] = new ProcSample { Key = c.P.Key, Cpu = c.Cpu, Gpu = c.Gpu, ReadBps = c.Read, WriteBps = c.Write, MemMB = c.Mem };
            }
            return res;
        }

        int Register(in Cand c)
        {
            byte* e = c.Entry;
            int len = *(ushort*)(e + 0x38);
            char* namePtr = *(char**)(e + 0x40);
            string name = namePtr != null && len > 0 ? new string(namePtr, 0, len / 2) : (c.Pid == 4 ? "System" : "pid " + c.Pid);
            int session = *(int*)(e + 0x64);

            var info = new ProcInfo { Key = ++nextKey, Pid = c.Pid, CreateTime = c.Create, Name = name };
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, c.Pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024); int size = sb.Capacity;
                    if (Native.QueryFullProcessImageName(h, 0, sb, ref size)) info.Path = sb.ToString();
                    info.User = GetUser(h) ?? "";
                    info.CommandLine = GetCommandLine(h) ?? "";
                }
                finally { Native.CloseHandle(h); }
            }
            if (info.User.Length == 0)
                info.User = c.Pid == 4 || session == 0 ? "SYSTEM (restricted)" : "session " + session;
            if (session == 0) info.Services = GetServices(c.Pid);

            Trace.AddProc(info);
            writer?.Proc(info);
            return info.Key;
        }

        string GetUser(IntPtr hProc)
        {
            if (!Native.OpenProcessToken(hProc, Native.TOKEN_QUERY, out var tok)) return null;
            IntPtr buf = IntPtr.Zero;
            try
            {
                Native.GetTokenInformation(tok, Native.TokenUser, IntPtr.Zero, 0, out int len);
                if (len <= 0) return null;
                buf = Marshal.AllocHGlobal(len);
                if (!Native.GetTokenInformation(tok, Native.TokenUser, buf, len, out len)) return null;
                var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buf));
                string key = sid.Value;
                if (userCache.TryGetValue(key, out var name)) return name;
                try
                {
                    name = ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
                    if (name.StartsWith(@"NT AUTHORITY\", StringComparison.OrdinalIgnoreCase)) name = name.Substring(13);
                }
                catch { name = key; }
                userCache[key] = name;
                return name;
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                Native.CloseHandle(tok);
            }
        }

        static string GetCommandLine(IntPtr hProc)
        {
            Native.NtQueryInformationProcess(hProc, Native.ProcessCommandLineInformation, IntPtr.Zero, 0, out int len);
            if (len <= 0 || len > 1 << 20) return null;
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                if (Native.NtQueryInformationProcess(hProc, Native.ProcessCommandLineInformation, buf, len, out _) != 0) return null;
                int bytes = *(ushort*)buf;
                char* p = *(char**)((byte*)buf + 8);
                if (p == null || bytes == 0) return null;
                var s = new string(p, 0, bytes / 2);
                return s.Length > 2048 ? s.Substring(0, 2048) : s;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>Names of Windows services hosted by a pid (svchost & friends) — makes "svchost.exe" meaningful.</summary>
        string GetServices(int pid)
        {
            long now = Environment.TickCount;
            if (!serviceMap.ContainsKey(pid) && now - serviceMapAt > 5000)
            {
                serviceMapAt = now;
                serviceMap = EnumServices();
            }
            return serviceMap.TryGetValue(pid, out var s) ? s : "";
        }

        static Dictionary<int, string> EnumServices()
        {
            var map = new Dictionary<int, string>();
            IntPtr scm = Native.OpenSCManager(null, null, Native.SC_MANAGER_ENUMERATE_SERVICE);
            if (scm == IntPtr.Zero) return map;
            IntPtr buf = IntPtr.Zero;
            try
            {
                int resume = 0;
                Native.EnumServicesStatusEx(scm, 0, 0x30 /*WIN32*/, 1 /*ACTIVE*/, IntPtr.Zero, 0, out int need, out _, ref resume, null);
                if (need <= 0) return map;
                need += 4096;
                buf = Marshal.AllocHGlobal(need);
                resume = 0;
                if (!Native.EnumServicesStatusEx(scm, 0, 0x30, 1, buf, need, out _, out int count, ref resume, null)) return map;
                var lists = new Dictionary<int, List<string>>();
                for (int i = 0; i < count; i++)
                {
                    byte* item = (byte*)buf + i * 56; // ENUM_SERVICE_STATUS_PROCESSW on x64
                    string svc = new string(*(char**)item);
                    int spid = *(int*)(item + 16 + 7 * 4);
                    if (spid == 0) continue;
                    if (!lists.TryGetValue(spid, out var l)) lists[spid] = l = new List<string>();
                    l.Add(svc);
                }
                foreach (var kv in lists)
                {
                    var l = kv.Value;
                    map[kv.Key] = l.Count <= 4 ? string.Join(", ", l) : string.Join(", ", l.GetRange(0, 4)) + $" +{l.Count - 4}";
                }
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                Native.CloseServiceHandle(scm);
            }
            return map;
        }

        static float Clamp(float v) => v < 0 || float.IsNaN(v) ? 0 : v > 100 ? 100 : v;
    }
}

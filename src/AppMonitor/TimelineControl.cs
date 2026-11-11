using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace AppMonitor
{
    /// <summary>
    /// Multi-lane zoomable timeline. Wheel = zoom, left-drag = select range, click = select one sample,
    /// right-drag = pan, overview strip at the bottom = jump anywhere, double-click = clear selection.
    /// The plot is rendered into a cached bitmap; crosshair / selection / tooltip are drawn on top.
    /// </summary>
    sealed class TimelineControl : Control
    {
        enum Lane { Cpu, Gpu, Fan, Temp, Disk, Mem, Clock, Net }

        static readonly Color[] Palette =
        {
            Color.FromArgb(0x4E, 0x79, 0xA7), Color.FromArgb(0xF2, 0x8E, 0x2B), Color.FromArgb(0xE1, 0x57, 0x59),
            Color.FromArgb(0x76, 0xB7, 0xB2), Color.FromArgb(0x59, 0xA1, 0x4F), Color.FromArgb(0xED, 0xC9, 0x48),
            Color.FromArgb(0xB0, 0x7A, 0xA1), Color.FromArgb(0xFF, 0x9D, 0xA7),
        };
        static readonly Color OtherColor = Color.FromArgb(0xD5, 0xD8, 0xDC);
        static readonly Color Ink = Color.FromArgb(0x30, 0x34, 0x3A);
        static readonly Color Muted = Color.FromArgb(0x80, 0x86, 0x8E);
        static readonly Color Grid = Color.FromArgb(0xEC, 0xEE, 0xF0);
        static readonly Color LaneBg = Color.FromArgb(0xFB, 0xFB, 0xFC);
        static readonly Color HiColor = Color.FromArgb(0xD0, 0x10, 0x60);

        Trace trace;
        public long ViewStart, ViewEnd;
        public long WindowTicks = TimeSpan.FromMinutes(10).Ticks; // 0 = whole trace
        bool follow = true;
        public long SelStart, SelEnd;
        public HashSet<int> HighlightKeys = new HashSet<int>();
        public string HighlightName = "";

        public event EventHandler SelectionChanged, ViewChanged, FollowChanged;
        /// <summary>A process swimlane was clicked (arg = display name, or "" to clear).</summary>
        public event Action<string> ProcessClicked;

        Bitmap cache;
        bool dirty = true;
        Rectangle plot, overview, swim;
        int rowH;
        readonly List<(string name, Rectangle rect)> rows = new List<(string, Rectangle)>();
        readonly List<(Lane lane, Rectangle rect)> lanes = new List<(Lane, Rectangle)>();
        Point mouse = new Point(-1, -1);
        enum Drag { None, Select, Pan, Overview, Divider }
        Drag drag;
        int dragX; long dragT, dragViewStart, grabOffset;
        long ovStart, ovEnd;

        public TimelineControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        public Trace Trace
        {
            get => trace;
            set
            {
                trace = value;
                SelStart = SelEnd = 0;
                HighlightKeys.Clear(); HighlightName = "";
                if (trace != null && !trace.IsLive) { follow = false; WindowTicks = 0; FitAll(); }
                else follow = true;
                DataChanged();
                FollowChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool Follow
        {
            get => follow;
            set { if (follow == value) return; follow = value; DataChanged(); FollowChanged?.Invoke(this, EventArgs.Empty); }
        }

        public bool HasSelection => SelEnd > SelStart;

        public void GetRange(out long a, out long b)
        {
            if (HasSelection) { a = SelStart; b = SelEnd; } else { a = ViewStart; b = ViewEnd; }
        }

        public void ClearSelection()
        {
            if (!HasSelection) return;
            SelStart = SelEnd = 0; Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Select(long a, long b)
        {
            Follow = false; // a selection pins the view, otherwise it would scroll away under the user
            SelStart = a; SelEnd = b; Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetWindow(long ticks)
        {
            WindowTicks = ticks;
            if (follow || trace == null) { DataChanged(); }
            else if (ticks <= 0) FitAll();
            else { long c = (ViewStart + ViewEnd) / 2; SetView(c - ticks / 2, c + ticks / 2); }
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ShowRange(long a, long b)
        {
            long pad = Math.Max((b - a) / 4, TimeSpan.FromSeconds(10).Ticks);
            follow = false; FollowChanged?.Invoke(this, EventArgs.Empty);
            SetView(a - pad, b + pad);
        }

        void FitAll()
        {
            if (trace == null || !trace.GetExtent(out long s, out long e)) return;
            SetView(s, Math.Max(e, s + TimeSpan.FromMinutes(1).Ticks));
        }

        void SetView(long a, long b)
        {
            long span = Math.Max(b - a, TimeSpan.FromSeconds(20).Ticks);
            span = Math.Min(span, TimeSpan.FromDays(60).Ticks);
            // keep the view on the recording: never scroll off into empty time
            if (trace != null && trace.GetExtent(out long s, out long e))
            {
                long pad = span / 40;
                if (span >= e - s) a = s - pad;          // whole recording fits: start it at the left edge
                else a = Math.Max(s - pad, Math.Min(a, e + pad - span));
            }
            ViewStart = a; ViewEnd = a + span;
            dirty = true; Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Call when new samples arrived or view settings changed.</summary>
        public void DataChanged()
        {
            if (trace != null && follow)
            {
                long end = trace.GetExtent(out long s, out long e) ? e : DateTime.UtcNow.Ticks;
                if (WindowTicks <= 0) { ViewStart = trace.GetExtent(out s, out _) ? s : end - TimeSpan.FromMinutes(1).Ticks; ViewEnd = Math.Max(end, ViewStart + TimeSpan.FromMinutes(1).Ticks); }
                else if (trace.GetExtent(out s, out e) && e - s < WindowTicks)
                {
                    // recording shorter than the window: anchor it at the left edge and let it grow rightwards
                    ViewStart = s; ViewEnd = s + WindowTicks;
                }
                else { ViewEnd = end; ViewStart = end - WindowTicks; } // full: scroll with the newest data on the right
            }
            dirty = true;
            Invalidate();
        }

        // ------------------------------------------------------------------ layout

        void DoLayout()
        {
            int axisH = Font.Height + 8;
            int ovH = Math.Max(30, Font.Height * 2 + 4);
            int gutter = Math.Max(150, Font.Height * 11);
            plot = new Rectangle(gutter, axisH, Math.Max(10, Width - gutter - 58), Math.Max(10, Height - axisH - ovH - 12));
            overview = new Rectangle(plot.Left, plot.Bottom + 8, plot.Width, ovH);

            // (lane, share of spare height, minimum height in lines of text so it stays readable)
            var defs = new List<(Lane lane, float w, float minLines)> { (Lane.Cpu, 3f, 3f), (Lane.Gpu, 2f, 2.5f) };
            if (trace != null && trace.HasFans) defs.Add((Lane.Fan, 1.8f, 3f));
            if (trace != null && (trace.HasTempSensors || trace.HasTemp)) defs.Add((Lane.Temp, trace.HasTempSensors ? 1.8f : 1f, trace.HasTempSensors ? 3f : 2f));
            defs.AddRange(new[] { (Lane.Disk, 1.4f, 2f), (Lane.Mem, 1f, 1.6f), (Lane.Clock, 1f, 1.6f), (Lane.Net, 1f, 1.6f) });
            const int gap = 5;
            // Tracealyzer-style split: system graphs on top, one swimlane per process below.
            // SwimFraction is user-adjustable by dragging the "Processes" divider.
            rowH = Font.Height + 5;
            int headH = Font.Height + 12;
            float minSum = defs.Sum(d => d.minLines * Font.Height) + gap * (defs.Count - 1);
            int lanesH = (int)Math.Max(plot.Height * (1 - SwimFraction) - headH, Math.Min(minSum, plot.Height * 0.70f - headH));
            lanesH = Math.Max(defs.Count * 8, Math.Min(lanesH, plot.Height - headH - rowH * 3));
            int swimH = Math.Max(rowH, plot.Height - lanesH - headH);
            swim = new Rectangle(plot.Left, plot.Bottom - swimH, plot.Width, swimH);
            divider = new Rectangle(0, plot.Top + lanesH, Width, headH);

            // every lane gets its minimum, the remainder is shared by weight (or everything shrinks if even minimums don't fit)
            float avail = lanesH - gap * (defs.Count - 1);
            float mins = defs.Sum(d => d.minLines * Font.Height), weights = defs.Sum(d => d.w);
            float scale = mins > avail ? avail / mins : 1f, spare = Math.Max(0, avail - mins);
            lanes.Clear();
            float y = plot.Top;
            foreach (var d in defs)
            {
                float h = d.minLines * Font.Height * scale + spare * d.w / weights;
                lanes.Add((d.lane, new Rectangle(plot.Left, (int)y, plot.Width, Math.Max(8, (int)h))));
                y += h + gap;
            }
        }

        /// <summary>Share of the plot height given to the process swimlanes (dragging the divider changes it).</summary>
        public float SwimFraction = 0.40f;
        public event EventHandler SwimFractionChanged;
        Rectangle divider;

        float X(long t) => plot.Left + (float)((t - ViewStart) / (double)(ViewEnd - ViewStart) * plot.Width);
        long TimeAt(int x) => ViewStart + (long)((x - plot.Left) / (double)plot.Width * (ViewEnd - ViewStart));

        // ------------------------------------------------------------------ data → frame

        sealed class Frame
        {
            public int N;
            public float[] X;
            public bool[] Brk;
            public float[] Cpu, Gpu, Mem, DiskR, DiskW, Net, Temp, Clock, HiCpu, HiGpu, HiIo;
            public float[][] CpuS, GpuS;
            public string[] CpuNames, GpuNames;
            public double[] CpuAvg, GpuAvg;
            public string[] RowNames;
            public float[][] RowS;   // per-process activity: max(cpu, gpu)
            public double[] RowAvg;
            public float[] Ov; // overview: max cpu per pixel
            public float[][] Sens; // per hardware sensor (fan rpm / temp), max within bucket
        }

        Frame BuildFrame()
        {
            var f = new Frame();
            lock (trace.Sync)
            {
                var ss = trace.Samples;
                int count = ss.Count;
                f.Ov = BuildOverview();
                if (count == 0) return f;
                int i0 = Math.Max(0, trace.LowerBound(ViewStart) - 1);
                int i1 = Math.Min(count - 1, trace.LowerBound(ViewEnd));

                // which processes (by name) dominate the visible window
                var cpuTot = new Dictionary<string, double>();
                var actTot = new Dictionary<string, double>();
                var gpuTot = new Dictionary<string, double>();
                double dur = 0;
                for (int i = i0; i <= i1; i++)
                {
                    var s = ss[i]; dur += s.IntervalSec;
                    foreach (var p in s.Procs)
                    {
                        if (!trace.Procs.TryGetValue(p.Key, out var info)) continue;
                        if (p.Cpu > 0) { cpuTot.TryGetValue(info.Name, out var c); cpuTot[info.Name] = c + p.Cpu * s.IntervalSec; }
                        if (p.Gpu > 0) { gpuTot.TryGetValue(info.Name, out var g); gpuTot[info.Name] = g + p.Gpu * s.IntervalSec; }
                        float act = Math.Max(p.Cpu, p.Gpu);
                        if (act > 0) { actTot.TryGetValue(info.Display, out var a); actTot[info.Display] = a + act * s.IntervalSec; }
                    }
                }
                var cpuTop = cpuTot.OrderByDescending(kv => kv.Value).Take(8).Where(kv => kv.Value / Math.Max(dur, 1e-9) >= 0.1).ToList();
                var gpuTop = gpuTot.OrderByDescending(kv => kv.Value).Take(6).Where(kv => kv.Value / Math.Max(dur, 1e-9) >= 0.1).ToList();
                f.CpuNames = cpuTop.Select(kv => kv.Key).ToArray(); f.CpuAvg = cpuTop.Select(kv => kv.Value / dur).ToArray();
                f.GpuNames = gpuTop.Select(kv => kv.Key).ToArray(); f.GpuAvg = gpuTop.Select(kv => kv.Value / dur).ToArray();
                var rowTop = actTot.OrderByDescending(kv => kv.Value).Take(40).Where(kv => kv.Value / Math.Max(dur, 1e-9) >= 0.05).ToList();
                f.RowNames = rowTop.Select(kv => kv.Key).ToArray(); f.RowAvg = rowTop.Select(kv => kv.Value / dur).ToArray();
                var cpuIdx = new Dictionary<int, int>(); var gpuIdx = new Dictionary<int, int>(); var rowIdx = new Dictionary<int, int>();

                int m = i1 - i0 + 1;
                bool bucket = m > plot.Width;
                int cap = bucket ? plot.Width + 4 : m;
                f.X = new float[cap]; f.Brk = new bool[cap];
                f.Cpu = new float[cap]; f.Gpu = new float[cap]; f.Mem = new float[cap]; f.DiskR = new float[cap]; f.DiskW = new float[cap];
                f.Net = new float[cap]; f.Temp = new float[cap]; f.Clock = new float[cap];
                f.HiCpu = new float[cap]; f.HiGpu = new float[cap]; f.HiIo = new float[cap];
                f.Sens = new float[trace.Sensors.Count][]; for (int k = 0; k < f.Sens.Length; k++) f.Sens[k] = new float[cap];
                f.CpuS = new float[f.CpuNames.Length][]; for (int k = 0; k < f.CpuS.Length; k++) f.CpuS[k] = new float[cap];
                f.GpuS = new float[f.GpuNames.Length][]; for (int k = 0; k < f.GpuS.Length; k++) f.GpuS[k] = new float[cap];
                f.RowS = new float[f.RowNames.Length][]; for (int k = 0; k < f.RowS.Length; k++) f.RowS[k] = new float[cap];
                var cnt = new int[cap];

                int cur = -1, lastPix = int.MinValue;
                for (int i = i0; i <= i1; i++)
                {
                    var s = ss[i];
                    float x = X(s.Ticks);
                    int pix = (int)Math.Floor(x);
                    bool brk = i > i0 && Trace.IsGap(ss[i - 1], s);
                    if (!bucket || pix != lastPix || brk)
                    {
                        if (cur + 1 >= cap) break;
                        cur++;
                        f.X[cur] = bucket ? pix + 0.5f : x; f.Brk[cur] = brk;
                        f.Temp[cur] = float.NaN; f.Clock[cur] = float.NaN;
                        foreach (var a in f.Sens) a[cur] = float.NaN;
                    }
                    lastPix = pix;
                    cnt[cur]++;
                    // totals: max within bucket so short spikes are never hidden
                    f.Cpu[cur] = Math.Max(f.Cpu[cur], s.Cpu); f.Gpu[cur] = Math.Max(f.Gpu[cur], s.Gpu);
                    f.Mem[cur] = Math.Max(f.Mem[cur], s.MemPct);
                    f.DiskR[cur] = Math.Max(f.DiskR[cur], s.DiskReadBps); f.DiskW[cur] = Math.Max(f.DiskW[cur], s.DiskWriteBps);
                    f.Net[cur] = Math.Max(f.Net[cur], s.NetBps);
                    if (!float.IsNaN(s.TempC) && !(f.Temp[cur] >= s.TempC)) f.Temp[cur] = s.TempC;
                    if (!float.IsNaN(s.CpuClockPct) && !(f.Clock[cur] >= s.CpuClockPct)) f.Clock[cur] = s.CpuClockPct;
                    if (s.Sensors != null)
                        for (int k = 0; k < s.Sensors.Length && k < f.Sens.Length; k++)
                        {
                            float v = s.Sensors[k];
                            if (!float.IsNaN(v) && !(f.Sens[k][cur] >= v)) f.Sens[k][cur] = v;
                        }
                    // per-process series: averaged within bucket
                    foreach (var p in s.Procs)
                    {
                        if (!cpuIdx.TryGetValue(p.Key, out int ci))
                        {
                            trace.Procs.TryGetValue(p.Key, out var info);
                            string nm = info?.Name ?? "";
                            cpuIdx[p.Key] = ci = Array.IndexOf(f.CpuNames, nm);
                            gpuIdx[p.Key] = Array.IndexOf(f.GpuNames, nm);
                            rowIdx[p.Key] = Array.IndexOf(f.RowNames, info?.Display ?? "");
                        }
                        if (ci >= 0) f.CpuS[ci][cur] += p.Cpu;
                        int gi = gpuIdx[p.Key];
                        if (gi >= 0) f.GpuS[gi][cur] += p.Gpu;
                        int ri = rowIdx[p.Key];
                        if (ri >= 0) f.RowS[ri][cur] += Math.Max(p.Cpu, p.Gpu);
                        if (HighlightKeys.Contains(p.Key)) { f.HiCpu[cur] += p.Cpu; f.HiGpu[cur] += p.Gpu; f.HiIo[cur] += p.ReadBps + p.WriteBps; }
                    }
                }
                f.N = cur + 1;
                for (int j = 0; j < f.N; j++)
                {
                    if (cnt[j] <= 1) continue;
                    float inv = 1f / cnt[j];
                    foreach (var a in f.CpuS) a[j] *= inv;
                    foreach (var a in f.GpuS) a[j] *= inv;
                    foreach (var a in f.RowS) a[j] *= inv;
                    f.HiCpu[j] *= inv; f.HiGpu[j] *= inv; f.HiIo[j] *= inv;
                }
            }
            return f;
        }

        float[] BuildOverview()
        {
            var ov = new float[Math.Max(1, overview.Width)];
            var ss = trace.Samples;
            if (ss.Count == 0) { ovStart = ViewStart; ovEnd = ViewEnd; return ov; }
            // the strip always maps exactly the recording (independent of the current view)
            ovStart = ss[0].StartTicks;
            ovEnd = Math.Max(ss[ss.Count - 1].Ticks, ovStart + TimeSpan.TicksPerMinute);
            double scale = ov.Length / (double)(ovEnd - ovStart);
            for (int i = 0; i < ss.Count; i++)
            {
                int px = (int)((ss[i].Ticks - ovStart) * scale);
                if (px < 0) px = 0; if (px >= ov.Length) px = ov.Length - 1;
                float v = Math.Max(ss[i].Cpu, ss[i].Gpu);
                if (v > ov[px]) ov[px] = v;
            }
            return ov;
        }

        // ------------------------------------------------------------------ rendering

        void RenderCache()
        {
            DoLayout();
            if (cache == null || cache.Width != Width || cache.Height != Height)
            {
                cache?.Dispose();
                cache = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var screen = CreateGraphics()) cache.SetResolution(screen.DpiX, screen.DpiY);
            }
            using var g = Graphics.FromImage(cache);
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            if (trace == null)
            {
                DrawText(g, "Press ● Record to start, or Open a saved trace.", Font, ClientRectangle, Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            var f = BuildFrame();
            DrawAxis(g);
            foreach (var (lane, r) in lanes) DrawLane(g, lane, r, f);
            DrawSwimlanes(g, f);
            DrawOverview(g, f.Ov);
            dirty = false;
        }

        void DrawAxis(Graphics g)
        {
            long span = ViewEnd - ViewStart;
            long[] steps = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400, 172800, 604800 };
            long step = steps[steps.Length - 1] * TimeSpan.TicksPerSecond;
            foreach (var s in steps)
            {
                long st = s * TimeSpan.TicksPerSecond;
                if (plot.Width * (double)st / span >= TextRenderer.MeasureText("00:00:00", Font).Width + 30) { step = st; break; }
            }
            long offset = DateTime.UtcNow.Ticks - DateTime.Now.Ticks; // utc - local
            long localStart = ViewStart - offset;
            long first = (localStart / step + 1) * step;
            bool showSec = step < TimeSpan.TicksPerMinute;
            bool showDate = span > TimeSpan.FromHours(20).Ticks || new DateTime(ViewStart).Date != new DateTime(ViewEnd).Date;
            using var gridPen = new Pen(Grid);
            for (long t = first; t < localStart + span; t += step)
            {
                float x = X(t + offset);
                var dt = new DateTime(t);
                string label = step >= TimeSpan.TicksPerDay ? dt.ToString("ddd d MMM")
                    : (showDate && dt.TimeOfDay.Ticks < step ? dt.ToString("ddd ") : "") + dt.ToString(showSec ? "HH:mm:ss" : "HH:mm");
                g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
                var sz = TextRenderer.MeasureText(label, Font);
                DrawText(g, label, Font, new Point((int)x - sz.Width / 2, 3), Muted);
            }
        }

        void DrawLane(Graphics g, Lane lane, Rectangle r, Frame f)
        {
            using (var bg = new SolidBrush(LaneBg)) g.FillRectangle(bg, r);
            using (var gp = new Pen(Grid))
                for (int k = 1; k < 4; k++) g.DrawLine(gp, r.Left, r.Top + r.Height * k / 4f, r.Right, r.Top + r.Height * k / 4f);

            string title; string unitMax; float max;
            var legend = new List<(Color, string)>();
            switch (lane)
            {
                case Lane.Cpu:
                    max = 100; title = "CPU"; unitMax = "100%";
                    DrawStacked(g, r, f, f.CpuS, f.Cpu, max);
                    for (int k = 0; k < (f.CpuNames?.Length ?? 0); k++) legend.Add((Palette[k], $"{Short(f.CpuNames[k])} {f.CpuAvg[k]:0.#}%"));
                    if (HighlightKeys.Count > 0) DrawLine(g, r, f, f.HiCpu, max, HiColor, 2.2f);
                    break;
                case Lane.Gpu:
                    max = 100; title = "GPU"; unitMax = "100%";
                    DrawStacked(g, r, f, f.GpuS, f.Gpu, max);
                    for (int k = 0; k < (f.GpuNames?.Length ?? 0); k++) legend.Add((Palette[k], $"{Short(f.GpuNames[k])} {f.GpuAvg[k]:0.#}%"));
                    if (HighlightKeys.Count > 0) DrawLine(g, r, f, f.HiGpu, max, HiColor, 2.2f);
                    break;
                case Lane.Disk:
                    max = NiceMax(Math.Max(MaxOf(f.DiskR, f.N), MaxOf(f.DiskW, f.N)), 1024 * 1024);
                    title = "Disk"; unitMax = Analysis.Bytes(max) + "/s";
                    DrawArea(g, r, f, f.DiskR, max, Palette[0]);
                    DrawLine(g, r, f, f.DiskW, max, Palette[1], 1.4f);
                    legend.Add((Palette[0], "read")); legend.Add((Palette[1], "write"));
                    if (HighlightKeys.Count > 0) DrawLine(g, r, f, f.HiIo, max, HiColor, 2.2f);
                    break;
                case Lane.Mem:
                    max = 100; title = "Memory"; unitMax = "100%";
                    DrawArea(g, r, f, f.Mem, max, Palette[3]);
                    break;
                case Lane.Clock:
                    max = Math.Max(150, NiceMax(MaxOf(f.Clock, f.N), 50)); title = "CPU clock (% of base — >100 = turbo, heat)"; unitMax = $"{max:0}%";
                    using (var p = new Pen(Color.FromArgb(0xC0, 0xC4, 0xCA)) { DashStyle = DashStyle.Dash })
                    { float y = r.Bottom - 100 / max * r.Height; g.DrawLine(p, r.Left, y, r.Right, y); }
                    DrawLine(g, r, f, f.Clock, max, Palette[5], 1.4f);
                    break;
                case Lane.Fan:
                    max = NiceMax(SensorMax(f, SensorKind.Fan), 1000); title = "Fans (RPM)"; unitMax = $"{max:0}";
                    DrawSensors(g, r, f, SensorKind.Fan, max, legend, " rpm");
                    break;
                case Lane.Temp:
                    if (trace.HasTempSensors)
                    {
                        max = Math.Max(100, NiceMax(SensorMax(f, SensorKind.Temp), 10)); title = "Temperature (°C, peak in view)"; unitMax = $"{max:0}°C";
                        DrawSensors(g, r, f, SensorKind.Temp, max, legend, "°C");
                    }
                    else
                    {
                        max = Math.Max(100, NiceMax(MaxOf(f.Temp, f.N), 10)); title = "Temperature (ACPI thermal zone — often inaccurate)"; unitMax = $"{max:0}°C";
                        DrawLine(g, r, f, f.Temp, max, Palette[2], 1.6f);
                    }
                    break;
                default:
                    max = NiceMax(MaxOf(f.Net, f.N), 128 * 1024); title = "Network"; unitMax = Analysis.Bytes(max) + "/s";
                    DrawArea(g, r, f, f.Net, max, Palette[6]);
                    break;
            }

            // title in the left gutter (Tracealyzer-style), legend inside the lane
            using var bold = new Font(Font, FontStyle.Bold);
            int paren = title.IndexOf(" (");
            string head = paren > 0 ? title.Substring(0, paren) : title, sub = paren > 0 ? title.Substring(paren + 1) : "";
            DrawText(g, head, bold, new Point(8, r.Top + 1), Ink);
            if (sub.Length > 0 && r.Height > bold.Height * 2)
                DrawText(g, sub, Font, new Rectangle(8, r.Top + bold.Height + 1, plot.Left - 14, r.Height - bold.Height), Muted, TextFormatFlags.WordBreak);
            int x = r.Left + 4, y0 = r.Top + 2;
            foreach (var (c, text) in legend)
            {
                var sz = TextRenderer.MeasureText(text, Font);
                if (x + sz.Width + 14 > r.Right) break;
                using (var wash = new SolidBrush(Color.FromArgb(200, 255, 255, 255))) g.FillRectangle(wash, x, y0, sz.Width + 12, sz.Height);
                using (var b = new SolidBrush(c)) g.FillRectangle(b, x + 1, y0 + sz.Height / 2 - 4, 8, 8);
                DrawText(g, text, Font, new Point(x + 10, y0), Ink);
                x += sz.Width + 16;
            }
            DrawText(g, unitMax, Font, new Point(r.Right + 4, r.Top - 2), Muted);
        }

        float SensorMax(Frame f, SensorKind kind)
        {
            float m = 0;
            if (f.Sens == null) return m;
            for (int k = 0; k < f.Sens.Length; k++)
                if (trace.Sensors[k].Kind == kind) m = Math.Max(m, MaxOf(f.Sens[k], f.N));
            return m;
        }

        /// <summary>One line per sensor of this kind that has data in view (disconnected fans reading 0 are skipped).</summary>
        void DrawSensors(Graphics g, Rectangle r, Frame f, SensorKind kind, float max, List<(Color, string)> legend, string unit)
        {
            int ci = 0;
            if (f.Sens == null) return;
            for (int k = 0; k < f.Sens.Length; k++)
            {
                if (trace.Sensors[k].Kind != kind) continue;
                var v = f.Sens[k];
                bool hasData = false;
                for (int i = 0; i < f.N && !hasData; i++) hasData = !float.IsNaN(v[i]);
                if (!hasData) continue;
                float peak = MaxOf(v, f.N);
                var c = Palette[ci++ % Palette.Length];
                DrawLine(g, r, f, v, max, c, 1.5f);
                // GPUs stop their fans entirely at low load (zero-RPM mode): say so instead of hiding the fan
                string val = kind == SensorKind.Fan && peak <= 0 ? "stopped" : $"{peak:0}{unit}";
                legend.Add((c, $"{trace.Sensors[k].Name} {val}"));
            }
        }

        static string Short(string s) => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s.Substring(0, s.Length - 4) : s;

        // GDI+ text: GDI's TextRenderer produces fringed ClearType glyphs when drawing into an off-screen bitmap.
        static void DrawText(Graphics g, string s, Font f, Point p, Color c, TextFormatFlags flags = 0)
        {
            using var b = new SolidBrush(c);
            using var sf = new StringFormat(StringFormat.GenericDefault) { FormatFlags = StringFormatFlags.NoWrap, HotkeyPrefix = System.Drawing.Text.HotkeyPrefix.None };
            g.DrawString(s, f, b, p, sf);
        }

        static void DrawText(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags flags = 0)
        {
            using var b = new SolidBrush(c);
            using var sf = new StringFormat(StringFormat.GenericDefault) { HotkeyPrefix = System.Drawing.Text.HotkeyPrefix.None };
            if ((flags & TextFormatFlags.WordBreak) == 0) sf.FormatFlags |= StringFormatFlags.NoWrap;
            if ((flags & TextFormatFlags.EndEllipsis) != 0) sf.Trimming = StringTrimming.EllipsisCharacter;
            if ((flags & TextFormatFlags.HorizontalCenter) != 0) sf.Alignment = StringAlignment.Center;
            if ((flags & TextFormatFlags.VerticalCenter) != 0) sf.LineAlignment = StringAlignment.Center;
            g.DrawString(s, f, b, r, sf);
        }

        static float MaxOf(float[] a, int n) { float m = 0; for (int i = 0; i < n; i++) if (a[i] > m) m = a[i]; return m; }

        static float NiceMax(float v, float floor)
        {
            v = Math.Max(v, floor);
            double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
            foreach (var k in new[] { 1, 2, 2.5, 5, 10 }) if (k * mag >= v) return (float)(k * mag);
            return (float)(10 * mag);
        }

        static float Y(Rectangle r, float v, float max) => r.Bottom - Math.Min(1f, Math.Max(0f, v / max)) * r.Height;

        IEnumerable<(int a, int b)> Segments(Frame f)
        {
            int a = 0;
            for (int i = 1; i <= f.N; i++)
                if (i == f.N || f.Brk[i]) { yield return (a, i); a = i; }
        }

        void DrawStacked(Graphics g, Rectangle r, Frame f, float[][] series, float[] total, float max)
        {
            if (f.N == 0) return;
            var cum = new float[f.N];
            var next = new float[f.N];
            g.SmoothingMode = SmoothingMode.None;
            for (int k = 0; k < series.Length; k++)
            {
                for (int i = 0; i < f.N; i++) next[i] = cum[i] + series[k][i];
                using (var b = new SolidBrush(Palette[k])) FillBetween(g, r, f, cum, next, max, b);
                var t = cum; cum = next; next = t;
            }
            // unattributed (other processes + kernel) up to the total
            for (int i = 0; i < f.N; i++) next[i] = Math.Max(cum[i], total[i]);
            using (var b = new SolidBrush(OtherColor)) FillBetween(g, r, f, cum, next, max, b);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawLine(g, r, f, total, max, Color.FromArgb(0x55, 0x5B, 0x63), 1f);
        }

        void FillBetween(Graphics g, Rectangle r, Frame f, float[] lo, float[] hi, float max, Brush b)
        {
            foreach (var (a, e) in Segments(f))
            {
                int n = e - a;
                var pts = new PointF[n * 2 + (n == 1 ? 2 : 0)];
                int j = 0;
                if (n == 1)
                {
                    pts = new[] { new PointF(f.X[a] - 1, Y(r, hi[a], max)), new PointF(f.X[a] + 1, Y(r, hi[a], max)), new PointF(f.X[a] + 1, Y(r, lo[a], max)), new PointF(f.X[a] - 1, Y(r, lo[a], max)) };
                }
                else
                {
                    for (int i = a; i < e; i++) pts[j++] = new PointF(f.X[i], Y(r, hi[i], max));
                    for (int i = e - 1; i >= a; i--) pts[j++] = new PointF(f.X[i], Y(r, lo[i], max));
                }
                g.FillPolygon(b, pts);
            }
        }

        void DrawArea(Graphics g, Rectangle r, Frame f, float[] v, float max, Color c)
        {
            if (f.N == 0) return;
            var zero = new float[f.N];
            using (var b = new SolidBrush(Color.FromArgb(90, c))) FillBetween(g, r, f, zero, v, max, b);
            DrawLine(g, r, f, v, max, c, 1.2f);
        }

        void DrawLine(Graphics g, Rectangle r, Frame f, float[] v, float max, Color c, float width)
        {
            using var pen = new Pen(c, width) { LineJoin = LineJoin.Round };
            foreach (var (a, e) in Segments(f))
            {
                var pts = new List<PointF>(e - a);
                for (int i = a; i < e; i++)
                {
                    if (float.IsNaN(v[i])) { if (pts.Count > 1) g.DrawLines(pen, pts.ToArray()); pts.Clear(); continue; }
                    pts.Add(new PointF(f.X[i], Y(r, v[i], max)));
                }
                if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
                else if (pts.Count == 1) g.DrawLine(pen, pts[0].X - 1, pts[0].Y, pts[0].X + 1, pts[0].Y);
            }
        }

        void DrawSwimlanes(Graphics g, Frame f)
        {
            rows.Clear();
            using var bold = new Font(Font, FontStyle.Bold);
            // divider between system graphs and processes: a line with a grip, draggable up/down
            using (var dp = new Pen(Color.FromArgb(0xC8, 0xCC, 0xD2))) g.DrawLine(dp, 0, divider.Top + 2, Width, divider.Top + 2);
            using (var gb = new SolidBrush(Color.FromArgb(0x9A, 0xA0, 0xA8)))
                for (int k = -2; k <= 2; k++) g.FillEllipse(gb, plot.Left + plot.Width / 2 + k * 7 - 1.5f, divider.Top + 0.5f, 3, 3);
            DrawText(g, "Processes", bold, new Point(8, swim.Top - bold.Height - 3), Ink);
            DrawText(g, "one lane per process, busiest first · darker/taller = more CPU or GPU · click a lane to highlight it · drag this line to resize",
                Font, new Point(plot.Left + 4, swim.Top - Font.Height - 3), Muted);
            if (f.RowNames == null || f.RowNames.Length == 0)
            {
                DrawText(g, "No process activity in this window", Font, swim, Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            int n = Math.Min(f.RowNames.Length, swim.Height / rowH);
            // intensity relative to "two cores fully busy" so a single-thread spin is clearly visible
            float full = Math.Max(5f, 200f / Math.Max(1, trace.CpuCount));
            bool blocks = f.N <= plot.Width / 2;
            for (int k = 0; k < n; k++)
            {
                var r = new Rectangle(swim.Left, swim.Top + k * rowH, swim.Width, rowH - 1);
                string name = f.RowNames[k];
                rows.Add((name, r));
                bool hi = name == HighlightName;
                using (var bg = new SolidBrush(hi ? Color.FromArgb(0xFC, 0xEE, 0xF3) : (k % 2 == 0 ? LaneBg : Color.White)))
                    g.FillRectangle(bg, 0, r.Top, Width, r.Height);

                string baseName = name.Split(' ')[0];
                int ci = f.CpuNames == null ? -1 : Array.IndexOf(f.CpuNames, baseName);
                Color c = ci >= 0 ? Palette[ci] : Palette[(baseName.GetHashCode() & 0x7fffffff) % Palette.Length];
                var v = f.RowS[k];
                for (int i = 0; i < f.N; i++)
                {
                    if (v[i] <= 0.05f) continue;
                    float x0 = f.X[i] - 0.75f, x1 = f.X[i] + 0.75f;
                    if (blocks)
                    {
                        if (i > 0 && !f.Brk[i]) x0 = (f.X[i - 1] + f.X[i]) / 2;
                        if (i + 1 < f.N && !f.Brk[i + 1]) x1 = (f.X[i] + f.X[i + 1]) / 2;
                    }
                    float rel = Math.Min(1f, v[i] / full);
                    int alpha = (int)(60 + 195 * Math.Sqrt(rel));
                    float h = Math.Max(3f, (r.Height - 2) * (0.3f + 0.7f * rel));
                    using var b = new SolidBrush(Color.FromArgb(alpha, c));
                    g.FillRectangle(b, x0, r.Bottom - 1 - h, Math.Max(1f, x1 - x0), h);
                }
                var lr = new Rectangle(8, r.Top + 2, plot.Left - 12, r.Height);
                DrawText(g, Short(name), hi ? bold : Font, lr, hi ? HiColor : Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                DrawText(g, $"{f.RowAvg[k]:0.#}%", Font, new Point(r.Right + 4, r.Top + 2), Muted);
            }
            if (f.RowNames.Length > n)
                DrawText(g, $"+{f.RowNames.Length - n} more — zoom in or see the table", Font, new Point(8, swim.Top + n * rowH + 2), Muted);
        }

        void DrawOverview(Graphics g, float[] ov)
        {
            using (var bg = new SolidBrush(LaneBg)) g.FillRectangle(bg, overview);
            using (var b = new SolidBrush(Color.FromArgb(0xA8, 0xB8, 0xCC)))
                for (int i = 0; i < ov.Length; i++)
                    if (ov[i] > 0) { float h = ov[i] / 100f * (overview.Height - 2); g.FillRectangle(b, overview.Left + i, overview.Bottom - h, 1, h); }
            float x0 = Math.Max(overview.Left, OvX(ViewStart)), x1 = Math.Min(overview.Right - 1, OvX(ViewEnd));
            using (var v = new SolidBrush(Color.FromArgb(45, 0x4E, 0x79, 0xA7))) g.FillRectangle(v, x0, overview.Top, Math.Max(3, x1 - x0), overview.Height);
            using (var p = new Pen(Color.FromArgb(0x4E, 0x79, 0xA7), 2)) g.DrawRectangle(p, x0, overview.Top + 1, Math.Max(3, x1 - x0), overview.Height - 2);

            // labels: what this is, and the time span it covers
            using var bold = new Font(Font, FontStyle.Bold);
            DrawText(g, "Whole recording", bold, new Point(8, overview.Top), Ink);
            DrawText(g, "drag the box to scroll", Font, new Point(8, overview.Top + bold.Height), Muted);
            if (trace.Samples.Count > 0)
            {
                string fmt = ovEnd - ovStart > TimeSpan.TicksPerDay ? "ddd HH:mm" : "HH:mm:ss";
                DrawText(g, new DateTime(ovStart, DateTimeKind.Utc).ToLocalTime().ToString(fmt), Font, new Point(overview.Left + 3, overview.Top + 2), Muted);
                DrawText(g, new DateTime(ovEnd, DateTimeKind.Utc).ToLocalTime().ToString(fmt), Font, new Point(overview.Right + 4, overview.Top + 2), Muted);
            }
        }

        float OvX(long t) => overview.Left + (float)((t - ovStart) / (double)Math.Max(1, ovEnd - ovStart) * overview.Width);
        long OvTime(int x) => ovStart + (long)((x - overview.Left) / (double)overview.Width * (ovEnd - ovStart));

        protected override void OnPaint(PaintEventArgs e)
        {
            if (dirty || cache == null || cache.Width != Width || cache.Height != Height)
            {
                try { RenderCache(); }
                catch (Exception ex)
                {
                    // never let a drawing bug turn the control into WinForms' permanent red X
                    e.Graphics.Clear(BackColor);
                    e.Graphics.DrawString("Chart error: " + ex.Message, Font, Brushes.DarkRed, 10, 10);
                    dirty = true;
                    return;
                }
            }
            // 1:1 pixel copy; DrawImageUnscaled would rescale by bitmap-vs-screen DPI on scaled displays and smear text
            var gs = e.Graphics.Save();
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.CompositingMode = CompositingMode.SourceCopy;
            e.Graphics.DrawImage(cache, new Rectangle(0, 0, cache.Width, cache.Height), 0, 0, cache.Width, cache.Height, GraphicsUnit.Pixel);
            e.Graphics.Restore(gs);
            if (trace == null) return;
            var g = e.Graphics;

            if (HasSelection)
            {
                float a = Math.Max(plot.Left, X(SelStart)), b = Math.Min(plot.Right, X(SelEnd));
                if (b > a)
                {
                    using var sb = new SolidBrush(Color.FromArgb(50, 0x4E, 0x79, 0xA7));
                    g.FillRectangle(sb, a, plot.Top, Math.Max(1.5f, b - a), plot.Height);
                    using var sp = new Pen(Color.FromArgb(140, 0x4E, 0x79, 0xA7));
                    g.DrawLine(sp, a, plot.Top, a, plot.Bottom); g.DrawLine(sp, b, plot.Top, b, plot.Bottom);
                }
            }

            if (trace.IsLive && !follow)
            {
                const string msg = "⏸ Paused on history: press ⏵ Live, or drag the box to the right end, to follow again";
                var sz = g.MeasureString(msg, Font);
                var box = new RectangleF(plot.Right - sz.Width - 10, 1, sz.Width + 8, sz.Height + 2);
                using (var bb = new SolidBrush(Color.FromArgb(0xFF, 0xF4, 0xCE))) g.FillRectangle(bb, box);
                using (var tb = new SolidBrush(Color.FromArgb(0x7A, 0x5B, 0x00))) g.DrawString(msg, Font, tb, box.X + 4, box.Y + 1);
            }

            if (plot.Contains(mouse) && drag != Drag.Pan)
            {
                using var cp = new Pen(Color.FromArgb(120, Ink)) { DashStyle = DashStyle.Dot };
                g.DrawLine(cp, mouse.X, plot.Top, mouse.X, plot.Bottom);
                if (drag == Drag.None) DrawTooltip(g);
            }
        }

        void DrawTooltip(Graphics g)
        {
            var lines = new List<(string text, bool bold)>();
            lock (trace.Sync)
            {
                long t = TimeAt(mouse.X);
                int i = trace.Nearest(t);
                if (i < 0) return;
                var s = trace.Samples[i];
                long tol = Math.Max((long)((ViewEnd - ViewStart) / (double)plot.Width * 6), (long)(s.IntervalSec * 1.5 * TimeSpan.TicksPerSecond));
                if (Math.Abs(s.Ticks - t) > tol) return;
                lines.Add((new DateTime(s.Ticks, DateTimeKind.Utc).ToLocalTime().ToString("ddd d MMM  HH:mm:ss"), true));
                foreach (var (name, rr) in rows)
                {
                    if (!rr.Contains(mouse)) continue;
                    float c = 0, gp = 0, io = 0, mem = 0; string user = "";
                    foreach (var p in s.Procs)
                        if (trace.Procs.TryGetValue(p.Key, out var pi) && pi.Display == name)
                        { c += p.Cpu; gp += p.Gpu; io += p.ReadBps + p.WriteBps; mem += p.MemMB; user = pi.User; }
                    lines.Add(($"> {name}  ({user})", true));
                    lines.Add(($"   CPU {c:0.0}%   GPU {gp:0}%   IO {Analysis.Bytes(io)}/s   Mem {mem:0} MB", false));
                }
                lines.Add(($"CPU {s.Cpu:0}%   GPU {s.Gpu:0}%   Memory {s.MemPct:0}%", false));
                lines.Add(($"Disk read {Analysis.Bytes(s.DiskReadBps)}/s, write {Analysis.Bytes(s.DiskWriteBps)}/s  (busy {s.DiskActivePct:0}%)", false));
                string extra = float.IsNaN(s.CpuClockPct) ? "" : $"CPU clock {s.CpuClockPct:0}%";
                if (!float.IsNaN(s.TempC)) extra += $"   Temp {s.TempC:0}°C";
                extra += $"   Net {Analysis.Bytes(s.NetBps)}/s";
                lines.Add((extra.Trim(), false));
                if (s.Sensors != null)
                {
                    var fans = new List<string>(); var temps = new List<string>();
                    for (int k = 0; k < s.Sensors.Length && k < trace.Sensors.Count; k++)
                    {
                        float v = s.Sensors[k];
                        if (float.IsNaN(v)) continue;
                        if (trace.Sensors[k].Kind == SensorKind.Fan) { if (v > 0) fans.Add($"{trace.Sensors[k].Name} {v:0}"); }
                        else temps.Add($"{trace.Sensors[k].Name} {v:0}°");
                    }
                    if (fans.Count > 0) lines.Add(("Fans (rpm): " + string.Join(", ", fans), false));
                    if (temps.Count > 0) lines.Add(("Temps: " + string.Join(", ", temps), false));
                }
                var top = s.Procs.Where(p => p.Cpu >= 0.5f || p.Gpu >= 1f || p.ReadBps + p.WriteBps > 1 << 20)
                                 .OrderByDescending(p => p.Cpu + p.Gpu * 0.5f).Take(6).ToList();
                if (top.Count > 0) lines.Add(("Top processes", true));
                foreach (var p in top)
                {
                    if (!trace.Procs.TryGetValue(p.Key, out var info)) continue;
                    string txt = $"{p.Cpu,5:0.0}%  {info.Display}  ({info.User})";
                    if (p.Gpu >= 1) txt += $"  GPU {p.Gpu:0}%";
                    if (p.ReadBps + p.WriteBps > 1 << 20) txt += $"  IO {Analysis.Bytes(p.ReadBps + p.WriteBps)}/s";
                    lines.Add((txt.Length > 110 ? txt.Substring(0, 110) + "…" : txt, false));
                }
            }

            using var bold = new Font(Font, FontStyle.Bold);
            int w = 0, h = 0, lh = Font.Height + 1;
            foreach (var (text, b) in lines) { w = Math.Max(w, TextRenderer.MeasureText(text, b ? bold : Font).Width); h += lh; }
            var box = new Rectangle(mouse.X + 14, mouse.Y + 14, w + 16, h + 10);
            if (box.Right > Width - 2) box.X = mouse.X - 14 - box.Width;
            if (box.Bottom > Height - 2) box.Y = Math.Max(2, Height - 2 - box.Height);
            if (box.X < 2) box.X = 2;
            using (var sh = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) g.FillRectangle(sh, box.X + 2, box.Y + 2, box.Width, box.Height);
            using (var bg = new SolidBrush(Color.FromArgb(250, 255, 255, 255))) g.FillRectangle(bg, box);
            using (var bp = new Pen(Color.FromArgb(0xC8, 0xCC, 0xD2))) g.DrawRectangle(bp, box);
            int y = box.Y + 5;
            foreach (var (text, b) in lines)
            {
                DrawText(g, text, b ? bold : Font, new Point(box.X + 8, y), Ink, TextFormatFlags.NoPrefix);
                y += lh;
            }
        }

        // ------------------------------------------------------------------ input

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (trace == null) return;
            if (divider.Contains(e.Location) && e.Button == MouseButtons.Left) { drag = Drag.Divider; return; }
            if (overview.Contains(e.Location))
            {
                // grab the box where it was clicked; clicking outside the box jumps it there first
                long t = OvTime(e.X), span = ViewEnd - ViewStart;
                Follow = false;
                if (t >= ViewStart && t <= ViewEnd) grabOffset = t - ViewStart;
                else { grabOffset = span / 2; SetView(t - grabOffset, t - grabOffset + span); }
                drag = Drag.Overview;
                return;
            }
            if (!plot.Contains(e.Location)) return;
            if (e.Button == MouseButtons.Left) { drag = Drag.Select; dragX = e.X; dragT = TimeAt(e.X); }
            else if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle)
            {
                drag = Drag.Pan; dragX = e.X; dragViewStart = ViewStart;
                Follow = false;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            mouse = e.Location;
            Cursor = drag == Drag.Divider || (drag == Drag.None && divider.Contains(e.Location)) ? Cursors.HSplit : Cursors.Default;
            switch (drag)
            {
                case Drag.Divider:
                    SwimFraction = Math.Max(0.08f, Math.Min(0.85f, (plot.Bottom - e.Y - Font.Height / 2f) / (float)plot.Height));
                    dirty = true; Invalidate();
                    return;
                case Drag.Select:
                    if (Math.Abs(e.X - dragX) > 3)
                    {
                        Follow = false; // freeze the view while selecting
                        long t =TimeAt(Math.Max(plot.Left, Math.Min(plot.Right, e.X)));
                        SelStart = Math.Min(dragT, t); SelEnd = Math.Max(dragT, t);
                    }
                    break;
                case Drag.Pan:
                    long delta = (long)((e.X - dragX) / (double)plot.Width * (ViewEnd - ViewStart));
                    long span = ViewEnd - ViewStart;
                    SetView(dragViewStart - delta, dragViewStart - delta + span);
                    return;
                case Drag.Overview:
                    long t0 = OvTime(Math.Max(overview.Left, Math.Min(overview.Right, e.X))) - grabOffset;
                    SetView(t0, t0 + (ViewEnd - ViewStart));
                    return;
            }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (drag == Drag.Divider) { drag = Drag.None; SwimFractionChanged?.Invoke(this, EventArgs.Empty); return; }
            if (drag == Drag.Select)
            {
                if (Math.Abs(e.X - dragX) <= 3)
                {
                    // click on a process lane: toggle highlight of that process
                    foreach (var (name, rr) in rows)
                        if (rr.Contains(e.Location))
                        {
                            ProcessClicked?.Invoke(name == HighlightName ? "" : name);
                            drag = Drag.None; Invalidate();
                            return;
                        }
                    // click on a graph: select the single sample under the cursor
                    long a = 0, b = 0;
                    lock (trace.Sync)
                    {
                        int i = trace.Nearest(TimeAt(e.X));
                        if (i >= 0) { a = trace.Samples[i].StartTicks; b = trace.Samples[i].Ticks; }
                    }
                    if (b > a) Select(a, b);
                }
                else SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
            else if ((drag == Drag.Pan || drag == Drag.Overview) && trace.IsLive && trace.GetExtent(out _, out long end)
                     && ViewEnd >= end - (ViewEnd - ViewStart) / 50)
            {
                // scrolled back to the newest data: resume live follow with the current zoom
                WindowTicks = ViewEnd - ViewStart;
                drag = Drag.None;
                Follow = true;
                return;
            }
            drag = Drag.None;
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (plot.Contains(e.Location)) ClearSelection();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            mouse = new Point(-1, -1);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (trace == null) return;
            double factor = Math.Pow(1.25, -e.Delta / 120.0);
            long span = ViewEnd - ViewStart;
            long newSpan = (long)Math.Max(TimeSpan.FromSeconds(20).Ticks, Math.Min(TimeSpan.FromDays(60).Ticks, span * factor));
            if (follow)
            {
                WindowTicks = newSpan;
                DataChanged();
                ViewChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            long anchor = plot.Contains(e.Location) ? TimeAt(e.X) : (ViewStart + ViewEnd) / 2;
            double frac = (anchor - ViewStart) / (double)span;
            WindowTicks = newSpan;
            SetView(anchor - (long)(frac * newSpan), anchor - (long)(frac * newSpan) + newSpan);
        }

        void CenterOn(long t)
        {
            long span = ViewEnd - ViewStart;
            Follow = false;
            SetView(t - span / 2, t + span / 2);
        }

        protected override void OnResize(EventArgs e)
        {
            dirty = true;
            base.OnResize(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) cache?.Dispose();
            base.Dispose(disposing);
        }
    }
}

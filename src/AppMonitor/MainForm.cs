using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AppMonitor
{
    sealed class MainForm : Form
    {
        readonly Collector collector = new Collector();
        Trace liveTrace;
        readonly bool startInTray;
        bool exiting, trayHintShown;

        // toolbar
        readonly ToolStrip tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 2, 6, 2), ImageScalingSize = new Size(1, 1) };
        readonly ToolStripButton btnRecord = new ToolStripButton("● Record") { ToolTipText = "Start/stop recording (auto-saved to the traces folder)" };
        readonly ToolStripButton btnLive = new ToolStripButton("⏵ Live") { ToolTipText = "Jump to the newest data and keep following it (clears the selection)" };
        readonly ToolStripComboBox cmbWindow = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 90 };
        readonly ToolStripComboBox cmbInterval = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 60 };
        readonly ToolStripButton btnGpu = new ToolStripButton("GPU") { CheckOnClick = true, Checked = true, ToolTipText = "Collect per-process GPU usage (slightly more expensive)" };

        // panes
        readonly TimelineControl timeline = new TimelineControl { Dock = DockStyle.Fill };
        readonly DbListView episodesList = new DbListView();
        readonly NumericUpDown numThreshold = new NumericUpDown { Minimum = 5, Maximum = 100, Value = 40, Width = 48 };
        readonly NumericUpDown numMinSec = new NumericUpDown { Minimum = 2, Maximum = 3600, Value = 10, Width = 52 };
        readonly DbListView table = new DbListView();
        readonly Label lblRange = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        readonly Button btnClearSel = new Button { Text = "✕ Clear selection", AutoSize = true, Visible = false, Margin = new Padding(0, 2, 0, 0), Anchor = AnchorStyles.Left };
        readonly CheckBox chkGroup = new CheckBox { Text = "Group by name", Checked = true, AutoSize = true, Margin = new Padding(8, 5, 4, 0) };
        readonly TextBox txtFilter = new TextBox { Width = 160, Margin = new Padding(4, 3, 4, 0) };

        // status
        readonly ToolStripStatusLabel lblState = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        readonly ToolStripStatusLabel lblOverhead = new ToolStripStatusLabel();
        readonly ToolStripStatusLabel lblSensors = new ToolStripStatusLabel { ForeColor = Color.DimGray };

        readonly NotifyIcon tray = new NotifyIcon();
        readonly Timer uiTimer = new Timer { Interval = 1000 };
        readonly Timer debounce = new Timer { Interval = 250 };
        int lastVersion = -1, tick, lastTableTick, lastEpisodeTick;
        string episodesSig = "";
        bool refreshingEpisodes;
        List<ProcStat> tableData = new List<ProcStat>();
        int sortCol = 3; bool sortDesc = true;

        static readonly (string label, long ticks)[] Windows =
        {
            ("1 min", TimeSpan.TicksPerMinute), ("5 min", 5 * TimeSpan.TicksPerMinute), ("15 min", 15 * TimeSpan.TicksPerMinute),
            ("1 hour", TimeSpan.TicksPerHour), ("6 hours", 6 * TimeSpan.TicksPerHour), ("24 hours", TimeSpan.TicksPerDay), ("All", 0),
        };
        static readonly int[] Intervals = { 1, 2, 5, 10 };

        public MainForm(string[] args)
        {
            Text = "AppMonitor";
            Font = SystemFonts.MessageBoxFont;
            Size = new Size(1360, 860);
            MinimumSize = new Size(900, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = MakeIcon(false);
            startInTray = args.Contains("--tray");

            BuildToolbar();
            BuildLayout();
            BuildTray();

            collector.Error += ex => BeginInvoke((Action)(() => { SetRecordingUi(); MessageBox.Show(this, "Recording stopped: " + ex.Message, "AppMonitor", MessageBoxButtons.OK, MessageBoxIcon.Error); }));
            timeline.SelectionChanged += (s, e) => { debounce.Stop(); debounce.Start(); };
            timeline.ViewChanged += (s, e) => { if (!timeline.HasSelection) { debounce.Stop(); debounce.Start(); } };
            timeline.FollowChanged += (s, e) => btnLive.Checked = timeline.Follow;
            timeline.ProcessClicked += OnLaneClicked;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && timeline.HasSelection) { timeline.ClearSelection(); e.Handled = true; } };
            debounce.Tick += (s, e) => { debounce.Stop(); RefreshTable(); };
            uiTimer.Tick += (s, e) => OnTick();
            uiTimer.Start();

            string file = args.FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a));
            if (file != null) Shown += (s, e) => OpenTrace(file);
            if (args.Contains("--record")) StartRecording();
            SetRecordingUi();
        }

        // ------------------------------------------------------------------ UI construction

        void BuildToolbar()
        {
            btnRecord.Font = new Font(Font, FontStyle.Bold);
            btnRecord.Click += (s, e) => { if (collector.Running) StopRecording(); else StartRecording(); };
            var btnOpen = new ToolStripButton("Open…", null, (s, e) => OpenTraceDialog()) { ToolTipText = "Open a saved trace" };
            var btnSave = new ToolStripButton("Save as…", null, (s, e) => SaveTraceDialog()) { ToolTipText = "Save the trace being viewed" };
            var btnCsv = new ToolStripButton("Export CSV…", null, (s, e) => ExportCsv()) { ToolTipText = "Export the process table below" };
            // not a toggle: pressing Live always goes live (pausing happens by panning, scrolling or selecting)
            btnLive.Click += (s, e) => GoLive(true);

            foreach (var w in Windows) cmbWindow.Items.Add(w.label);
            cmbWindow.SelectedIndex = 2;
            cmbWindow.SelectedIndexChanged += (s, e) => timeline.SetWindow(Windows[cmbWindow.SelectedIndex].ticks);
            foreach (var i in Intervals) cmbInterval.Items.Add(i + " s");
            cmbInterval.SelectedIndex = 1;
            cmbInterval.SelectedIndexChanged += (s, e) => collector.IntervalMs = Intervals[cmbInterval.SelectedIndex] * 1000;
            btnGpu.CheckedChanged += (s, e) => collector.CollectGpu = btnGpu.Checked;

            var more = new ToolStripDropDownButton("More") { Alignment = ToolStripItemAlignment.Right };
            more.DropDownItems.Add("Open traces folder", null, (s, e) => Process.Start("explorer.exe", "\"" + TracesDir + "\""));
            var startup = new ToolStripMenuItem("Start with Windows (recording, in tray)") { CheckOnClick = true };
            startup.Click += (s, e) => SetStartup(startup.Checked);
            more.DropDownItems.Add(startup);
            more.DropDownOpening += (s, e) => startup.Checked = IsStartupEnabled();
            more.DropDownItems.Add("Get the PawnIO driver (motherboard fan speeds & CPU temperature)…", null, (s, e) =>
            {
                if (MessageBox.Show(this, "Motherboard fan speeds and the real CPU temperature are read from the motherboard's monitoring chip, " +
                    "which needs administrator rights and the PawnIO driver (signed, open source, used by LibreHardwareMonitor).\n\n" +
                    "Install it from https://pawnio.eu, then use \"Restart as administrator\".\n\nOpen the website now?",
                    "AppMonitor", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo("https://pawnio.eu") { UseShellExecute = true });
            });
            if (!IsElevated())
                more.DropDownItems.Add("Restart as administrator (shows users/command lines of system processes)", null, (s, e) => RestartElevated());
            more.DropDownItems.Add(new ToolStripSeparator());
            more.DropDownItems.Add("Exit", null, (s, e) => ExitApp());

            tools.Items.AddRange(new ToolStripItem[]
            {
                btnRecord, new ToolStripSeparator(), btnOpen, btnSave, btnCsv, new ToolStripSeparator(),
                btnLive, new ToolStripLabel("Show:"), cmbWindow, new ToolStripSeparator(),
                new ToolStripLabel("Sample every:"), cmbInterval, btnGpu, more,
            });
        }

        void BuildLayout()
        {
            // left: busy periods
            episodesList.Columns.Add("When", 118);
            episodesList.Columns.Add("Length", 60);
            episodesList.Columns.Add("Peak", 120);
            episodesList.Columns.Add("Main culprits", 320);
            episodesList.MultiSelect = false;
            episodesList.ShowItemToolTips = true;
            episodesList.SelectedIndexChanged += (s, e) =>
            {
                if (refreshingEpisodes) return; // list rebuilt by the timer, not clicked by the user
                if (episodesList.SelectedItems.Count == 0 || !(episodesList.SelectedItems[0].Tag is Episode ep)) return;
                timeline.ShowRange(ep.Start, ep.End);
                timeline.Select(ep.Start, ep.End);
            };
            numThreshold.ValueChanged += (s, e) => { episodesSig = ""; RefreshEpisodes(); };
            numMinSec.ValueChanged += (s, e) => { episodesSig = ""; RefreshEpisodes(); };

            var epHeader = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4, 4, 4, 2) };
            epHeader.Controls.Add(new Label { Text = "Busy periods — likely fan triggers", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
            epHeader.SetFlowBreak(epHeader.Controls[0], true);
            epHeader.Controls.Add(new Label { Text = "CPU or GPU ≥", AutoSize = true, Margin = new Padding(0, 6, 2, 0) });
            epHeader.Controls.Add(numThreshold);
            epHeader.Controls.Add(new Label { Text = "% for ≥", AutoSize = true, Margin = new Padding(2, 6, 2, 0) });
            epHeader.Controls.Add(numMinSec);
            epHeader.Controls.Add(new Label { Text = "s", AutoSize = true, Margin = new Padding(2, 6, 0, 0) });
            var left = new Panel { Dock = DockStyle.Fill };
            left.Controls.Add(episodesList);
            left.Controls.Add(epHeader);

            // bottom: process table
            string[] cols = { "Process", "PID", "User", "Avg CPU", "Peak CPU", "CPU time", "Avg GPU", "Peak GPU", "I/O read", "I/O write", "Peak mem", "Seen for", "Command line" };
            int[] widths = { 330, 70, 130, 64, 68, 70, 64, 68, 76, 76, 74, 70, 600 };
            for (int i = 0; i < cols.Length; i++)
                table.Columns.Add(cols[i], widths[i], i >= 3 && i <= 11 ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            table.ColumnClick += (s, e) => { if (sortCol == e.Column) sortDesc = !sortDesc; else { sortCol = e.Column; sortDesc = e.Column >= 3 && e.Column <= 11; } FillTable(); };
            table.SelectedIndexChanged += (s, e) =>
            {
                var st = table.SelectedItems.Count > 0 ? table.SelectedItems[0].Tag as ProcStat : null;
                timeline.HighlightKeys = st != null ? new HashSet<int>(st.Keys) : new HashSet<int>();
                timeline.HighlightName = st?.Name ?? "";
                timeline.DataChanged();
            };
            chkGroup.CheckedChanged += (s, e) => RefreshTable();
            txtFilter.TextChanged += (s, e) => FillTable();

            btnClearSel.Click += (s, e) => timeline.ClearSelection();
            var tableBar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 32, ColumnCount = 5, Padding = new Padding(4, 2, 4, 0) };
            tableBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tableBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tableBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tableBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tableBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lblRange.Dock = DockStyle.None; lblRange.AutoSize = true; lblRange.Margin = new Padding(0, 7, 6, 0);
            tableBar.Controls.Add(lblRange, 0, 0);
            tableBar.Controls.Add(btnClearSel, 1, 0);
            tableBar.Controls.Add(chkGroup, 2, 0);
            tableBar.Controls.Add(new Label { Text = "Filter:", AutoSize = true, Margin = new Padding(12, 7, 0, 0) }, 3, 0);
            tableBar.Controls.Add(txtFilter, 4, 0);
            var bottom = new Panel { Dock = DockStyle.Fill };
            bottom.Controls.Add(table);
            bottom.Controls.Add(tableBar);

            var hint = new Label
            {
                Dock = DockStyle.Bottom, Height = Font.Height + 6, ForeColor = Color.DimGray, Padding = new Padding(6, 2, 0, 0),
                Text = "Wheel: zoom · Drag: select a range · Click: one moment · Right-drag: pan · Bottom strip: jump · Esc or double-click: clear selection · Click a process below to highlight it",
            };
            var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
            right.Panel1.Controls.Add(timeline);
            right.Panel1.Controls.Add(hint);
            right.Panel2.Controls.Add(bottom);

            var main = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6, FixedPanel = FixedPanel.Panel1 };
            main.Panel1.Controls.Add(left);
            main.Panel2.Controls.Add(right);

            var status = new StatusStrip { SizingGrip = true };
            status.Items.AddRange(new ToolStripItem[] { lblState, lblSensors, lblOverhead });

            Controls.Add(main);
            Controls.Add(tools);
            Controls.Add(status);

            Load += (s, e) =>
            {
                main.SplitterDistance = 340;
                right.SplitterDistance = (int)(right.Height * 0.64);
            };
        }

        void BuildTray()
        {
            tray.Icon = MakeIcon(false);
            tray.Text = "AppMonitor";
            tray.Visible = true;
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show", null, (s, e) => ShowFromTray());
            var rec = new ToolStripMenuItem("Record");
            rec.Click += (s, e) => { if (collector.Running) StopRecording(); else StartRecording(); };
            menu.Items.Add(rec);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitApp());
            menu.Opening += (s, e) => rec.Text = collector.Running ? "Stop recording" : "Start recording";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => ShowFromTray();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (startInTray && !IsHandleCreated) { CreateHandle(); value = false; }
            base.SetVisibleCore(value);
        }

        // ------------------------------------------------------------------ recording

        void StartRecording()
        {
            if (collector.Running) return;
            var t = new Trace { IsLive = true, RetainTicks = TimeSpan.FromHours(24).Ticks };
            string path = Path.Combine(TracesDir, $"trace_{DateTime.Now:yyyyMMdd_HHmmss}{TraceFile.Extension}");
            t.FilePath = path;
            collector.IntervalMs = Intervals[Math.Max(0, cmbInterval.SelectedIndex)] * 1000;
            collector.CollectGpu = btnGpu.Checked;
            try { collector.Start(t, path); }
            catch (Exception ex) { MessageBox.Show(this, "Could not start recording: " + ex.Message, "AppMonitor"); return; }
            liveTrace = t;
            ShowTrace(t);
            timeline.SetWindow(Windows[cmbWindow.SelectedIndex].ticks);
            SetRecordingUi();
        }

        void StopRecording()
        {
            collector.Stop();
            if (liveTrace != null) liveTrace.IsLive = false;
            timeline.Follow = false;
            SetRecordingUi();
        }

        void SetRecordingUi()
        {
            bool rec = collector.Running;
            btnRecord.Text = rec ? "■ Stop" : "● Record";
            btnRecord.ForeColor = rec ? Color.FromArgb(0xC0, 0x20, 0x20) : SystemColors.ControlText;
            cmbInterval.Enabled = true;
            var old = tray.Icon; tray.Icon = MakeIcon(rec); old?.Dispose();
            tray.Text = rec ? "AppMonitor — recording" : "AppMonitor — stopped";
            UpdateStatus();
        }

        void GoLive(bool live)
        {
            if (live && liveTrace != null)
            {
                if (timeline.Trace != liveTrace) ShowTrace(liveTrace);
                timeline.ClearSelection();
                refreshingEpisodes = true; // un-highlight the busy period without jumping to it
                try { episodesList.SelectedItems.Clear(); } finally { refreshingEpisodes = false; }
                timeline.Follow = collector.Running;
                timeline.SetWindow(Windows[cmbWindow.SelectedIndex].ticks);
            }
            else timeline.Follow = false;
            btnLive.Checked = timeline.Follow;
        }

        void ShowTrace(Trace t)
        {
            timeline.Trace = t;
            lastVersion = -1; episodesSig = "";
            if (!t.IsLive) cmbWindow.SelectedIndex = Windows.Length - 1;
            RefreshEpisodes();
            RefreshTable();
            UpdateStatus();
        }

        // ------------------------------------------------------------------ files

        void OpenTraceDialog()
        {
            using var dlg = new OpenFileDialog { Filter = "AppMonitor trace (*.amtrace)|*.amtrace|All files|*.*", InitialDirectory = TracesDir };
            if (dlg.ShowDialog(this) == DialogResult.OK) OpenTrace(dlg.FileName);
        }

        async void OpenTrace(string path)
        {
            if (liveTrace != null && string.Equals(path, liveTrace.FilePath, StringComparison.OrdinalIgnoreCase) && collector.Running)
            { GoLive(true); return; }
            UseWaitCursor = true;
            try
            {
                var t = await Task.Run(() => TraceFile.Load(path));
                ShowTrace(t);
            }
            catch (Exception ex) { MessageBox.Show(this, "Could not open trace: " + ex.Message, "AppMonitor", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { UseWaitCursor = false; }
        }

        async void SaveTraceDialog()
        {
            var t = timeline.Trace;
            if (t == null) { MessageBox.Show(this, "Nothing to save yet.", "AppMonitor"); return; }
            using var dlg = new SaveFileDialog
            {
                Filter = "AppMonitor trace (*.amtrace)|*.amtrace", InitialDirectory = TracesDir,
                FileName = t.FilePath != null ? Path.GetFileNameWithoutExtension(t.FilePath) + "_copy" : "trace",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            UseWaitCursor = true;
            try { await Task.Run(() => TraceFile.Save(t, dlg.FileName)); }
            catch (Exception ex) { MessageBox.Show(this, "Could not save: " + ex.Message, "AppMonitor", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { UseWaitCursor = false; }
        }

        void ExportCsv()
        {
            if (tableData.Count == 0) { MessageBox.Show(this, "The table is empty.", "AppMonitor"); return; }
            using var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "processes.csv" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var sb = new StringBuilder();
            sb.AppendLine("Process,PIDs,User,AvgCpuPct,PeakCpuPct,CpuSeconds,AvgGpuPct,PeakGpuPct,IoReadBytes,IoWriteBytes,PeakMemMB,SeenSeconds,CommandLine");
            foreach (var s in Filtered())
                sb.AppendLine(string.Join(",", Csv(s.Name), Csv(s.Pids), Csv(s.User), F(s.AvgCpu), F(s.PeakCpu), F(s.CpuSec), F(s.AvgGpu), F(s.PeakGpu),
                    F(s.ReadBytes), F(s.WriteBytes), F(s.PeakMem), F(s.ActiveSec), Csv(s.CommandLine)));
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));

            static string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            static string Csv(string v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
        }

        static string tracesDir;
        static string TracesDir
        {
            get
            {
                if (tracesDir != null) return tracesDir;
                // portable: next to the exe if writable, else in LocalAppData
                foreach (var dir in new[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "traces"),
                    Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "AppMonitor", "traces"),
                })
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        string probe = Path.Combine(dir, ".probe");
                        File.WriteAllText(probe, ""); File.Delete(probe);
                        return tracesDir = dir;
                    }
                    catch { }
                }
                return tracesDir = Path.GetTempPath();
            }
        }

        // ------------------------------------------------------------------ periodic refresh

        void OnTick()
        {
            tick++;
            UpdateStatus();
            if (!Visible || WindowState == FormWindowState.Minimized) return;
            var t = timeline.Trace;
            if (t == null || t != liveTrace || t.Version == lastVersion) return;
            bool firstData = lastVersion <= 0;
            lastVersion = t.Version;
            timeline.DataChanged(); // also while paused on history: the overview and any visible recent data keep updating
            // refresh as soon as data first arrives, then at most every ~3 s / ~5 s
            if (!timeline.HasSelection && timeline.Follow && (firstData || tick - lastTableTick >= 3)) { lastTableTick = tick; RefreshTable(); }
            if (firstData || tick - lastEpisodeTick >= 5) { lastEpisodeTick = tick; RefreshEpisodes(); }
        }

        void UpdateStatus()
        {
            var t = timeline.Trace;
            string state;
            if (collector.Running && t == liveTrace) state = $"● Recording → {collector.FilePath}";
            else if (collector.Running) state = $"● Recording in background  |  Viewing {t?.FilePath}";
            else if (t != null) state = $"Viewing {t.FilePath}" + (t.Machine != null ? $"  ({t.Machine})" : "");
            else state = "Stopped";
            if (t != null)
            {
                int n; lock (t.Sync) n = t.Samples.Count;
                state += $"  |  {n:N0} samples";
            }
            lblState.Text = state;
            lblSensors.Text = collector.Running && collector.SensorStatus.Length > 0 ? "Sensors: " + collector.SensorStatus + "  |" : "";
            lblOverhead.Text = collector.Running
                ? $"AppMonitor's own usage: CPU {collector.SelfCpu:0.0}%  RAM {collector.SelfMemMB:0} MB  (sample took {collector.LastSampleMs:0} ms)"
                : "Not recording — using no resources";
        }

        void RefreshEpisodes()
        {
            var t = timeline.Trace;
            if (t == null) { episodesList.Items.Clear(); return; }
            var eps = Analysis.FindEpisodes(t, (float)numThreshold.Value, (double)numMinSec.Value);
            string sig = eps.Count + ":" + (eps.Count > 0 ? eps[0].Start + "-" + eps[0].End + eps[0].Culprits : "");
            if (sig == episodesSig) return;
            episodesSig = sig;
            long? selected = episodesList.SelectedItems.Count > 0 ? (episodesList.SelectedItems[0].Tag as Episode)?.Start : null;
            refreshingEpisodes = true;
            try { FillEpisodes(eps, selected); } finally { refreshingEpisodes = false; }
        }

        void FillEpisodes(List<Episode> eps, long? selected)
        {
            episodesList.BeginUpdate();
            episodesList.Items.Clear();
            foreach (var ep in eps.Take(1000))
            {
                var local = new DateTime(ep.Start, DateTimeKind.Utc).ToLocalTime();
                string when = local.Date == DateTime.Today ? local.ToString("HH:mm:ss") : local.ToString("ddd d MMM HH:mm");
                string peak = ep.PeakGpu > ep.PeakCpu ? $"GPU {ep.PeakGpu:0}%" : $"CPU {ep.PeakCpu:0}%";
                if (ep.PeakFan > 0) peak += $" · {ep.PeakFan:0} rpm";
                var it = new ListViewItem(new[] { when, Analysis.Duration(ep.DurationSec), peak, ep.Culprits }) { Tag = ep };
                it.ToolTipText = $"avg CPU {ep.AvgCpu:0}% · avg GPU {ep.AvgGpu:0}%" + (float.IsNaN(ep.PeakTemp) ? "" : $" · peak temp {ep.PeakTemp:0}°C")
                    + (ep.PeakFan > 0 ? $" · fastest fan {ep.PeakFan:0} rpm ({ep.PeakFanName})" : "") + "\n" + ep.Culprits;
                episodesList.Items.Add(it);
                if (selected == ep.Start) it.Selected = true;
            }
            if (eps.Count == 0)
                episodesList.Items.Add(new ListViewItem(new[] { "—", "", "", "No busy periods yet" }));
            episodesList.EndUpdate();
        }

        void RefreshTable()
        {
            var t = timeline.Trace;
            if (t == null) { tableData.Clear(); FillTable(); lblRange.Text = ""; return; }
            timeline.GetRange(out long a, out long b);
            tableData = Analysis.Compute(t, a, b, chkGroup.Checked, out double sec);
            var la = new DateTime(a, DateTimeKind.Utc).ToLocalTime();
            var lb = new DateTime(b, DateTimeKind.Utc).ToLocalTime();
            string fmt = la.Date == lb.Date ? "HH:mm:ss" : "ddd d MMM HH:mm:ss";
            lblRange.Text = (timeline.HasSelection ? "Selection" : "Visible window") +
                $":  {la.ToString(la.Date == DateTime.Today ? "HH:mm:ss" : "ddd d MMM HH:mm:ss")} → {lb.ToString(fmt)}  ({Analysis.Duration(sec)} of data)";
            btnClearSel.Visible = timeline.HasSelection;
            FillTable();
        }

        /// <summary>Swimlane clicked: select that process in the table (which drives the highlight), or highlight it directly.</summary>
        void OnLaneClicked(string name)
        {
            table.SelectedItems.Clear();
            if (name.Length == 0) return; // clearing the table selection already cleared the highlight
            foreach (ListViewItem it in table.Items)
                if (((ProcStat)it.Tag).Name == name) { it.Selected = true; it.EnsureVisible(); return; }
            var t = timeline.Trace;
            lock (t.Sync) timeline.HighlightKeys = new HashSet<int>(t.Procs.Values.Where(p => p.Display == name).Select(p => p.Key));
            timeline.HighlightName = name;
            timeline.DataChanged();
        }

        IEnumerable<ProcStat> Filtered()
        {
            string f = txtFilter.Text.Trim();
            IEnumerable<ProcStat> q = tableData;
            if (f.Length > 0)
                q = q.Where(s => s.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 || s.User.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                              || s.CommandLine.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 || s.Pids.Contains(f));
            Func<ProcStat, object> key = sortCol switch
            {
                0 => s => s.Name, 1 => s => s.Pids, 2 => s => s.User, 3 => s => s.AvgCpu, 4 => s => s.PeakCpu, 5 => s => s.CpuSec,
                6 => s => s.AvgGpu, 7 => s => s.PeakGpu, 8 => s => s.ReadBytes, 9 => s => s.WriteBytes, 10 => s => s.PeakMem,
                11 => s => s.ActiveSec, _ => s => s.CommandLine,
            };
            return sortDesc ? q.OrderByDescending(key) : q.OrderBy(key);
        }

        void FillTable()
        {
            string selectedName = table.SelectedItems.Count > 0 ? (table.SelectedItems[0].Tag as ProcStat)?.Name : null;
            int top = table.TopItem?.Index ?? 0;
            table.BeginUpdate();
            table.Items.Clear();
            var items = new List<ListViewItem>();
            foreach (var s in Filtered().Take(500))
            {
                var it = new ListViewItem(new[]
                {
                    s.Name, s.Pids, s.User, $"{s.AvgCpu:0.0}%", $"{s.PeakCpu:0.0}%", Analysis.Duration(s.CpuSec),
                    s.PeakGpu > 0 ? $"{s.AvgGpu:0.0}%" : "", s.PeakGpu > 0 ? $"{s.PeakGpu:0}%" : "",
                    Analysis.Bytes(s.ReadBytes), Analysis.Bytes(s.WriteBytes), $"{s.PeakMem:0} MB", Analysis.Duration(s.ActiveSec),
                    string.IsNullOrEmpty(s.CommandLine) ? s.Path : s.CommandLine,
                }) { Tag = s };
                items.Add(it);
            }
            table.Items.AddRange(items.ToArray());
            if (selectedName != null)
                foreach (ListViewItem it in table.Items)
                    if (((ProcStat)it.Tag).Name == selectedName) { it.Selected = true; break; }
            if (table.Items.Count > 0) table.TopItem = table.Items[Math.Min(top, table.Items.Count - 1)];
            table.EndUpdate();
        }

        // ------------------------------------------------------------------ window / tray behaviour

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && collector.Running) HideToTray();
        }

        void HideToTray()
        {
            Hide();
            if (!trayHintShown) { trayHintShown = true; tray.ShowBalloonTip(3000, "AppMonitor", "Still recording in the background. Double-click the tray icon to open.", ToolTipIcon.Info); }
            Native.EmptyWorkingSet(Native.GetCurrentProcess()); // give back UI memory while hidden
        }

        void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            timeline.DataChanged();
            RefreshEpisodes(); RefreshTable();
        }

        public void ActivateFromOtherInstance() => BeginInvoke((Action)ShowFromTray);

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && collector.Running && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            collector.Stop();
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        void ExitApp()
        {
            exiting = true;
            Close();
        }

        static bool IsElevated()
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        void RestartElevated()
        {
            bool wasRecording = collector.Running;
            var psi = new ProcessStartInfo(Application.ExecutablePath, wasRecording ? "--record" : "") { UseShellExecute = true, Verb = "runas" };
            collector.Stop();
            Program.ReleaseSingleInstance();
            try
            {
                Process.Start(psi);
                ExitApp();
            }
            catch (Win32Exception)
            {
                Program.AcquireSingleInstance();
                if (wasRecording) StartRecording();
            }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static bool IsStartupEnabled()
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("AppMonitor") is string v && v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static void SetStartup(bool on)
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue("AppMonitor", $"\"{Application.ExecutablePath}\" --record --tray");
            else k.DeleteValue("AppMonitor", false);
        }

        static Icon MakeIcon(bool recording)
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var bg = new SolidBrush(Color.FromArgb(0x2B, 0x3A, 0x4E));
                g.FillRectangle(bg, 1, 1, 30, 30);
                int[] h = { 10, 18, 13, 24, 16 };
                using var bar = new SolidBrush(Color.FromArgb(0x76, 0xB7, 0xB2));
                for (int i = 0; i < h.Length; i++) g.FillRectangle(bar, 4 + i * 5, 28 - h[i], 4, h[i]);
                if (recording)
                {
                    using var red = new SolidBrush(Color.FromArgb(0xE1, 0x2B, 0x2B));
                    g.FillEllipse(red, 18, 1, 13, 13);
                }
            }
            IntPtr hIcon = bmp.GetHicon();
            var icon = (Icon)Icon.FromHandle(hIcon).Clone();
            Native.DestroyIcon(hIcon);
            return icon;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { collector.Dispose(); tray.Dispose(); uiTimer.Dispose(); debounce.Dispose(); }
            base.Dispose(disposing);
        }
    }

    sealed class DbListView : ListView
    {
        public DbListView()
        {
            Dock = DockStyle.Fill;
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            DoubleBuffered = true;
        }
    }
}

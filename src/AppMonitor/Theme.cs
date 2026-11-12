using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AppMonitor
{
    enum ThemeMode { System = 0, Light = 1, Dark = 2 }

    /// <summary>All UI colours in one place, for the light and dark themes.</summary>
    sealed class ThemeColors
    {
        // window chrome / standard controls
        public Color Window, WindowText, Control, ControlText, Border, Accent, Splitter, Grip, HintText, RecordText;
        // chart
        public Color ChartBg, Ink, Muted, Grid, LaneBg, LaneAlt, LaneHighlight, Other, Total, RefLine, Divider, DividerGrip,
                     OverviewBars, LegendWash, BannerBg, BannerInk, TipBg, TipBorder, TipShadow, Cursor;
        public int SelectionAlpha, AreaAlpha;

        public static readonly ThemeColors Light = new ThemeColors
        {
            Window = Color.White, WindowText = SystemColors.WindowText, Control = SystemColors.Control, ControlText = SystemColors.ControlText,
            Border = Color.FromArgb(0xC8, 0xCC, 0xD2), Accent = Color.FromArgb(0x4E, 0x79, 0xA7),
            Splitter = Color.FromArgb(0xD8, 0xDB, 0xE0), Grip = Color.FromArgb(0x8A, 0x90, 0x98), HintText = Color.DimGray,
            RecordText = Color.FromArgb(0xC0, 0x20, 0x20),
            ChartBg = Color.White, Ink = Color.FromArgb(0x30, 0x34, 0x3A), Muted = Color.FromArgb(0x80, 0x86, 0x8E),
            Grid = Color.FromArgb(0xEC, 0xEE, 0xF0), LaneBg = Color.FromArgb(0xFB, 0xFB, 0xFC), LaneAlt = Color.White,
            LaneHighlight = Color.FromArgb(0xFC, 0xEE, 0xF3), Other = Color.FromArgb(0xD5, 0xD8, 0xDC), Total = Color.FromArgb(0x55, 0x5B, 0x63),
            RefLine = Color.FromArgb(0xC0, 0xC4, 0xCA), Divider = Color.FromArgb(0xC8, 0xCC, 0xD2), DividerGrip = Color.FromArgb(0x9A, 0xA0, 0xA8),
            OverviewBars = Color.FromArgb(0xA8, 0xB8, 0xCC), LegendWash = Color.FromArgb(200, 255, 255, 255),
            BannerBg = Color.FromArgb(0xFF, 0xF4, 0xCE), BannerInk = Color.FromArgb(0x7A, 0x5B, 0x00),
            TipBg = Color.FromArgb(250, 255, 255, 255), TipBorder = Color.FromArgb(0xC8, 0xCC, 0xD2), TipShadow = Color.FromArgb(40, 0, 0, 0),
            Cursor = Color.FromArgb(120, 0x30, 0x34, 0x3A),
            SelectionAlpha = 50, AreaAlpha = 90,
        };

        public static readonly ThemeColors Dark = new ThemeColors
        {
            Window = Color.FromArgb(0x1E, 0x20, 0x24), WindowText = Color.FromArgb(0xDD, 0xE1, 0xE6),
            Control = Color.FromArgb(0x2A, 0x2D, 0x32), ControlText = Color.FromArgb(0xDD, 0xE1, 0xE6),
            Border = Color.FromArgb(0x45, 0x4A, 0x52), Accent = Color.FromArgb(0x6F, 0x9F, 0xD8),
            Splitter = Color.FromArgb(0x3A, 0x3E, 0x45), Grip = Color.FromArgb(0x7A, 0x80, 0x88), HintText = Color.FromArgb(0x9A, 0xA0, 0xA8),
            RecordText = Color.FromArgb(0xFF, 0x6B, 0x6B),
            ChartBg = Color.FromArgb(0x1A, 0x1C, 0x20), Ink = Color.FromArgb(0xDD, 0xE1, 0xE6), Muted = Color.FromArgb(0x8E, 0x95, 0x9E),
            Grid = Color.FromArgb(0x2C, 0x30, 0x36), LaneBg = Color.FromArgb(0x20, 0x23, 0x28), LaneAlt = Color.FromArgb(0x1A, 0x1C, 0x20),
            LaneHighlight = Color.FromArgb(0x45, 0x22, 0x33), Other = Color.FromArgb(0x4A, 0x4F, 0x57), Total = Color.FromArgb(0xB8, 0xBE, 0xC6),
            RefLine = Color.FromArgb(0x55, 0x5B, 0x63), Divider = Color.FromArgb(0x3A, 0x3E, 0x45), DividerGrip = Color.FromArgb(0x7A, 0x80, 0x88),
            OverviewBars = Color.FromArgb(0x4A, 0x5C, 0x74), LegendWash = Color.FromArgb(200, 0x1A, 0x1C, 0x20),
            BannerBg = Color.FromArgb(0x4A, 0x3C, 0x10), BannerInk = Color.FromArgb(0xFF, 0xDD, 0x80),
            TipBg = Color.FromArgb(250, 0x26, 0x29, 0x2E), TipBorder = Color.FromArgb(0x50, 0x55, 0x5D), TipShadow = Color.FromArgb(80, 0, 0, 0),
            Cursor = Color.FromArgb(140, 0xDD, 0xE1, 0xE6),
            SelectionAlpha = 70, AreaAlpha = 110,
        };
    }

    static class Theme
    {
        public static ThemeColors C { get; private set; } = ThemeColors.Light;
        public static bool IsDark => C == ThemeColors.Dark;
        public static ThemeMode Mode { get; private set; } = ThemeMode.System;
        public static event EventHandler Changed;

        /// <summary>Windows "Choose your app mode" setting.</summary>
        public static bool SystemPrefersDark()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        public static void Set(ThemeMode mode)
        {
            Mode = mode;
            bool dark = mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemPrefersDark());
            var next = dark ? ThemeColors.Dark : ThemeColors.Light;
            ToolStripManager.Renderer = dark ? new DarkToolStripRenderer() : (ToolStripRenderer)new ToolStripProfessionalRenderer();
            C = next;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Re-evaluate after Windows' light/dark setting changed (only matters in System mode).</summary>
        public static void SystemSettingChanged()
        {
            if (Mode != ThemeMode.System) return;
            bool dark = SystemPrefersDark();
            if (dark != IsDark) Set(ThemeMode.System);
        }

        // ---------------------------------------------------------------- applying to WinForms controls

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        const int LVM_GETHEADER = 0x101F;

        /// <summary>Dark title bar (Windows 10 20H1+ / 11).</summary>
        public static void ApplyTitleBar(Form f)
        {
            if (!f.IsHandleCreated) return;
            int on = IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
            // force a non-client repaint so the title bar picks it up immediately
            f.Width += 1; f.Width -= 1;
        }

        /// <summary>Colours every standard control under <paramref name="root"/>.</summary>
        public static void Apply(Control root)
        {
            var c = C;
            switch (root)
            {
                case ListView lv:
                    lv.BackColor = c.Window; lv.ForeColor = c.WindowText;
                    if (lv.IsHandleCreated) ThemeListView(lv); else lv.HandleCreated += (s, e) => ThemeListView((ListView)s);
                    break;
                case TextBox _:
                case NumericUpDown _:
                    root.BackColor = c.Window; root.ForeColor = c.WindowText;
                    break;
                case Button b:
                    b.FlatStyle = IsDark ? FlatStyle.Flat : FlatStyle.Standard;
                    b.FlatAppearance.BorderColor = c.Border;
                    b.BackColor = IsDark ? c.Control : SystemColors.Control; b.ForeColor = c.ControlText;
                    b.UseVisualStyleBackColor = !IsDark;
                    break;
                case CheckBox cb:
                    cb.FlatStyle = IsDark ? FlatStyle.Flat : FlatStyle.Standard;
                    cb.ForeColor = c.ControlText;
                    break;
                case ToolStrip ts:
                    ts.BackColor = c.Control; ts.ForeColor = c.ControlText;
                    foreach (ToolStripItem it in ts.Items) ApplyItem(it);
                    break;
                case TimelineControl tl:
                    tl.ThemeChanged(); // draws itself from Theme.C
                    break;
                case SplitContainer sc:
                    sc.BackColor = c.Splitter; // the visible splitter bar
                    break;
                default:
                    if (root is Form || root is Panel || root is SplitterPanel || root is Label || root is TableLayoutPanel || root is FlowLayoutPanel)
                    {
                        root.BackColor = c.Control; root.ForeColor = c.ControlText;
                    }
                    break;
            }
            foreach (Control child in root.Controls) Apply(child);
        }

        static void ApplyItem(ToolStripItem it)
        {
            it.ForeColor = C.ControlText;
            if (it is ToolStripComboBox cb)
            {
                cb.ComboBox.FlatStyle = IsDark ? FlatStyle.Flat : FlatStyle.Standard;
                cb.ComboBox.BackColor = C.Window; cb.ComboBox.ForeColor = C.WindowText;
            }
            if (it is ToolStripDropDownItem dd)
                foreach (ToolStripItem sub in dd.DropDownItems) ApplyItem(sub);
        }

        static void ThemeListView(ListView lv)
        {
            // dark scrollbars and column header (undocumented but stable Explorer themes)
            // (null restores the default look used in light mode)
            SetWindowTheme(lv.Handle, IsDark ? "DarkMode_Explorer" : null, null);
            IntPtr header = SendMessage(lv.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            if (header != IntPtr.Zero) SetWindowTheme(header, IsDark ? "DarkMode_ItemsView" : null, null);
            lv.Invalidate(true);
        }
    }

    /// <summary>Toolbar, status bar and menus in dark colours.</summary>
    sealed class DarkToolStripRenderer : ToolStripProfessionalRenderer
    {
        public DarkToolStripRenderer() : base(new DarkColorTable()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (!e.Item.Enabled) e.TextColor = Theme.C.Muted;
            else if (e.TextColor == SystemColors.ControlText || e.TextColor == SystemColors.MenuText) e.TextColor = Theme.C.ControlText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.C.ControlText;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // draw a visible check mark instead of the dark-on-dark default glyph
            var r = e.ImageRectangle;
            using var b = new SolidBrush(Color.FromArgb(0x3A, 0x4A, 0x60));
            e.Graphics.FillRectangle(b, r);
            using var p = new Pen(Theme.C.ControlText, 2);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(p, new[] { new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f), new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f), new PointF(r.Left + r.Width * 0.78f, r.Top + r.Height * 0.3f) });
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown) { using var p = new Pen(Theme.C.Border); e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1); }
            // no light line under toolbars / above the status bar
        }

        sealed class DarkColorTable : ProfessionalColorTable
        {
            static Color Bg => ThemeColors.Dark.Control;
            static Color Hover => Color.FromArgb(0x3A, 0x3E, 0x45);
            static Color Press => Color.FromArgb(0x2F, 0x4A, 0x6B);
            static Color Check => Color.FromArgb(0x2F, 0x4A, 0x6B);
            static Color Line => ThemeColors.Dark.Border;

            public override Color ToolStripGradientBegin => Bg;
            public override Color ToolStripGradientMiddle => Bg;
            public override Color ToolStripGradientEnd => Bg;
            public override Color ToolStripBorder => Bg;
            public override Color StatusStripGradientBegin => Bg;
            public override Color StatusStripGradientEnd => Bg;
            public override Color ToolStripDropDownBackground => Bg;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
            public override Color MenuBorder => Line;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color MenuItemPressedGradientBegin => Hover;
            public override Color MenuItemPressedGradientMiddle => Hover;
            public override Color MenuItemPressedGradientEnd => Hover;
            public override Color ButtonSelectedBorder => Hover;
            public override Color ButtonSelectedHighlight => Hover;
            public override Color ButtonSelectedGradientBegin => Hover;
            public override Color ButtonSelectedGradientMiddle => Hover;
            public override Color ButtonSelectedGradientEnd => Hover;
            public override Color ButtonPressedBorder => Press;
            public override Color ButtonPressedGradientBegin => Press;
            public override Color ButtonPressedGradientMiddle => Press;
            public override Color ButtonPressedGradientEnd => Press;
            public override Color ButtonCheckedGradientBegin => Check;
            public override Color ButtonCheckedGradientMiddle => Check;
            public override Color ButtonCheckedGradientEnd => Check;
            public override Color ButtonCheckedHighlight => Check;
            public override Color ButtonCheckedHighlightBorder => Press;
            public override Color CheckBackground => Check;
            public override Color CheckSelectedBackground => Press;
            public override Color CheckPressedBackground => Press;
            public override Color SeparatorDark => Line;
            public override Color SeparatorLight => Bg;
            public override Color GripDark => Line;
            public override Color GripLight => Bg;
            public override Color OverflowButtonGradientBegin => Bg;
            public override Color OverflowButtonGradientMiddle => Bg;
            public override Color OverflowButtonGradientEnd => Bg;
        }
    }
}

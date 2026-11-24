using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ArcBox.UI
{
    public sealed class Palette
    {
        public bool Dark;
        public Color Bg, Chrome, Side, Text, Sub, Muted, Border, Input, RowLine, Sel, Hover, Accent, AccentText, OnAccent, Btn, Field, Icon, Warn, Track, Rule, Dash, Folder;
    }

    /// <summary>Light / dark palettes taken from the approved mockup, plus helpers that paint WinForms controls.</summary>
    public static class Theme
    {
        public static readonly Palette Light = new Palette
        {
            Dark = false, Bg = C("#FFFFFF"), Chrome = C("#F3F3F3"), Side = C("#FAFAFA"), Text = C("#1B1B1B"), Sub = C("#5F5F5F"),
            Muted = C("#767676"), Border = C("#E5E5E5"), Input = C("#D1D1D1"), RowLine = C("#F2F2F2"), Sel = C("#CCE4F7"),
            Hover = C("#E8F1FB"), Accent = C("#005FB8"), AccentText = C("#005FB8"), OnAccent = C("#FFFFFF"), Btn = C("#FFFFFF"),
            Field = C("#FFFFFF"), Icon = C("#5F5F5F"), Warn = C("#B45309"), Track = C("#E5E5E5"), Rule = C("#D6E4F5"), Dash = C("#C8C8C8"),
            Folder = C("#F2C14E")
        };

        public static readonly Palette Dark = new Palette
        {
            Dark = true, Bg = C("#202020"), Chrome = C("#2B2B2B"), Side = C("#1C1C1C"), Text = C("#F3F3F3"), Sub = C("#B8B8B8"),
            Muted = C("#8C8C8C"), Border = C("#3A3A3A"), Input = C("#4A4A4A"), RowLine = C("#2A2A2A"), Sel = C("#0F3A5F"),
            Hover = C("#2F3E50"), Accent = C("#4CC2FF"), AccentText = C("#7FD3FF"), OnAccent = C("#0B0B0B"), Btn = C("#2D2D2D"),
            Field = C("#2B2B2B"), Icon = C("#C8C8C8"), Warn = C("#FFB454"), Track = C("#3A3A3A"), Rule = C("#2F4A66"), Dash = C("#555555"),
            Folder = C("#F2C14E")
        };

        public static Palette P { get; private set; } = Light;
        public static event Action Changed;

        private static Color C(string hex) => ColorTranslator.FromHtml(hex);

        public static void SetMode(string mode)
        {
            bool dark = mode == "dark" || (mode != "light" && SystemPrefersDark());
            P = dark ? Dark : Light;
            Changed?.Invoke();
        }

        public static bool SystemPrefersDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------ fonts & dpi
        private static Font _ui, _uiBold, _title;
        public static Font UiFont => _ui ?? (_ui = MakeFont(9f, FontStyle.Regular));
        public static Font UiBold => _uiBold ?? (_uiBold = MakeFont(9f, FontStyle.Bold));
        public static Font TitleFont => _title ?? (_title = MakeFont(13f, FontStyle.Bold));

        public static Font MakeFont(float size, FontStyle style)
        {
            foreach (var name in new[] { "Malgun Gothic", "Segoe UI", "NanumGothic", "Noto Sans CJK KR" })
            {
                try
                {
                    var f = new Font(name, size, style);
                    if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
                    f.Dispose();
                }
                catch { }
            }
            return new Font(SystemFonts.MessageBoxFont.FontFamily, size, style);
        }

        private static float _scale = -1;
        public static float Scale
        {
            get
            {
                if (_scale < 0)
                {
                    using (var g = Graphics.FromHwnd(IntPtr.Zero)) _scale = g.DpiX / 96f;
                }
                return _scale;
            }
        }
        public static int S(int px) => (int)Math.Round(px * Scale);

        // ------------------------------------------------------------ native
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        public static void TitleBar(Form f)
        {
            if (!f.IsHandleCreated) return;
            try
            {
                int on = P.Dark ? 1 : 0;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
            }
            catch { }
        }

        private static bool? _win11;
        public static bool IsWin11
        {
            get
            {
                if (_win11 == null)
                {
                    try
                    {
                        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                            _win11 = int.TryParse(k?.GetValue("CurrentBuildNumber") as string, out int b) && b >= 22000;
                    }
                    catch { _win11 = false; }
                }
                return _win11.Value;
            }
        }

        /// <summary>Rounded corners and a themed border on Windows 11 popups (menus, the double-click menu).</summary>
        public static bool RoundPopup(IntPtr hwnd)
        {
            if (!IsWin11 || hwnd == IntPtr.Zero) return false;
            try
            {
                int round = 2; // DWMWCP_ROUND
                if (DwmSetWindowAttribute(hwnd, 33, ref round, 4) != 0) return false;
                var c = P.Input;
                int bgr = c.R | (c.G << 8) | (c.B << 16);
                DwmSetWindowAttribute(hwnd, 34, ref bgr, 4); // DWMWA_BORDER_COLOR
                return true;
            }
            catch { return false; }
        }

        private static readonly HashSet<ToolStrip> StyledMenus = new HashSet<ToolStrip>();

        /// <summary>Gives every drop-down under ts (menu bar, toolbar buttons, context menus) the look of the double-click menu:
        /// roomier rows, rounded highlight, rounded window on Windows 11.</summary>
        public static void StyleMenus(ToolStrip ts)
        {
            if (ts == null || !StyledMenus.Add(ts)) return;
            ts.Disposed += (o, e) => StyledMenus.Remove(ts);
            if (ts is ToolStripDropDown dd)
            {
                dd.Padding = new Padding(S(4), S(5), S(4), S(5));
                dd.Opening += (o, e) => PadItems(dd);
                dd.Opened += (o, e) => RoundPopup(dd.Handle);
                if (dd is ToolStripDropDownMenu dm) dm.ImageScalingSize = new Size(S(16), S(16));
            }
            foreach (ToolStripItem it in ts.Items)
                if (it is ToolStripDropDownItem di) Hook(di);
        }

        private static readonly HashSet<ToolStripItem> Hooked = new HashSet<ToolStripItem>();

        private static void Hook(ToolStripDropDownItem di)
        {
            if (!Hooked.Add(di)) return;
            di.Disposed += (o, e) => Hooked.Remove(di);
            di.DropDownOpening += (o, e) => { if (!(di.DropDown.Renderer is FlatRenderer)) di.DropDown.Renderer = new FlatRenderer(); StyleMenus(di.DropDown); };
        }

        private static void PadItems(ToolStripDropDown dd)
        {
            foreach (ToolStripItem it in dd.Items)
            {
                if (it is ToolStripMenuItem mi)
                {
                    mi.Padding = new Padding(0, S(4), 0, S(4));
                    if (mi.HasDropDownItems) Hook(mi);
                }
                else if (it is ToolStripSeparator sep) { sep.AutoSize = false; sep.Height = S(9); }
            }
        }

        /// <summary>Dark scroll bars for scrollable panels and multi-line text boxes.</summary>
        public static void DarkScroll(Control c)
        {
            if (c.IsHandleCreated) Explorer(c);
            else c.HandleCreated += (s, e) => Explorer(c);
        }

        public static void Explorer(Control c)
        {
            try { SetWindowTheme(c.Handle, P.Dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
        }

        // ------------------------------------------------------------ painting controls
        public static void Apply(Control root)
        {
            if (root is Form f)
            {
                f.BackColor = P.Bg; f.ForeColor = P.Text;
                if (f.IsHandleCreated) TitleBar(f); else f.HandleCreated += (s, e) => TitleBar(f);
            }
            ApplyOne(root);
            foreach (Control c in root.Controls) Apply(c);
        }

        private static void ApplyOne(Control c)
        {
            string role = c.Tag as string ?? "";
            switch (c)
            {
                case MenuStrip ms: ms.Renderer = new FlatRenderer(); ms.BackColor = P.Chrome; ms.ForeColor = P.Text; break;
                case StatusStrip ss: ss.Renderer = new FlatRenderer(); ss.BackColor = P.Chrome; ss.ForeColor = P.Sub; break;
                case ToolStrip ts: ts.Renderer = new FlatRenderer(); ts.BackColor = P.Bg; ts.ForeColor = P.Text; break;
                case Button b: StyleButton(b, role == "primary"); break;
                case FlatCombo fc: fc.ApplyTheme(); break;
                case FlatTextBox ftb: ftb.ApplyTheme(); break;
                case TextBox tb when role == "bare" || tb.BorderStyle == BorderStyle.None:
                    if (role == "bare") tb.BackColor = P.Field; // inside a field box; other borderless ones are coloured by their owner
                    tb.ForeColor = P.Text;
                    if (tb.Multiline) DarkScroll(tb);
                    break;
                case TextBox tb: tb.BackColor = P.Field; tb.ForeColor = P.Text; tb.BorderStyle = BorderStyle.FixedSingle; break;
                case ComboBox cb: cb.BackColor = P.Field; cb.ForeColor = P.Text; cb.FlatStyle = FlatStyle.Flat; break;
                case ListView lv: lv.BackColor = P.Bg; lv.ForeColor = P.Text; if (lv.IsHandleCreated) Explorer(lv); else lv.HandleCreated += (s, e) => Explorer(lv); break;
                case TreeView tv: tv.BackColor = P.Side; tv.ForeColor = P.Text; tv.LineColor = P.Sub; if (tv.IsHandleCreated) Explorer(tv); else tv.HandleCreated += (s, e) => Explorer(tv); break;
                case ListBox lb: lb.BackColor = P.Field; lb.ForeColor = P.Text; break;
                case CheckBox ch:
                    if (ch.Appearance == Appearance.Button) StyleToggle(ch);
                    else { ch.ForeColor = ch.Enabled ? P.Text : P.Muted; ch.BackColor = Color.Transparent; }
                    break;
                case RadioButton rb:
                    if (rb.Appearance == Appearance.Button) StyleToggle(rb);
                    else { rb.ForeColor = P.Text; rb.BackColor = Color.Transparent; }
                    break;
                case LinkLabel ll: ll.LinkColor = P.AccentText; ll.ActiveLinkColor = P.AccentText; ll.VisitedLinkColor = P.AccentText; ll.BackColor = Color.Transparent; break;
                case Label l:
                    l.BackColor = Color.Transparent;
                    l.ForeColor = role == "sub" ? P.Sub : role == "accent" ? P.AccentText : role == "muted" ? P.Muted : P.Text;
                    break;
                case ProgressBar _: break;
                case Panel p when p.AutoScroll:
                    p.BackColor = role == "chrome" ? P.Chrome : role == "side" ? P.Side : P.Bg;
                    DarkScroll(p);
                    break;
                case Panel p:
                    // layout panels without a role take their parent's colour, so button rows on a footer bar
                    // don't show up as a darker box
                    Color inherit = p.Parent != null && p.Parent.BackColor.A == 255 ? p.Parent.BackColor : P.Bg;
                    p.BackColor = role == "chrome" ? P.Chrome : role == "side" ? P.Side : role == "line" ? P.Border : role == "rule" ? P.Rule : role == "field" ? P.Field
                        : (p is FlowLayoutPanel || p is TableLayoutPanel) ? inherit : P.Bg;
                    p.ForeColor = P.Text;
                    break;
                case GroupBox g: g.ForeColor = P.Text; g.BackColor = P.Bg; break;
                case NumericUpDown n: n.BackColor = P.Field; n.ForeColor = P.Text; break;
            }
        }

        public static void StyleButton(Button b, bool primary)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = P.Input;
            b.BackColor = primary ? P.Accent : P.Btn;
            b.ForeColor = primary ? P.OnAccent : P.Text;
            b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(P.Accent, 0.15f) : P.Hover;
            b.FlatAppearance.MouseDownBackColor = primary ? ControlPaint.Dark(P.Accent, 0.05f) : P.Sel;
            if (primary) b.Font = UiBold;
        }

        private static void StyleToggle(ButtonBase b)
        {
            bool on = b is CheckBox c ? c.Checked : ((RadioButton)b).Checked;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = on ? P.Accent : P.Input;
            b.FlatAppearance.CheckedBackColor = P.Hover;
            b.FlatAppearance.MouseOverBackColor = P.Hover;
            b.BackColor = on ? P.Hover : P.Btn;
            b.ForeColor = on ? (P.Dark ? P.AccentText : C("#003E7A")) : P.Text;
            b.Font = on ? UiBold : UiFont;
        }

        /// <summary>Re-style a toggle button after its checked state changed.</summary>
        public static void Restyle(ButtonBase b) => StyleToggle(b);

        // ------------------------------------------------------------ toolstrip renderer
        public sealed class FlatRenderer : ToolStripProfessionalRenderer
        {
            public FlatRenderer() : base(new Colors()) { RoundedEdges = false; }

            protected override void Initialize(ToolStrip toolStrip)
            {
                base.Initialize(toolStrip);
                StyleMenus(toolStrip);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                if (e.ToolStrip is ToolStripDropDown && !IsWin11)
                {
                    using (var p = new Pen(P.Input)) e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
                }
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                Color c = e.ToolStrip is ToolStripDropDown ? (P.Dark ? P.Chrome : P.Bg) : e.ToolStrip.BackColor;
                using (var b = new SolidBrush(c)) e.Graphics.FillRectangle(b, e.AffectedBounds);
            }

            protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
            {
                var item = e.Item;
                bool on = item is ToolStripButton tb && tb.Checked;
                if (!item.Selected && !item.Pressed && !on) return;
                var r = new Rectangle(1, 1, item.Width - 2, item.Height - 2);
                using (var path = Round(r, S(6)))
                using (var b = new SolidBrush(item.Pressed ? P.Sel : P.Hover))
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.FillPath(b, path);
                }
            }

            protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e) => OnRenderButtonBackground(e);
            protected override void OnRenderSplitButtonBackground(ToolStripItemRenderEventArgs e) => OnRenderButtonBackground(e);

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected && !e.Item.Pressed) return;
                if (!e.Item.Enabled) return;
                bool top = e.Item.Owner is MenuStrip;
                var r = top ? new Rectangle(1, S(2), e.Item.Width - 2, e.Item.Height - S(4)) : new Rectangle(S(2), 1, e.Item.Width - S(4), e.Item.Height - 2);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Round(r, S(5)))
                using (var b = new SolidBrush(P.Hover)) e.Graphics.FillPath(b, path);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = !e.Item.Enabled ? P.Muted : (e.Item.Owner is StatusStrip ? P.Sub : P.Text);
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = P.Sub;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                using (var p = new Pen(P.Border))
                {
                    if (e.Vertical) e.Graphics.DrawLine(p, e.Item.Width / 2, S(8), e.Item.Width / 2, e.Item.Height - S(8));
                    else e.Graphics.DrawLine(p, S(8), e.Item.Height / 2, e.Item.Width - S(8), e.Item.Height / 2);
                }
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                var r = e.ImageRectangle;
                using (var b = new SolidBrush(P.Hover)) e.Graphics.FillRectangle(b, r);
                using (var p = new Pen(P.AccentText, Math.Max(1.5f, Scale * 1.6f)))
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.DrawLines(p, new[]
                    {
                        new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
                        new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
                        new PointF(r.Left + r.Width * 0.78f, r.Top + r.Height * 0.30f)
                    });
                }
            }

            protected override void OnRenderStatusStripSizingGrip(ToolStripRenderEventArgs e) { }
        }

        private sealed class Colors : ProfessionalColorTable
        {
            public override Color MenuItemSelected => P.Hover;
            public override Color MenuItemBorder => P.Hover;
            public override Color MenuBorder => P.Input;
            public override Color ToolStripDropDownBackground => P.Dark ? P.Chrome : P.Bg;
            public override Color ImageMarginGradientBegin => P.Dark ? P.Chrome : P.Bg;
            public override Color ImageMarginGradientMiddle => P.Dark ? P.Chrome : P.Bg;
            public override Color ImageMarginGradientEnd => P.Dark ? P.Chrome : P.Bg;
            public override Color SeparatorDark => P.Border;
            public override Color SeparatorLight => P.Border;
            public override Color MenuStripGradientBegin => P.Chrome;
            public override Color MenuStripGradientEnd => P.Chrome;
            public override Color MenuItemPressedGradientBegin => P.Hover;
            public override Color MenuItemPressedGradientEnd => P.Hover;
            public override Color StatusStripGradientBegin => P.Chrome;
            public override Color StatusStripGradientEnd => P.Chrome;
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(1, radius * 2);
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>Icons rendered from the Windows icon fonts (Segoe Fluent Icons / Segoe MDL2 Assets).</summary>
    public static class Glyphs
    {
        public const string Open = "", NewArchive = "", Extract = "", Add = "", Delete = "",
            Test = "", Info = "", Settings = "", Up = "", Search = "", Lock = "",
            NewFolder = "", Rename = "", Refresh = "", Back = "", View = "\uE8A9", Forward = "\uE72A", ChevronDown = "\uE70D", Repair = "\uE90F";

        private static string _family;
        private static readonly Dictionary<string, Bitmap> Cache = new Dictionary<string, Bitmap>();

        private static string Family
        {
            get
            {
                if (_family != null) return _family;
                using (var fonts = new InstalledFontCollection())
                {
                    var names = new HashSet<string>(fonts.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                    _family = names.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : names.Contains("Segoe MDL2 Assets") ? "Segoe MDL2 Assets" : "";
                }
                return _family;
            }
        }

        public static Bitmap Get(string glyph, int size, Color color, string fallback = "?")
        {
            string key = glyph + "|" + size + "|" + color.ToArgb();
            if (Cache.TryGetValue(key, out var bmp)) return bmp;
            bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(color))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    if (Family.Length > 0)
                    {
                        using (var f = new Font(Family, size * 0.72f, FontStyle.Regular, GraphicsUnit.Pixel))
                            g.DrawString(glyph, f, brush, new RectangleF(0, 0, size, size), sf);
                    }
                    else
                    {
                        using (var f = new Font(Theme.UiFont.FontFamily, size * 0.45f, FontStyle.Bold, GraphicsUnit.Pixel))
                            g.DrawString(fallback, f, brush, new RectangleF(0, 0, size, size), sf);
                    }
                }
            }
            Cache[key] = bmp;
            return bmp;
        }
    }
}

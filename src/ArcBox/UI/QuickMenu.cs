using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ArcBox.Core;

namespace ArcBox.UI
{
    /// <summary>Small popup shown at the mouse when an archive is double-clicked (setting "메뉴 보이기").</summary>
    public static class QuickMenu
    {
        /// <returns>The command verb to run ("/open", "/x", ...), or "" when the menu was dismissed.</returns>
        public static string Choose(string archive)
        {
            using (var f = new MenuForm(archive))
            {
                Application.Run(f);
                return f.Result;
            }
        }

        private sealed class Item
        {
            public string Text, Hint, Verb, Glyph;
            public bool Separator, Primary;
            public Rectangle Bounds;
        }

        private sealed class MenuForm : Form
        {
            public string Result = "";
            private readonly List<Item> _items = new List<Item>();
            private readonly string _name, _sub;
            private int _hot = -1;
            private bool _dwmBorder;

            private int Pad => Theme.S(6);
            private int HeadH => Theme.S(58);
            private int RowH => Theme.S(34);
            private int SepH => Theme.S(9);

            public MenuForm(string archive)
            {
                _name = archive == null ? "ArcBox" : Path.GetFileName(archive);
                string folder = archive == null ? "" : PathUtil.ArchiveBaseName(archive);
                try
                {
                    var fi = new FileInfo(archive);
                    _sub = fi.Extension.TrimStart('.').ToUpperInvariant() + " 압축 파일 · " + PathUtil.FormatSize((ulong)fi.Length);
                }
                catch { _sub = "압축 파일"; }

                _items.Add(new Item { Text = "ArcBox로 열기", Verb = "/open", Glyph = Glyphs.Open, Primary = true });
                _items.Add(new Item { Separator = true });
                _items.Add(new Item { Text = "여기에 풀기", Hint = "스마트", Verb = "/x", Glyph = Glyphs.Extract });
                _items.Add(new Item { Text = "폴더로 풀기", Hint = folder.Length > 0 ? folder + "\\" : null, Verb = "/xf", Glyph = Glyphs.NewFolder });
                _items.Add(new Item { Text = "압축 풀기…", Hint = "위치 선택", Verb = "/xd", Glyph = Glyphs.Extract });
                _items.Add(new Item { Separator = true });
                _items.Add(new Item { Text = "압축 파일 테스트", Verb = "/t", Glyph = Glyphs.Test });

                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                KeyPreview = true;
                DoubleBuffered = true;
                AutoScaleMode = AutoScaleMode.None;
                Font = Theme.UiFont;
                Text = "ArcBox";
                BackColor = Bg;

                int w = Theme.S(300);
                int y = HeadH + Pad / 2;
                foreach (var it in _items)
                {
                    int h = it.Separator ? SepH : RowH;
                    it.Bounds = new Rectangle(Pad, y, w - Pad * 2, h);
                    y += h;
                }
                ClientSize = new Size(w, y + Pad);

                // open at the mouse, kept inside the screen
                var at = Cursor.Position;
                var wa = Screen.FromPoint(at).WorkingArea;
                int x = Math.Min(at.X, wa.Right - Width), top = Math.Min(at.Y, wa.Bottom - Height);
                Location = new Point(Math.Max(wa.Left, x), Math.Max(wa.Top, top));
            }

            private static Color Bg => Theme.P.Dark ? Theme.P.Chrome : Theme.P.Bg;

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW
                    cp.ExStyle |= 0x80;         // WS_EX_TOOLWINDOW (no taskbar / alt-tab entry)
                    return cp;
                }
            }

            [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
            [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                try
                {
                    int round = 2; // DWMWCP_ROUND (Windows 11)
                    if (DwmSetWindowAttribute(Handle, 33, ref round, 4) == 0)
                    {
                        var c = Theme.P.Input;
                        int bgr = c.R | (c.G << 8) | (c.B << 16);
                        _dwmBorder = DwmSetWindowAttribute(Handle, 34, ref bgr, 4) == 0; // DWMWA_BORDER_COLOR
                    }
                }
                catch { }
            }

            private readonly Timer _outside = new Timer { Interval = 80 };

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                try { SetForegroundWindow(Handle); } catch { }
                Activate();
                // if Windows refused to give us focus, still close on a click elsewhere
                _outside.Tick += (o, a) =>
                {
                    if (MouseButtons != MouseButtons.None && !Bounds.Contains(Cursor.Position)) Close();
                };
                _outside.Start();
            }

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                _outside.Dispose();
                base.OnFormClosed(e);
            }

            protected override void OnDeactivate(EventArgs e)
            {
                base.OnDeactivate(e);
                if (Result == "") BeginInvoke((Action)Close);
            }

            private void Pick(int i)
            {
                if (i < 0 || i >= _items.Count || _items[i].Separator) return;
                Result = _items[i].Verb;
                Close();
            }

            private int HitTest(Point p)
            {
                for (int i = 0; i < _items.Count; i++)
                    if (!_items[i].Separator && _items[i].Bounds.Contains(p)) return i;
                return -1;
            }

            private void SetHot(int i)
            {
                if (i == _hot) return;
                _hot = i;
                Invalidate();
            }

            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); SetHot(HitTest(e.Location)); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetHot(-1); }
            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button == MouseButtons.Left) Pick(HitTest(e.Location));
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                switch (keyData)
                {
                    case Keys.Escape: Close(); return true;
                    case Keys.Enter: case Keys.Space: Pick(_hot < 0 ? 0 : _hot); return true;
                    case Keys.Down: case Keys.Tab: Move(1); return true;
                    case Keys.Up: case Keys.Shift | Keys.Tab: Move(-1); return true;
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }

            private void Move(int dir)
            {
                int i = _hot;
                for (int n = 0; n < _items.Count; n++)
                {
                    i = (i + dir + _items.Count) % _items.Count;
                    if (!_items[i].Separator) { SetHot(i); return; }
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                var p = Theme.P;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Bg);

                // header: archive badge + file name + type/size
                int bs = Theme.S(36);
                var badge = new Rectangle(Theme.S(14), (HeadH - bs) / 2 + Theme.S(2), bs, bs);
                using (var path = Theme.Round(badge, Theme.S(8)))
                using (var b = new SolidBrush(Color.FromArgb(p.Dark ? 60 : 28, p.Accent))) g.FillPath(b, path);
                int gs = Theme.S(22);
                g.DrawImage(Glyphs.Get(Glyphs.NewArchive, gs, p.Dark ? p.AccentText : p.Accent, "A"), badge.X + (bs - gs) / 2, badge.Y + (bs - gs) / 2);
                int tx = badge.Right + Theme.S(12), tw = ClientSize.Width - tx - Theme.S(12);
                var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.Left;
                TextRenderer.DrawText(g, _name, Theme.UiBold, new Rectangle(tx, badge.Y - Theme.S(1), tw, Theme.S(20)), p.Text, flags);
                TextRenderer.DrawText(g, _sub, Theme.UiFont, new Rectangle(tx, badge.Y + Theme.S(18), tw, Theme.S(20)), p.Sub, flags);
                using (var pen = new Pen(p.Border)) g.DrawLine(pen, 0, HeadH, ClientSize.Width, HeadH);

                // items
                for (int i = 0; i < _items.Count; i++)
                {
                    var it = _items[i];
                    var r = it.Bounds;
                    if (it.Separator)
                    {
                        using (var pen = new Pen(p.Border)) g.DrawLine(pen, r.Left + Theme.S(8), r.Top + r.Height / 2, r.Right - Theme.S(8), r.Top + r.Height / 2);
                        continue;
                    }
                    if (i == _hot)
                        using (var path = Theme.Round(r, Theme.S(6)))
                        using (var b = new SolidBrush(p.Hover)) g.FillPath(b, path);
                    int isz = Theme.S(18);
                    g.DrawImage(Glyphs.Get(it.Glyph, isz, it.Primary ? (p.Dark ? p.AccentText : p.Accent) : p.Icon, "•"), r.Left + Theme.S(10), r.Top + (r.Height - isz) / 2);
                    int x = r.Left + Theme.S(40);
                    var font = it.Primary ? Theme.UiBold : Theme.UiFont;
                    var tr = new Rectangle(x, r.Top, r.Right - x - Theme.S(10), r.Height);
                    var vflags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
                    int textW = TextRenderer.MeasureText(g, it.Text, font, tr.Size, vflags).Width;
                    TextRenderer.DrawText(g, it.Text, font, tr, p.Text, vflags | TextFormatFlags.Left);
                    if (!string.IsNullOrEmpty(it.Hint))
                    {
                        var hr = new Rectangle(x + textW + Theme.S(12), r.Top, tr.Right - (x + textW + Theme.S(12)), r.Height);
                        if (hr.Width > Theme.S(24)) TextRenderer.DrawText(g, it.Hint, Theme.UiFont, hr, p.Muted, vflags | TextFormatFlags.Right);
                    }
                }

                if (!_dwmBorder)
                    using (var pen = new Pen(p.Input)) g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            }
        }
    }
}

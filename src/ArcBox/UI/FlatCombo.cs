using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ArcBox.UI
{
    /// <summary>Themed replacement for ComboBox: rounded box, and the list opens as the same styled menu as the rest of ArcBox.
    /// Editable mode has a text box (paths, sizes); list mode only picks from Items.</summary>
    public sealed class FlatCombo : Control
    {
        public readonly List<string> Items = new List<string>();
        public event EventHandler SelectedIndexChanged;

        private readonly bool _editable;
        private readonly TextBox _edit;
        private int _sel = -1;
        private bool _hover;
        private ContextMenuStrip _menu;

        private int ArrowW => Theme.S(30);

        public FlatCombo(bool editable)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            _editable = editable;
            Font = Theme.UiFont;
            Height = Theme.S(30);
            MinimumSize = new Size(Theme.S(60), Theme.S(30));
            TabStop = !editable;
            Cursor = editable ? Cursors.Default : Cursors.Hand;
            if (editable)
            {
                _edit = new TextBox { BorderStyle = BorderStyle.None, Font = Theme.UiFont, Tag = "bare" };
                _edit.TextChanged += (s, e) => OnTextChanged(e);
                _edit.GotFocus += (s, e) => Invalidate();
                _edit.LostFocus += (s, e) => Invalidate();
                _edit.KeyDown += (s, e) => { if ((e.Alt && e.KeyCode == Keys.Down) || e.KeyCode == Keys.F4) { ShowList(); e.Handled = true; } };
                Controls.Add(_edit);
            }
            ApplyTheme();
        }

        public void ApplyTheme()
        {
            BackColor = Color.Transparent;
            if (_edit != null) { _edit.BackColor = Theme.P.Field; _edit.ForeColor = Theme.P.Text; }
            Invalidate();
        }

        public override string Text
        {
            get => _editable ? _edit.Text : (_sel >= 0 && _sel < Items.Count ? Items[_sel] : "");
            set
            {
                if (_editable) _edit.Text = value ?? "";
                else { int i = Items.IndexOf(value); if (i >= 0) SelectedIndex = i; }
                Invalidate();
            }
        }

        /// <summary>In editable mode this is the index of the typed text in Items, or -1 (like ComboBox).</summary>
        public int SelectedIndex
        {
            get => _editable ? Items.IndexOf(_edit.Text) : _sel;
            set
            {
                if (value < -1 || value >= Items.Count) return;
                bool changed = value != SelectedIndex;
                _sel = value;
                if (_editable && value >= 0) { _edit.Text = Items[value]; _edit.SelectionStart = _edit.TextLength; }
                Invalidate();
                if (changed) SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_edit == null) return;
            int x = Theme.S(9);
            _edit.SetBounds(x, (Height - _edit.PreferredHeight) / 2 + 1, Math.Max(10, Width - x - ArrowW), _edit.PreferredHeight);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (_edit != null) _edit.Enabled = Enabled;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;
            if (_editable && e.X < Width - ArrowW) { _edit.Focus(); return; }
            if (!_editable) Focus();
            ShowList();
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_editable) return;
            if (e.KeyCode == Keys.Down && !e.Alt) { if (_sel < Items.Count - 1) SelectedIndex = _sel + 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Up) { if (_sel > 0) SelectedIndex = _sel - 1; e.Handled = true; }
            else if (e.KeyCode == Keys.F4 || e.KeyCode == Keys.Space || (e.Alt && e.KeyCode == Keys.Down)) { ShowList(); e.Handled = true; }
        }

        private void ShowList()
        {
            if (Items.Count == 0) return;
            _menu?.Dispose();
            var m = new ContextMenuStrip { Renderer = new Theme.FlatRenderer(), Font = Theme.UiFont, ShowCheckMargin = true, ShowImageMargin = false };
            int cur = SelectedIndex;
            for (int i = 0; i < Items.Count; i++)
            {
                int idx = i;
                var mi = new ToolStripMenuItem(Items[i]) { Checked = i == cur };
                mi.Click += (s, e) => { SelectedIndex = idx; if (_editable) _edit.Focus(); else Focus(); };
                m.Items.Add(mi);
            }
            m.MinimumSize = new Size(Width, 0);
            m.Closed += (s, e) => { _hover = false; Invalidate(); };
            _menu = m;
            m.Show(this, new Point(0, Height + Theme.S(2)));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _menu?.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var p = Theme.P;
            var parentBg = Parent?.BackColor ?? p.Bg;
            if (parentBg.A < 255) parentBg = p.Bg;
            g.Clear(parentBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool focused = Focused || (_edit != null && _edit.Focused);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.Round(r, Theme.S(5)))
            {
                using (var b = new SolidBrush(!_editable && _hover && Enabled ? p.Hover : p.Field)) g.FillPath(b, path);
                using (var pen = new Pen(focused ? p.Accent : p.Input)) g.DrawPath(pen, path);
            }
            if (focused)
                using (var pen = new Pen(p.Accent, Theme.S(2)))
                    g.DrawLine(pen, Theme.S(4), Height - Theme.S(1), Width - Theme.S(5), Height - Theme.S(1));

            if (!_editable)
            {
                var tr = new Rectangle(Theme.S(10), 0, Width - Theme.S(10) - ArrowW, Height);
                TextRenderer.DrawText(g, Text, Font, tr, Enabled ? p.Text : p.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            else
            {
                if (_edit.BackColor != p.Field) _edit.BackColor = p.Field;
                if (_hover && Enabled)
                    using (var path = Theme.Round(new Rectangle(Width - ArrowW, Theme.S(3), ArrowW - Theme.S(3), Height - Theme.S(7)), Theme.S(4)))
                    using (var b = new SolidBrush(p.Hover)) g.FillPath(b, path);
            }
            int a = Theme.S(12);
            g.DrawImage(Glyphs.Get(Glyphs.ChevronDown, a, Enabled ? p.Sub : p.Muted, "▾"), Width - ArrowW + (ArrowW - a) / 2 - Theme.S(1), (Height - a) / 2);
        }
    }
}

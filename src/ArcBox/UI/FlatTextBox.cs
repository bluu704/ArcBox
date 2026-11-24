using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ArcBox.UI
{
    /// <summary>TextBox with the same rounded, padded look as FlatCombo. The frame is drawn in the non-client area,
    /// so it is still a plain TextBox for the code that uses it (Text, UseSystemPasswordChar, SelectAll …).</summary>
    public class FlatTextBox : TextBox
    {
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr rgn, uint flags);

        private const int WM_NCCALCSIZE = 0x83, WM_NCPAINT = 0x85;

        public FlatTextBox()
        {
            BorderStyle = BorderStyle.None;
            AutoSize = false;
            Font = Theme.UiFont;
            Height = Theme.S(30);
            ApplyTheme();
        }

        public void ApplyTheme()
        {
            BackColor = Enabled ? Theme.P.Field : Theme.P.Bg;
            ForeColor = Theme.P.Text;
            RedrawFrame();
        }

        private int PadX => Theme.S(9);

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCCALCSIZE)
            {
                base.WndProc(ref m);
                if (m.LParam == IntPtr.Zero) return;
                var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                if (Multiline)
                {
                    int p = Theme.S(4);
                    r.Left += p; r.Top += p; r.Right -= p; r.Bottom -= p;
                }
                else
                {
                    // centre the single text line vertically
                    int h = r.Bottom - r.Top, th = Font.Height + Theme.S(2);
                    int pad = Math.Max(1, (h - th) / 2);
                    r.Left += PadX; r.Right -= PadX; r.Top += pad; r.Bottom -= Math.Max(1, h - th - pad);
                }
                Marshal.StructureToPtr(r, m.LParam, false);
                return;
            }
            if (m.Msg == WM_NCPAINT)
            {
                PaintFrame();
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        private void PaintFrame()
        {
            if (!IsHandleCreated) return;
            IntPtr dc = GetWindowDC(Handle);
            if (dc == IntPtr.Zero) return;
            try
            {
                GetWindowRect(Handle, out var wr);
                var client = RectangleToScreen(ClientRectangle);
                client.Offset(-wr.Left, -wr.Top);
                using (var g = Graphics.FromHdc(dc))
                {
                    g.ExcludeClip(client);
                    var p = Theme.P;
                    var parentBg = Parent?.BackColor ?? p.Bg;
                    if (parentBg.A < 255) parentBg = p.Bg;
                    g.Clear(parentBg);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    var r = new Rectangle(0, 0, wr.Right - wr.Left - 1, wr.Bottom - wr.Top - 1);
                    using (var path = Theme.Round(r, Theme.S(5)))
                    {
                        using (var b = new SolidBrush(BackColor)) g.FillPath(b, path);
                        using (var pen = new Pen(Focused ? p.Accent : p.Input)) g.DrawPath(pen, path);
                    }
                    if (Focused)
                        using (var pen = new Pen(p.Accent, Theme.S(2)))
                            g.DrawLine(pen, Theme.S(4), r.Bottom, r.Right - Theme.S(4), r.Bottom);
                }
            }
            finally { ReleaseDC(Handle, dc); }
        }

        private void RedrawFrame()
        {
            if (IsHandleCreated) RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x400 | 0x1 | 0x100); // RDW_FRAME | RDW_INVALIDATE | RDW_UPDATENOW
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x1 | 0x2 | 0x4 | 0x20); // recalc frame (SWP_FRAMECHANGED)
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); RedrawFrame(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); RedrawFrame(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); ApplyTheme(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); RedrawFrame(); }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ArcBox.UI
{
    /// <summary>Small helpers to build dialogs in code with consistent spacing.</summary>
    public static class Kit
    {
        public static Button Btn(string text, bool primary = false, int minWidth = 84)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, Font = Theme.UiFont,
                MinimumSize = new Size(Theme.S(minWidth), Theme.S(30)), Padding = new Padding(Theme.S(8), 0, Theme.S(8), 0),
                Tag = primary ? "primary" : null, Margin = new Padding(Theme.S(4), 0, 0, 0)
            };
            return b;
        }

        public static Label Lbl(string text, string role = null, bool bold = false)
        {
            return new Label
            {
                Text = text, AutoSize = true, Tag = role, Font = bold ? Theme.UiBold : Theme.UiFont,
                Margin = new Padding(0, Theme.S(4), 0, Theme.S(4))
            };
        }

        public static TextBox Txt(string text = "", bool password = false)
        {
            return new TextBox
            {
                Text = text, Font = Theme.UiFont, UseSystemPasswordChar = password, Dock = DockStyle.Fill,
                Margin = new Padding(0, Theme.S(2), 0, Theme.S(2))
            };
        }

        public static CheckBox Chk(string text, bool on = false)
        {
            return new CheckBox { Text = text, Checked = on, AutoSize = true, Font = Theme.UiFont, Margin = new Padding(0, Theme.S(3), Theme.S(12), Theme.S(3)) };
        }

        public static RadioButton Radio(string text, bool on = false)
        {
            return new RadioButton { Text = text, Checked = on, AutoSize = true, Font = Theme.UiFont, Margin = new Padding(0, Theme.S(3), Theme.S(14), Theme.S(3)) };
        }

        public static FlatCombo Combo(IEnumerable<string> items, int selected = 0, bool editable = false)
        {
            var c = new FlatCombo(editable) { Dock = DockStyle.Fill, Margin = new Padding(0) };
            c.Items.AddRange(items);
            if (c.Items.Count > 0) c.SelectedIndex = Math.Max(0, Math.Min(selected, c.Items.Count - 1));
            return c;
        }

        public static FlowLayoutPanel Row(params Control[] controls)
        {
            var p = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0), Padding = new Padding(0), Dock = DockStyle.Top };
            p.Controls.AddRange(controls);
            return p;
        }

        public static TableLayoutPanel Column()
        {
            return new TableLayoutPanel
            {
                ColumnCount = 1, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(Theme.S(18), Theme.S(14), Theme.S(18), Theme.S(8)),
            };
        }

        public static void AddRow(TableLayoutPanel t, Control c, int spacingAfter = 6)
        {
            c.Margin = new Padding(c.Margin.Left, c.Margin.Top, c.Margin.Right, Theme.S(spacingAfter));
            if (c is FlowLayoutPanel || c is TableLayoutPanel || c is TextBox || c is FlatCombo) c.Dock = DockStyle.Fill;
            t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(c, 0, t.RowCount - 1);
        }

        /// <summary>Footer bar with right aligned buttons (first = primary).</summary>
        public static Panel Footer(Control leftInfo, params Button[] buttons)
        {
            var bar = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(52), Tag = "chrome", Padding = new Padding(Theme.S(14), Theme.S(10), Theme.S(14), Theme.S(10)) };
            var line = new Panel { Dock = DockStyle.Top, Height = 1, Tag = "line" };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.LeftToRight, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Color.Transparent };
            flow.Controls.AddRange(buttons);
            bar.Controls.Add(flow);
            if (leftInfo != null) { leftInfo.Dock = DockStyle.Left; leftInfo.Padding = new Padding(0, Theme.S(6), 0, 0); bar.Controls.Add(leftInfo); }
            bar.Controls.Add(line);
            return bar;
        }

        public static void InitDialog(Form f, string title, int w, int h)
        {
            f.Text = title;
            f.Font = Theme.UiFont;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false; f.MinimizeBox = false;
            f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.CenterParent;
            f.AutoScaleMode = AutoScaleMode.None;
            f.ClientSize = new Size(Theme.S(w), Theme.S(h));
            try { f.Icon = AppIcon; } catch { }
        }

        private static Icon _icon;
        public static Icon AppIcon
        {
            get
            {
                if (_icon == null)
                {
                    try { _icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                }
                return _icon;
            }
        }

        public static void Error(IWin32Window owner, string msg) =>
            MessageBox.Show(owner, msg, "ArcBox", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        public static bool Confirm(IWin32Window owner, string msg) =>
            MessageBox.Show(owner, msg, "ArcBox", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

        public static void OpenFolder(string path, string select = null)
        {
            try
            {
                if (select != null && File.Exists(select))
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + select + "\"");
                else
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Write("open folder: " + ex.Message); }
        }

        public static void ShellOpen(string file)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
        }
    }

    /// <summary>System icons and type names by extension (cached).</summary>
    public static class ShellIcons
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon; public int iIcon; public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO psfi, uint cb, uint flags);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

        private const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_TYPENAME = 0x400, SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

        private static readonly Dictionary<string, Bitmap> Icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string Key(string name, bool dir)
        {
            if (dir) return "<dir>";
            string ext = Path.GetExtension(name);
            if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase) || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                return ext; // don't ask for per-file icons of files that are not on disk
            return string.IsNullOrEmpty(ext) ? "<none>" : ext;
        }

        public static Bitmap Icon(string name, bool dir)
        {
            string key = Key(name, dir);
            if (Icons.TryGetValue(key, out var b)) return b;
            b = null;
            try
            {
                var info = new SHFILEINFO();
                string probe = dir ? "folder" : (key.StartsWith("<") ? "file" : "file" + key);
                SHGetFileInfo(probe, dir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                    SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES | SHGFI_TYPENAME);
                if (info.hIcon != IntPtr.Zero)
                {
                    using (var ic = System.Drawing.Icon.FromHandle(info.hIcon)) b = ic.ToBitmap();
                    DestroyIcon(info.hIcon);
                }
                if (!string.IsNullOrEmpty(info.szTypeName)) Types[key] = info.szTypeName;
            }
            catch { }
            if (b == null) b = Glyphs.Get(dir ? "" : "", Theme.S(16), dir ? Theme.P.Folder : Theme.P.Sub, dir ? "D" : "F");
            Icons[key] = b;
            return b;
        }

        // ---------------- large icons (icon views)
        [DllImport("shell32.dll", EntryPoint = "#727")] private static extern int SHGetImageList(int list, ref Guid riid, out IntPtr ppv);
        [DllImport("comctl32.dll")] private static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, int flags);
        private const uint SHGFI_SYSICONINDEX = 0x4000;
        private static readonly Dictionary<string, Bitmap> Large = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Type icon for name, drawn centred on a transparent size x size square.</summary>
        public static Bitmap LargeIcon(string name, bool dir, int size)
        {
            string key = Key(name, dir) + "|" + size;
            if (Large.TryGetValue(key, out var cached)) return cached;
            Bitmap src = null;
            try
            {
                var info = new SHFILEINFO();
                string k = Key(name, dir);
                string probe = dir ? "folder" : (k.StartsWith("<") ? "file" : "file" + k);
                SHGetFileInfo(probe, dir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                    SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES);
                var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"); // IImageList
                int list = size > 48 ? 4 : 2; // SHIL_JUMBO (256) / SHIL_EXTRALARGE (48)
                if (SHGetImageList(list, ref iid, out var himl) == 0 && himl != IntPtr.Zero)
                {
                    IntPtr h = ImageList_GetIcon(himl, info.iIcon, 1 /* ILD_TRANSPARENT */);
                    if (h != IntPtr.Zero)
                    {
                        using (var ic = System.Drawing.Icon.FromHandle(h)) src = ic.ToBitmap();
                        DestroyIcon(h);
                    }
                }
            }
            catch { }
            if (src == null) src = Icon(name, dir);
            var b = new Bitmap(size, size);
            using (var g = Graphics.FromImage(b))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                var r = Trimmed(src);
                // small source icons are not blown up past twice their size
                float scale = Math.Min((float)size / Math.Max(r.Width, r.Height), r.Width <= 32 ? 2f : 100f);
                int w = (int)(r.Width * scale), hgt = (int)(r.Height * scale);
                g.DrawImage(src, new Rectangle((size - w) / 2, (size - hgt) / 2, w, hgt), r, GraphicsUnit.Pixel);
            }
            Large[key] = b;
            return b;
        }

        /// <summary>Bounds of the non-transparent pixels (jumbo icons of small-only types sit in the top-left corner).</summary>
        private static Rectangle Trimmed(Bitmap b)
        {
            int minX = b.Width, minY = b.Height, maxX = -1, maxY = -1;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                    if (b.GetPixel(x, y).A > 8) { if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y; }
            if (maxX < 0) return new Rectangle(0, 0, b.Width, b.Height);
            // keep it square-ish around the content so normal icons are not stretched
            int w = maxX - minX + 1, h = maxY - minY + 1;
            if (w > b.Width * 0.6 && h > b.Height * 0.6) return new Rectangle(0, 0, b.Width, b.Height);
            return new Rectangle(minX, minY, w, h);
        }

        public static string TypeName(string name, bool dir)
        {
            string key = Key(name, dir);
            if (!Types.ContainsKey(key)) Icon(name, dir);
            if (Types.TryGetValue(key, out var t)) return t;
            if (dir) return "파일 폴더";
            string ext = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
            return ext.Length > 0 ? ext + " 파일" : "파일";
        }
    }
}

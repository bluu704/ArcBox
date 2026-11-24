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

        public static ComboBox Combo(IEnumerable<string> items, int selected = 0, bool editable = false)
        {
            var c = new ComboBox { DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList, Font = Theme.UiFont, Dock = DockStyle.Fill };
            foreach (var i in items) c.Items.Add(i);
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
            if (c is FlowLayoutPanel || c is TableLayoutPanel || c is TextBox || c is ComboBox) c.Dock = DockStyle.Fill;
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

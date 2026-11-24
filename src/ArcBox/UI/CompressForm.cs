using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ArcBox.Core;

namespace ArcBox.UI
{
    /// <summary>"압축하기" dialog. Collects options; the caller runs the compression.</summary>
    public sealed class CompressForm : Form
    {
        private static readonly string[] MainFormats = { "zip", "7z", "tar", "tgz", "txz" };
        private static readonly string[] MoreFormats = { "tbz2", "gz", "bz2", "xz" };
        private static readonly string[] LevelNames = { "저장 (압축 안 함)", "가장 빠름", "빠름", "보통 (권장)", "높음", "최고 압축" };
        private static readonly int[] LevelValues = { 0, 1, 3, 5, 7, 9 };

        private readonly IList<string> _sources;
        private readonly TextBox _path;
        private readonly List<RadioButton> _formatButtons = new List<RadioButton>();
        private readonly Button _more;
        private readonly FlatCombo _level, _split;
        private readonly TextBox _pwd, _pwd2;
        private readonly CheckBox _showPwd, _utf8, _separate, _encNames;
        private readonly RadioButton _aes, _zipCrypto;
        private string _format;

        public string ArchivePath => _path.Text.Trim();
        public bool Separately => _separate.Checked;
        public CompressOptions Options { get; private set; }

        public CompressForm(IList<string> sources)
        {
            _sources = sources;
            Kit.InitDialog(this, "압축하기 — 항목 " + sources.Count + "개", 540, 550);
            if (Owner == null) StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            var s = AppSettings.Current;
            _format = s.LastFormat;

            var col = Kit.Column();
            Kit.AddRow(col, Kit.Lbl("압축 파일 이름", null, true), 2);
            _path = Kit.Txt(Ops.DefaultArchivePath(sources, _format));
            var browse = Kit.Btn("찾아보기…", false, 80);
            browse.Click += (o, e) => Browse();
            var pathRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathRow.Controls.Add(_path, 0, 0); pathRow.Controls.Add(browse, 1, 0);
            Kit.AddRow(col, pathRow, 10);

            Kit.AddRow(col, Kit.Lbl("형식", null, true), 2);
            var fmtRow = Kit.Row();
            foreach (var f in MainFormats)
            {
                var rb = new RadioButton
                {
                    Text = Label(f), Appearance = Appearance.Button, AutoSize = true, MinimumSize = new Size(Theme.S(58), Theme.S(30)), Padding = new Padding(Theme.S(8), 0, Theme.S(8), 0),
                    TextAlign = ContentAlignment.MiddleCenter, Tag = f, Font = Theme.UiFont, Margin = new Padding(0, 0, Theme.S(6), Theme.S(4))
                };
                rb.CheckedChanged += (o, e) => { if (rb.Checked) SetFormat((string)rb.Tag); Theme.Restyle(rb); };
                _formatButtons.Add(rb);
                fmtRow.Controls.Add(rb);
            }
            _more = Kit.Btn("더 보기 ▾", false, 80);
            _more.Margin = new Padding(0, 0, 0, Theme.S(4));
            var moreMenu = new ContextMenuStrip { Renderer = new Theme.FlatRenderer(), Font = Theme.UiFont };
            foreach (var f in MoreFormats)
            {
                var item = new ToolStripMenuItem(Label(f)) { Tag = f };
                item.Click += (o, e) => SetFormat((string)item.Tag);
                moreMenu.Items.Add(item);
            }
            _more.Click += (o, e) => moreMenu.Show(_more, new Point(0, _more.Height));
            fmtRow.Controls.Add(_more);
            Kit.AddRow(col, fmtRow, 10);

            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _level = Kit.Combo(LevelNames, Math.Max(0, Array.IndexOf(LevelValues, s.LastLevel)));
            if (_level.SelectedIndex < 0) _level.SelectedIndex = 3;
            _split = Kit.Combo(new[] { "분할 안 함", "10 MB", "100 MB", "700 MB (CD)", "1 GB", "4092 MB (FAT32)", "4.7 GB (DVD)" }, 0, true);
            _level.Margin = new Padding(0, 0, Theme.S(8), 0);
            grid.Controls.Add(Kit.Lbl("압축 레벨", null, true), 0, 0); grid.Controls.Add(Kit.Lbl("분할 압축 (직접 입력 가능: 50M, 2G)", null, true), 1, 0);
            grid.Controls.Add(_level, 0, 1); grid.Controls.Add(_split, 1, 1);
            Kit.AddRow(col, grid, 10);

            Kit.AddRow(col, Kit.Lbl("암호", null, true), 2);
            var pwdIn = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Font = Theme.UiFont, Margin = new Padding(0), Padding = new Padding(0) };
            pwdIn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            pwdIn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _pwd = Kit.Txt("", true); _pwd2 = Kit.Txt("", true);
            _pwd.Margin = new Padding(0, 0, Theme.S(8), Theme.S(6));
            SetCue(_pwd, "암호 (비워 두면 암호 없음)"); SetCue(_pwd2, "암호 확인");
            _showPwd = Kit.Chk("암호 표시");
            _aes = Kit.Radio("AES-256 (안전)", s.LastFormat != "zipcrypto");
            _zipCrypto = Kit.Radio("ZipCrypto (호환성)");
            _showPwd.CheckedChanged += (o, e) => { _pwd.UseSystemPasswordChar = _pwd2.UseSystemPasswordChar = !_showPwd.Checked; };
            pwdIn.Controls.Add(_pwd, 0, 0); pwdIn.Controls.Add(_pwd2, 1, 0);
            var pwdOpts = Kit.Row(_showPwd, _aes, _zipCrypto);
            pwdIn.Controls.Add(pwdOpts, 0, 1); pwdIn.SetColumnSpan(pwdOpts, 2);
            Kit.AddRow(col, pwdIn, 10);

            Kit.AddRow(col, Kit.Lbl("옵션", null, true), 2);
            _utf8 = Kit.Chk("파일 이름을 UTF-8로 저장 (macOS·리눅스 호환)", s.Utf8Names);
            _separate = Kit.Chk("항목마다 따로 압축", false);
            _separate.Enabled = sources.Count > 1;
            _encNames = Kit.Chk("파일 이름도 암호화 (7Z 전용)", true);
            Kit.AddRow(col, _utf8, 0);
            Kit.AddRow(col, _separate, 0);
            Kit.AddRow(col, _encNames, 0);

            var ok = Kit.Btn("압축 시작", true, 96);
            var cancel = Kit.Btn("취소");
            cancel.DialogResult = DialogResult.Cancel;
            ok.Click += (o, e) => Accept();
            AcceptButton = ok; CancelButton = cancel;
            var info = Kit.Lbl(Summary(), "sub");
            Controls.Add(col);
            Controls.Add(Kit.Footer(info, ok, cancel));
            Theme.Apply(this);
            SetFormat(_format, false);
        }

        private static string Label(string f)
        {
            switch (f)
            {
                case "tgz": return "TAR.GZ";
                case "txz": return "TAR.XZ";
                case "tbz2": return "TAR.BZ2";
                default: return f.ToUpperInvariant();
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        private static void SetCue(TextBox tb, string cue) => tb.HandleCreated += (s, e) => SendMessage(tb.Handle, 0x1501, (IntPtr)1, cue);

        private string Summary()
        {
            try
            {
                ulong size = 0; int files = 0;
                foreach (var src in _sources.Take(50))
                {
                    if (File.Exists(src)) { size += (ulong)new FileInfo(src).Length; files++; }
                    else if (Directory.Exists(src))
                    {
                        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).Take(20000))
                        {
                            try { size += (ulong)new FileInfo(f).Length; files++; } catch { }
                        }
                    }
                }
                return "원본 " + PathUtil.FormatSize(size) + " · 파일 " + files.ToString("N0") + "개";
            }
            catch { return ""; }
        }

        private void SetFormat(string f, bool updatePath = true)
        {
            _format = f;
            foreach (var b in _formatButtons) { b.Checked = (string)b.Tag == f; Theme.Restyle(b); }
            _more.Text = MoreFormats.Contains(f) ? Label(f) + " ▾" : "더 보기 ▾";
            if (updatePath)
            {
                string p = _path.Text.Trim();
                string dir = Path.GetDirectoryName(p);
                string stem = PathUtil.ArchiveBaseName(p);
                if (string.IsNullOrEmpty(dir)) dir = Path.GetDirectoryName(Ops.DefaultArchivePath(_sources, f));
                _path.Text = Path.Combine(dir ?? "", stem + CompressOptions.Extension(f));
            }
            bool zip = f == "zip", sevenZ = f == "7z";
            bool pwdOk = zip || sevenZ;
            _pwd.Enabled = _pwd2.Enabled = _showPwd.Enabled = pwdOk;
            _aes.Enabled = _zipCrypto.Enabled = zip;
            _encNames.Enabled = sevenZ;
            _utf8.Enabled = zip;
            _level.Enabled = f != "tar";
            Theme.Apply(_encNames); Theme.Apply(_utf8); Theme.Apply(_aes); Theme.Apply(_zipCrypto);
        }

        private void Browse()
        {
            using (var d = new SaveFileDialog { Title = "압축 파일 저장 위치", FileName = Path.GetFileName(_path.Text), OverwritePrompt = false })
            {
                try { d.InitialDirectory = Path.GetDirectoryName(_path.Text); } catch { }
                d.Filter = Label(_format) + " 파일|*" + CompressOptions.Extension(_format) + "|모든 파일|*.*";
                if (d.ShowDialog(this) == DialogResult.OK) _path.Text = d.FileName;
            }
        }

        private void Accept()
        {
            string path = ArchivePath;
            if (path.Length == 0) { Kit.Error(this, "압축 파일 이름을 입력하세요."); return; }
            try { path = Path.GetFullPath(path); }
            catch { Kit.Error(this, "압축 파일 이름이 올바르지 않습니다."); return; }
            if (_pwd.Enabled && _pwd.Text != _pwd2.Text) { Kit.Error(this, "암호와 암호 확인이 다릅니다."); _pwd2.Focus(); return; }
            long vol = 0;
            if (_split.SelectedIndex != 0)
            {
                string t = _split.Text.Split('(')[0];
                vol = CompressOptions.ParseSize(t);
                if (vol <= 0) { Kit.Error(this, "분할 크기를 이해하지 못했습니다. 예: 100M, 2G"); return; }
            }
            if (CompressOptions.IsSingleStream(_format) && (_sources.Count != 1 || Directory.Exists(_sources[0])) && !_separate.Checked)
            {
                Kit.Error(this, Label(_format) + " 형식은 파일 하나만 압축할 수 있습니다.\nTAR." + Label(_format) + " 형식을 선택하세요.");
                return;
            }
            if (!_separate.Checked && File.Exists(path) && !Kit.Confirm(this, "같은 이름의 파일이 있습니다. 덮어쓸까요?\n" + path)) return;

            Options = new CompressOptions
            {
                Format = _format,
                Level = _level.Enabled ? LevelValues[Math.Max(0, _level.SelectedIndex)] : 0,
                Password = _pwd.Enabled && _pwd.Text.Length > 0 ? _pwd.Text : null,
                EncryptFileNames = _encNames.Enabled && _encNames.Checked,
                ZipEncryption = _zipCrypto.Checked ? "ZipCrypto" : "AES256",
                Utf8Names = _utf8.Checked,
                VolumeSize = vol,
                Solid = true
            };
            _path.Text = path;
            var s = AppSettings.Current;
            s.LastFormat = _format; s.LastLevel = Options.Level; s.Utf8Names = _utf8.Checked;
            s.Save();
            DialogResult = DialogResult.OK;
        }
    }
}

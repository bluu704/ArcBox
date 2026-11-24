using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using ArcBox.Core;

namespace ArcBox.UI
{
    /// <summary>Text helpers shared by the text viewer and the preview pane.</summary>
    public static class TextUtil
    {
        private static readonly HashSet<string> Exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".markdown", ".log", ".json", ".xml", ".csv", ".tsv", ".ini", ".cfg", ".conf", ".yaml", ".yml", ".toml",
            ".html", ".htm", ".css", ".js", ".ts", ".jsx", ".tsx", ".py", ".cs", ".java", ".kt", ".c", ".cpp", ".cc", ".h", ".hpp",
            ".go", ".rs", ".rb", ".php", ".lua", ".pl", ".swift", ".bat", ".cmd", ".ps1", ".sh", ".sql", ".srt", ".smi", ".ass",
            ".vtt", ".nfo", ".diz", ".properties", ".gitignore", ".editorconfig", ".reg", ".inf", ".sub", ".cue", ".m3u", ".svg",
        };
        private static readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "readme", "license", "licence", "copying", "changelog", "authors", "notice", "makefile", "dockerfile",
        };

        public static bool IsText(string name)
        {
            string ext = Path.GetExtension(name);
            return Exts.Contains(ext) || (ext.Length == 0 && Names.Contains(name));
        }

        /// <summary>Encodings offered in the viewer (0 = automatic).</summary>
        public static readonly (int cp, string label)[] Choices =
        {
            (0, "자동"), (65001, "UTF-8"), (949, "한국어 (CP949)"), (1200, "UTF-16 LE"), (932, "일본어 (Shift-JIS)"), (936, "중국어 간체 (GBK)"), (1252, "서유럽 (1252)"),
        };

        public static string Decode(byte[] data, int codePage, out string used)
        {
            if (codePage != 0)
            {
                var enc = Encoding.GetEncoding(codePage);
                used = enc.WebName;
                return enc.GetString(data);
            }
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) { used = "UTF-8"; return Encoding.UTF8.GetString(data, 3, data.Length - 3); }
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) { used = "UTF-16 LE"; return Encoding.Unicode.GetString(data, 2, data.Length - 2); }
            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) { used = "UTF-16 BE"; return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2); }
            try
            {
                // strict UTF-8; a cut-off last character (preview pane reads only the start) is allowed
                var strict = new UTF8Encoding(false, true);
                int n = data.Length;
                for (int back = 0; back < 4; back++)
                {
                    try { used = "UTF-8"; return strict.GetString(data, 0, n - back); }
                    catch (DecoderFallbackException) when (back < 3 && n - back > 0) { }
                }
            }
            catch { }
            try { used = "CP949"; return Encoding.GetEncoding(949).GetString(data); }
            catch { used = Encoding.Default.WebName; return Encoding.Default.GetString(data); }
        }

        /// <summary>Windows line endings so the TextBox shows line breaks.</summary>
        public static string Lines(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n").Replace("\0", " ");

        public static Font Mono
        {
            get
            {
                foreach (var f in new[] { "D2Coding", "Cascadia Mono", "Consolas" })
                {
                    try { using (var test = new Font(f, 10f)) if (test.Name == f) return new Font(f, 10f); } catch { }
                }
                return new Font(FontFamily.GenericMonospace, 10f);
            }
        }
    }

    /// <summary>Read-only viewer for text files inside an archive.</summary>
    public sealed class TextViewerForm : Form
    {
        private const ulong MaxSize = 32UL * 1024 * 1024;
        private readonly ArchiveReader _reader;
        private readonly ArchiveEntry _entry;
        private readonly TextBox _text;
        private readonly Label _info;
        private readonly FlatCombo _encoding;
        private readonly CheckBox _wrap;
        private readonly TextBox _find;
        private byte[] _data;

        public TextViewerForm(ArchiveReader reader, ArchiveEntry entry)
        {
            _reader = reader; _entry = entry;
            Text = entry.Name + " — ArcBox";
            Font = Theme.UiFont;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.S(900), Theme.S(640));
            MinimumSize = new Size(Theme.S(420), Theme.S(300));
            ShowInTaskbar = false;
            KeyPreview = true;
            try { Icon = Kit.AppIcon; } catch { }

            var top = new Panel { Dock = DockStyle.Top, Height = Theme.S(48), Tag = "chrome", Padding = new Padding(Theme.S(10), Theme.S(8), Theme.S(10), Theme.S(8)) };
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.Transparent };
            _encoding = new FlatCombo(false) { Width = Theme.S(160), Margin = new Padding(0, 0, Theme.S(10), 0) };
            foreach (var c in TextUtil.Choices) _encoding.Items.Add(c.label);
            _encoding.SelectedIndex = 0;
            _encoding.SelectedIndexChanged += (s, e) => Render();
            _wrap = Kit.Chk("자동 줄 바꿈", true);
            _wrap.CheckedChanged += (s, e) => _text.WordWrap = _wrap.Checked;
            _find = new TextBox { Width = Theme.S(180), Font = Theme.UiFont, Margin = new Padding(Theme.S(6), Theme.S(1), Theme.S(4), 0) };
            var findBtn = Kit.Btn("찾기", false, 56);
            findBtn.Margin = new Padding(0);
            findBtn.Click += (s, e) => FindNext();
            _find.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { FindNext(); e.SuppressKeyPress = true; } };
            row.Controls.AddRange(new Control[] { Kit.Lbl("인코딩", "sub"), _encoding, _wrap, _find, findBtn });
            foreach (Control c in row.Controls) if (c is Label l) l.Margin = new Padding(0, Theme.S(5), Theme.S(6), 0);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
            var save = Kit.Btn("풀기…", false, 72);
            save.Click += (s, e) => SaveAs();
            var close = Kit.Btn("닫기", true, 64);
            close.Click += (s, e) => Close();
            buttons.Controls.AddRange(new Control[] { save, close });
            top.Controls.Add(row);
            top.Controls.Add(buttons);

            _text = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = true,
                BorderStyle = BorderStyle.None, Font = TextUtil.Mono, MaxLength = 0, HideSelection = false,
            };
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(26), Tag = "chrome", Padding = new Padding(Theme.S(10), 0, Theme.S(10), 0) };
            _info = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Tag = "sub" };
            bottom.Controls.Add(_info);
            var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(8), Theme.S(6), 0, 0) };
            pad.Controls.Add(_text);

            Controls.Add(pad);
            Controls.Add(bottom);
            Controls.Add(top);
            Theme.Apply(this);
            pad.BackColor = _text.BackColor = Theme.P.Bg;
            _text.ForeColor = Theme.P.Text;
            Shown += async (s, e) => await LoadText();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == (Keys.Control | Keys.F)) { _find.Focus(); _find.SelectAll(); return true; }
            if (keyData == Keys.F3) { FindNext(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private async Task LoadText()
        {
            if (_entry.Size > MaxSize) { _info.Text = "파일이 너무 커서(" + PathUtil.FormatSize(_entry.Size) + ") 표시할 수 없습니다. 풀어서 여세요."; return; }
            _info.Text = "불러오는 중…";
            Cursor = Cursors.AppStarting;
            try
            {
                var ui = new PromptUI(this);
                _data = await Task.Run(() => _reader.ReadEntryBytes(_entry, ui));
            }
            catch (Exception ex) { _info.Text = "읽지 못했습니다: " + ex.Message; }
            finally { Cursor = Cursors.Default; }
            if (_data != null) Render();
            else if (_info.Text == "불러오는 중…") _info.Text = "취소했습니다.";
        }

        private void Render()
        {
            if (_data == null) return;
            int cp = TextUtil.Choices[Math.Max(0, _encoding.SelectedIndex)].cp;
            string used;
            try { _text.Text = TextUtil.Lines(TextUtil.Decode(_data, cp, out used)); }
            catch (Exception ex) { _text.Text = ""; used = "?"; Kit.Error(this, ex.Message); }
            _text.Select(0, 0);
            _info.Text = _entry.Path + " · " + PathUtil.FormatSize(_entry.Size) + " · " + _text.Lines.Length.ToString("N0") + "줄 · " + used;
        }

        private void FindNext()
        {
            string q = _find.Text;
            if (q.Length == 0) return;
            int from = _text.SelectionStart + _text.SelectionLength;
            int i = _text.Text.IndexOf(q, Math.Min(from, _text.TextLength), StringComparison.CurrentCultureIgnoreCase);
            if (i < 0) i = _text.Text.IndexOf(q, 0, StringComparison.CurrentCultureIgnoreCase); // wrap around
            if (i < 0) { System.Media.SystemSounds.Beep.Play(); return; }
            _text.Select(i, q.Length);
            _text.ScrollToCaret();
            _text.Focus();
        }

        private void SaveAs()
        {
            if (_data == null) return;
            using (var d = new SaveFileDialog { FileName = _entry.Name, Title = "파일 저장", Filter = "모든 파일|*.*" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllBytes(d.FileName, _data);
                    if (_entry.MTime.HasValue) File.SetLastWriteTimeUtc(d.FileName, _entry.MTime.Value);
                }
                catch (Exception ex) { Kit.Error(this, "저장하지 못했습니다.\n" + ex.Message); }
            }
        }

        /// <summary>Only password prompts are shown.</summary>
        public sealed class PromptUI : IOperationUI
        {
            private readonly Form _owner;
            public PromptUI(Form owner) { _owner = owner; }
            public bool IsCancelled => false;
            public void WaitIfPaused() { }
            public void SetStage(string text) { }
            public void SetTotal(ulong total) { }
            public void SetCompleted(ulong done) { }
            public void SetCurrentFile(string name) { }
            public string AskPassword(string archiveName, bool retry) =>
                (string)_owner.Invoke((Func<string>)(() => PasswordDialog.Ask(_owner, archiveName, retry)));
            public OverwriteAction AskOverwrite(string targetPath, ArchiveEntry entry) => OverwriteAction.Overwrite;
        }
    }
}

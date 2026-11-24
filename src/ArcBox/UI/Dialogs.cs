using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ArcBox.Core;

namespace ArcBox.UI
{
    public static class PasswordDialog
    {
        public static string Ask(IWin32Window owner, string archiveName, bool retry)
        {
            using (var f = new Form())
            {
                Kit.InitDialog(f, "암호 입력", 420, 200);
                var col = Kit.Column();
                Kit.AddRow(col, Kit.Lbl(retry ? "암호가 틀렸습니다. 다시 입력하세요." : "암호가 걸린 압축 파일입니다.", retry ? "accent" : null, true), 2);
                Kit.AddRow(col, Kit.Lbl(archiveName, "sub"), 6);
                var tb = Kit.Txt("", true);
                Kit.AddRow(col, tb, 4);
                var show = Kit.Chk("암호 표시");
                show.CheckedChanged += (s, e) => tb.UseSystemPasswordChar = !show.Checked;
                Kit.AddRow(col, show, 0);
                var ok = Kit.Btn("확인", true); var cancel = Kit.Btn("취소");
                ok.DialogResult = DialogResult.OK; cancel.DialogResult = DialogResult.Cancel;
                f.AcceptButton = ok; f.CancelButton = cancel;
                f.Controls.Add(col);
                f.Controls.Add(Kit.Footer(null, ok, cancel));
                Theme.Apply(f);
                f.Shown += (s, e) => tb.Focus();
                if (owner == null) f.StartPosition = FormStartPosition.CenterScreen;
                return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
            }
        }
    }

    public static class OverwriteDialog
    {
        public static OverwriteAction Ask(IWin32Window owner, string target, ArchiveEntry entry, out bool applyAll)
        {
            using (var f = new Form())
            {
                Kit.InitDialog(f, "파일 덮어쓰기", 500, 290);
                var col = Kit.Column();
                Kit.AddRow(col, Kit.Lbl("같은 이름의 파일이 이미 있습니다.", null, true), 4);
                Kit.AddRow(col, new Label { Text = target, AutoSize = false, Height = Theme.S(36), Dock = DockStyle.Fill, Font = Theme.UiFont, AutoEllipsis = true, Tag = "sub" }, 6);
                string existing = "";
                try { var fi = new FileInfo(target); existing = PathUtil.FormatSize((ulong)fi.Length) + " · " + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"); } catch { }
                string incoming = PathUtil.FormatSize(entry.Size) + (entry.MTime.HasValue ? " · " + entry.MTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "");
                Kit.AddRow(col, Kit.Lbl("기존 파일:  " + existing), 0);
                Kit.AddRow(col, Kit.Lbl("새 파일:     " + incoming), 6);
                var all = Kit.Chk("나머지 파일에도 모두 적용");
                Kit.AddRow(col, all, 0);
                var result = OverwriteAction.Cancel;
                Button B(string t, OverwriteAction a, bool primary = false)
                {
                    var b = Kit.Btn(t, primary, 80);
                    b.Click += (s, e) => { result = a; f.DialogResult = DialogResult.OK; };
                    return b;
                }
                var cancel = B("취소", OverwriteAction.Cancel);
                f.CancelButton = cancel;
                f.Controls.Add(col);
                f.Controls.Add(Kit.Footer(null, B("덮어쓰기", OverwriteAction.Overwrite, true), B("건너뛰기", OverwriteAction.Skip), B("이름 바꾸기", OverwriteAction.Rename), cancel));
                Theme.Apply(f);
                f.ShowDialog(owner);
                applyAll = all.Checked;
                return result;
            }
        }
    }

    public static class InputDialog
    {
        public static string Ask(IWin32Window owner, string title, string prompt, string initial)
        {
            using (var f = new Form())
            {
                Kit.InitDialog(f, title, 420, 170);
                var col = Kit.Column();
                Kit.AddRow(col, Kit.Lbl(prompt), 4);
                var tb = Kit.Txt(initial);
                Kit.AddRow(col, tb, 0);
                var ok = Kit.Btn("확인", true); var cancel = Kit.Btn("취소");
                ok.DialogResult = DialogResult.OK; cancel.DialogResult = DialogResult.Cancel;
                f.AcceptButton = ok; f.CancelButton = cancel;
                f.Controls.Add(col);
                f.Controls.Add(Kit.Footer(null, ok, cancel));
                Theme.Apply(f);
                f.Shown += (s, e) =>
                {
                    tb.Focus();
                    int dot = tb.Text.LastIndexOf('.');
                    if (dot > 0) tb.Select(0, dot); else tb.SelectAll();
                };
                return f.ShowDialog(owner) == DialogResult.OK ? tb.Text.Trim() : null;
            }
        }
    }

    /// <summary>Key/value list (archive info) or a list of messages (errors).</summary>
    public static class ListDialog
    {
        public static void Show(IWin32Window owner, string title, string heading, IEnumerable<KeyValuePair<string, string>> rows, int w = 560, int h = 420)
        {
            using (var f = new Form())
            {
                Kit.InitDialog(f, title, w, h);
                f.FormBorderStyle = FormBorderStyle.Sizable; f.MinimizeBox = false;
                var lv = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.None, BorderStyle = BorderStyle.None, Font = Theme.UiFont };
                bool twoCols = rows.Any(r => !string.IsNullOrEmpty(r.Key));
                if (twoCols) lv.Columns.Add("", Theme.S(130));
                lv.Columns.Add("", Theme.S(w - (twoCols ? 170 : 40)));
                foreach (var r in rows)
                {
                    var it = twoCols ? new ListViewItem(new[] { r.Key, r.Value }) : new ListViewItem(r.Value);
                    lv.Items.Add(it);
                }
                var copy = Kit.Btn("복사");
                copy.Click += (s, e) =>
                {
                    try { Clipboard.SetText(string.Join("\r\n", rows.Select(r => string.IsNullOrEmpty(r.Key) ? r.Value : r.Key + ": " + r.Value))); } catch { }
                };
                var ok = Kit.Btn("닫기", true);
                ok.DialogResult = DialogResult.OK;
                f.AcceptButton = ok; f.CancelButton = ok;
                var head = new Panel { Dock = DockStyle.Top, Height = Theme.S(40), Padding = new Padding(Theme.S(16), Theme.S(12), Theme.S(16), 0) };
                head.Controls.Add(Kit.Lbl(heading, null, true));
                var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(12), Theme.S(4), Theme.S(12), Theme.S(8)) };
                body.Controls.Add(lv);
                f.Controls.Add(body);
                f.Controls.Add(head);
                f.Controls.Add(Kit.Footer(null, copy, ok));
                Theme.Apply(f);
                f.ShowDialog(owner);
            }
        }

        public static void Result(IWin32Window owner, string title, OpResult r, string success)
        {
            if (r.Cancelled && !r.HasErrors) return;
            if (!r.HasErrors && r.Warnings.Count == 0)
            {
                if (success != null) Kit.Info(owner, success);
                return;
            }
            var rows = new List<KeyValuePair<string, string>>();
            rows.AddRange(r.Errors.Select(e => new KeyValuePair<string, string>("", "오류: " + e)));
            rows.AddRange(r.Warnings.Select(e => new KeyValuePair<string, string>("", "주의: " + e)));
            string heading = r.HasErrors ? "문제가 " + r.Errors.Count + "건 발생했습니다." : "완료했지만 확인할 내용이 있습니다.";
            if (r.FilesProcessed > 0) heading += "  (성공 " + r.FilesProcessed + "개)";
            Show(owner, title, heading, rows);
        }
    }

    public static class HashDialog
    {
        public static void Show(IWin32Window owner, IList<string> files)
        {
            var rows = new List<KeyValuePair<string, string>>();
            var pf = ProgressForm.Run(owner, "해시 계산", files.Count == 1 ? Path.GetFileName(files[0]) : files.Count + "개 파일", null, false, ui =>
            {
                ulong total = 0;
                foreach (var f in files) if (File.Exists(f)) total += (ulong)new FileInfo(f).Length;
                ui.SetTotal(total);
                ulong done = 0;
                foreach (var f in files)
                {
                    if (!File.Exists(f)) continue;
                    ui.SetCurrentFile(Path.GetFileName(f));
                    using (var md5 = MD5.Create())
                    using (var sha1 = SHA1.Create())
                    using (var sha256 = SHA256.Create())
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
                    {
                        var buf = new byte[1 << 20];
                        uint crc = 0xFFFFFFFF;
                        int n;
                        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (ui.IsCancelled) throw new OperationCanceledException();
                            ui.WaitIfPaused();
                            md5.TransformBlock(buf, 0, n, null, 0);
                            sha1.TransformBlock(buf, 0, n, null, 0);
                            sha256.TransformBlock(buf, 0, n, null, 0);
                            crc = Crc32.Update(crc, buf, n);
                            done += (ulong)n;
                            ui.SetCompleted(done);
                        }
                        md5.TransformFinalBlock(buf, 0, 0); sha1.TransformFinalBlock(buf, 0, 0); sha256.TransformFinalBlock(buf, 0, 0);
                        lock (rows)
                        {
                            if (rows.Count > 0) rows.Add(new KeyValuePair<string, string>("", ""));
                            rows.Add(new KeyValuePair<string, string>("파일", Path.GetFileName(f)));
                            rows.Add(new KeyValuePair<string, string>("CRC32", (crc ^ 0xFFFFFFFF).ToString("X8")));
                            rows.Add(new KeyValuePair<string, string>("MD5", Hex(md5.Hash)));
                            rows.Add(new KeyValuePair<string, string>("SHA-1", Hex(sha1.Hash)));
                            rows.Add(new KeyValuePair<string, string>("SHA-256", Hex(sha256.Hash)));
                        }
                    }
                }
            });
            if (pf.Error != null) { Kit.Error(owner, pf.Error.Message); return; }
            if (pf.IsCancelled) return;
            ListDialog.Show(owner, "해시 값", "해시 값", rows, 720, 360);
        }

        private static string Hex(byte[] b) => BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
    }

    public static class Crc32
    {
        private static readonly uint[] Table = Build();
        private static uint[] Build()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }
        public static uint Update(uint crc, byte[] buf, int n)
        {
            for (int i = 0; i < n; i++) crc = Table[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}

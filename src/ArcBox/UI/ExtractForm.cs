using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ArcBox.Core;

namespace ArcBox.UI
{
    /// <summary>"압축 풀기" options dialog.</summary>
    public sealed class ExtractForm : Form
    {
        private readonly ComboBox _dest;
        private readonly CheckBox _smart, _openAfter;
        private readonly ComboBox _overwrite;
        private static readonly string[] OwKeys = { "ask", "overwrite", "skip", "rename" };

        public string Destination => _dest.Text.Trim();
        public bool SmartFolder => _smart.Checked;
        public OverwriteMode Overwrite => AppSettings.Current.Overwrite;

        public ExtractForm(string archivePath, bool selectionOnly)
        {
            Kit.InitDialog(this, "압축 풀기 — " + Path.GetFileName(archivePath), 520, 300);
            ShowInTaskbar = true;
            var s = AppSettings.Current;
            var col = Kit.Column();
            Kit.AddRow(col, Kit.Lbl(selectionOnly ? "선택한 항목을 풀 위치" : "압축을 풀 위치", null, true), 2);
            _dest = Kit.Combo(new string[0], 0, true);
            _dest.Items.Add(Path.GetDirectoryName(Path.GetFullPath(archivePath)));
            foreach (var d in s.RecentDest) if (!_dest.Items.Contains(d)) _dest.Items.Add(d);
            foreach (var sf in new[] { Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments })
            {
                string p = Environment.GetFolderPath(sf);
                if (!string.IsNullOrEmpty(p) && !_dest.Items.Contains(p)) _dest.Items.Add(p);
            }
            _dest.SelectedIndex = 0;
            var browse = Kit.Btn("찾아보기…", false, 80);
            browse.Click += (o, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "압축을 풀 폴더를 선택하세요", SelectedPath = Destination, ShowNewFolderButton = true })
                    if (d.ShowDialog(this) == DialogResult.OK) _dest.Text = d.SelectedPath;
            };
            var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(_dest, 0, 0); row.Controls.Add(browse, 1, 0);
            Kit.AddRow(col, row, 10);

            _smart = Kit.Chk("압축 파일 이름으로 폴더 만들기 (안에 폴더가 하나뿐이면 만들지 않음)", s.SmartFolder);
            _smart.Visible = !selectionOnly;
            if (!selectionOnly) Kit.AddRow(col, _smart, 4);

            var owRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            owRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            owRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _overwrite = Kit.Combo(new[] { "물어보기", "항상 덮어쓰기", "건너뛰기", "이름 바꿔서 저장" }, Math.Max(0, Array.IndexOf(OwKeys, s.OverwriteMode)));
            _overwrite.Dock = DockStyle.None; _overwrite.Width = Theme.S(180);
            owRow.Controls.Add(Kit.Lbl("같은 이름의 파일이 있으면:  "), 0, 0); owRow.Controls.Add(_overwrite, 1, 0);
            Kit.AddRow(col, owRow, 4);
            _openAfter = Kit.Chk("완료 후 폴더 열기", s.OpenFolderAfterExtract);
            Kit.AddRow(col, _openAfter, 0);

            var ok = Kit.Btn("풀기", true, 96);
            var cancel = Kit.Btn("취소");
            cancel.DialogResult = DialogResult.Cancel;
            ok.Click += (o, e) =>
            {
                if (Destination.Length == 0) { Kit.Error(this, "풀 위치를 입력하세요."); return; }
                try { Path.GetFullPath(Destination); } catch { Kit.Error(this, "폴더 경로가 올바르지 않습니다."); return; }
                s.SmartFolder = _smart.Checked;
                s.OverwriteMode = OwKeys[Math.Max(0, _overwrite.SelectedIndex)];
                s.OpenFolderAfterExtract = _openAfter.Checked;
                s.AddRecentDest(Path.GetFullPath(Destination));
                DialogResult = DialogResult.OK;
            };
            AcceptButton = ok; CancelButton = cancel;
            Controls.Add(col);
            Controls.Add(Kit.Footer(null, ok, cancel));
            Theme.Apply(this);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using ArcBox.Core;

namespace ArcBox.UI
{
    /// <summary>Shows images inside an archive without extracting them to the user's folders.</summary>
    public sealed class ImageViewerForm : Form
    {
        private readonly ArchiveReader _reader;
        private readonly List<ArchiveEntry> _items;
        private int _index;
        private readonly Panel _top, _bottom, _scroll;
        private readonly PictureBox _pic;
        private readonly Label _name, _info, _message;
        private readonly Button _prev, _next, _fit, _extract;
        private bool _fitMode = true;
        private Image _image;
        private int _loadToken;
        private readonly Dictionary<int, byte[]> _cache = new Dictionary<int, byte[]>();

        public ImageViewerForm(ArchiveReader reader, List<ArchiveEntry> items, int start)
        {
            _reader = reader; _items = items; _index = Math.Max(0, Math.Min(start, items.Count - 1));
            Text = "이미지 미리보기 — ArcBox";
            Font = Theme.UiFont;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.S(960), Theme.S(680));
            MinimumSize = new Size(Theme.S(420), Theme.S(300));
            KeyPreview = true;
            ShowInTaskbar = false;
            try { Icon = Kit.AppIcon; } catch { }

            _top = new Panel { Dock = DockStyle.Top, Height = Theme.S(44), Tag = "chrome", Padding = new Padding(Theme.S(10), Theme.S(7), Theme.S(10), Theme.S(7)) };
            _name = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.UiBold };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
            _prev = Kit.Btn("◀ 이전", false, 72); _next = Kit.Btn("다음 ▶", false, 72);
            _fit = Kit.Btn("원본 크기", false, 84); _extract = Kit.Btn("풀기…", false, 72);
            var close = Kit.Btn("닫기", true, 64);
            _prev.Click += (s, e) => Go(-1);
            _next.Click += (s, e) => Go(1);
            _fit.Click += (s, e) => ToggleFit();
            _extract.Click += (s, e) => ExtractCurrent();
            close.Click += (s, e) => Close();
            buttons.Controls.AddRange(new Control[] { _prev, _next, _fit, _extract, close });
            _top.Controls.Add(_name);
            _top.Controls.Add(buttons);

            _bottom = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(28), Tag = "chrome", Padding = new Padding(Theme.S(10), 0, Theme.S(10), 0) };
            _info = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Tag = "sub" };
            _bottom.Controls.Add(_info);

            _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Tag = "side" };
            _pic = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, BackColor = Color.Transparent };
            _message = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Visible = false, Font = Theme.UiFont };
            _scroll.Controls.Add(_pic);
            _scroll.Controls.Add(_message);
            _pic.DoubleClick += (s, e) => ToggleFit();
            _scroll.MouseWheel += (s, e) => { if (_fitMode) Go(e.Delta < 0 ? 1 : -1); };
            _pic.MouseWheel += (s, e) => { if (_fitMode) Go(e.Delta < 0 ? 1 : -1); };

            Controls.Add(_scroll);
            Controls.Add(_bottom);
            Controls.Add(_top);
            Theme.Apply(this);
            _scroll.BackColor = Theme.P.Dark ? Color.FromArgb(24, 24, 24) : Color.FromArgb(240, 240, 240);

            Shown += (s, e) => ShowImage(_index);
            FormClosed += (s, e) => { _pic.Image = null; _image?.Dispose(); };
        }

        // Arrow keys and Space would otherwise move focus between the buttons
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var args = new KeyEventArgs(keyData);
            OnKey(this, args);
            return args.Handled || base.ProcessCmdKey(ref msg, keyData);
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Modifiers != Keys.None) return;
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.PageUp: case Keys.Back: Go(-1); break;
                case Keys.Right: case Keys.PageDown: case Keys.Space: Go(1); break;
                case Keys.Home: ShowImage(0); break;
                case Keys.End: ShowImage(_items.Count - 1); break;
                case Keys.Escape: Close(); break;
                case Keys.Z: case Keys.Enter: ToggleFit(); break;
                default: return;
            }
            e.Handled = true;
        }

        private void Go(int d)
        {
            int n = _index + d;
            if (n < 0 || n >= _items.Count) return;
            ShowImage(n);
        }

        private void ToggleFit()
        {
            _fitMode = !_fitMode;
            _fit.Text = _fitMode ? "원본 크기" : "화면 맞춤";
            ApplySizeMode();
        }

        private void ApplySizeMode()
        {
            if (_fitMode || _image == null)
            {
                _pic.Dock = DockStyle.Fill;
                _pic.SizeMode = PictureBoxSizeMode.Zoom;
            }
            else
            {
                _pic.Dock = DockStyle.None;
                _pic.SizeMode = PictureBoxSizeMode.AutoSize;
                _pic.Location = new Point(Math.Max(0, (_scroll.ClientSize.Width - _image.Width) / 2), Math.Max(0, (_scroll.ClientSize.Height - _image.Height) / 2));
            }
        }

        private async void ShowImage(int index)
        {
            _index = index;
            int token = ++_loadToken;
            var e = _items[index];
            _name.Text = e.Name;
            _prev.Enabled = index > 0;
            _next.Enabled = index < _items.Count - 1;
            _info.Text = (index + 1) + " / " + _items.Count + " · 불러오는 중…";
            Cursor = Cursors.AppStarting;
            byte[] data = null;
            string error = null;
            try
            {
                if (!_cache.TryGetValue(e.Index, out data))
                {
                    if (e.Size > 512UL * 1024 * 1024) throw new InvalidOperationException("이미지 파일이 너무 커서 미리볼 수 없습니다.");
                    var ui = new ViewerUI(this);
                    data = await Task.Run(() => _reader.ReadEntryBytes(e, ui));
                    if (data != null)
                    {
                        if (_cache.Count > 8) _cache.Clear();
                        _cache[e.Index] = data;
                    }
                }
            }
            catch (Exception ex) { error = ex.Message; }
            if (token != _loadToken || IsDisposed) return;
            Cursor = Cursors.Default;

            Image img = null;
            if (data != null && error == null)
            {
                try { img = ImageDecode.Decode(data, e.Name); }
                catch (Exception ex) { error = ex.Message; }
            }
            var old = _image;
            _image = img;
            _pic.Image = img;
            old?.Dispose();
            _message.Visible = img == null;
            _pic.Visible = img != null;
            _message.Text = img == null ? (error ?? "이미지를 불러오지 못했습니다.") : "";
            _message.ForeColor = Theme.P.Sub;
            _info.Text = (index + 1) + " / " + _items.Count + " · " + e.Path +
                         (img != null ? " · " + img.Width + "×" + img.Height : "") + " · " + PathUtil.FormatSize(e.Size);
            ApplySizeMode();
        }

        private void ExtractCurrent()
        {
            var e = _items[_index];
            using (var d = new SaveFileDialog { FileName = e.Name, Title = "이미지 저장" })
            {
                d.Filter = "현재 형식|*" + Path.GetExtension(e.Name) + "|모든 파일|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    if (!_cache.TryGetValue(e.Index, out var data)) data = Task.Run(() => _reader.ReadEntryBytes(e, new ViewerUI(this))).Result;
                    if (data == null) return;
                    File.WriteAllBytes(d.FileName, data);
                    if (e.MTime.HasValue) File.SetLastWriteTimeUtc(d.FileName, e.MTime.Value);
                }
                catch (Exception ex) { Kit.Error(this, "저장하지 못했습니다.\n" + (ex.InnerException ?? ex).Message); }
            }
        }

        /// <summary>Minimal operation UI: only password prompts are shown.</summary>
        private sealed class ViewerUI : IOperationUI
        {
            private readonly Form _owner;
            public ViewerUI(Form owner) { _owner = owner; }
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

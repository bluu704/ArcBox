using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
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
        private readonly Button _prev, _next, _fit, _extract, _rotL, _rotR, _slide, _full;
        private readonly ThumbStrip _strip;
        private readonly Timer _slideTimer = new Timer { Interval = 3000 };
        private bool _fullScreen;
        private FormWindowState _savedState;
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
            _rotL = Kit.Btn("↺", false, 36); _rotR = Kit.Btn("↻", false, 36);
            _slide = Kit.Btn("▶ 슬라이드쇼", false, 96); _full = Kit.Btn("전체 화면", false, 76);
            _rotL.Click += (s, e) => Rotate(false);
            _rotR.Click += (s, e) => Rotate(true);
            _slide.Click += (s, e) => ToggleSlideshow();
            _full.Click += (s, e) => ToggleFullScreen();
            var tips = Kit.Tip();
            tips.SetToolTip(_rotL, "왼쪽으로 회전 (Shift+R)"); tips.SetToolTip(_rotR, "오른쪽으로 회전 (R)");
            tips.SetToolTip(_slide, "슬라이드쇼 (F5)"); tips.SetToolTip(_full, "전체 화면 (F11)");
            _slideTimer.Tick += (s, e) => ShowImage(_index + 1 < _items.Count ? _index + 1 : 0);
            var close = Kit.Btn("닫기", true, 64);
            _prev.Click += (s, e) => Go(-1);
            _next.Click += (s, e) => Go(1);
            _fit.Click += (s, e) => ToggleFit();
            _extract.Click += (s, e) => ExtractCurrent();
            close.Click += (s, e) => Close();
            buttons.Controls.AddRange(new Control[] { _prev, _next, _rotL, _rotR, _fit, _slide, _full, _extract, close });
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
            _pic.DoubleClick += (s, e) => { if (_fullScreen) ToggleFullScreen(); else ToggleFit(); };
            _strip = new ThumbStrip(reader, items) { Dock = DockStyle.Bottom, Height = Theme.S(86), Visible = items.Count > 1 };
            _strip.Picked += i => ShowImage(i);
            _scroll.MouseWheel += (s, e) => { if (_fitMode) Go(e.Delta < 0 ? 1 : -1); };
            _pic.MouseWheel += (s, e) => { if (_fitMode) Go(e.Delta < 0 ? 1 : -1); };

            Controls.Add(_scroll);
            Controls.Add(_strip);
            Controls.Add(_bottom);
            Controls.Add(_top);
            Theme.Apply(this);
            _scroll.BackColor = Theme.P.Dark ? Color.FromArgb(24, 24, 24) : Color.FromArgb(240, 240, 240);

            Shown += (s, e) => ShowImage(_index);
            FormClosed += (s, e) => { _slideTimer.Dispose(); _pic.Image = null; _image?.Dispose(); };
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
            if (e.KeyData == (Keys.Shift | Keys.R)) { Rotate(false); e.Handled = true; return; }
            if (e.Modifiers != Keys.None) return;
            switch (e.KeyCode)
            {
                case Keys.R: Rotate(true); break;
                case Keys.L: Rotate(false); break;
                case Keys.F5: ToggleSlideshow(); break;
                case Keys.F11: case Keys.F: ToggleFullScreen(); break;
                case Keys.Left: case Keys.PageUp: case Keys.Back: Go(-1); break;
                case Keys.Right: case Keys.PageDown: case Keys.Space: Go(1); break;
                case Keys.Home: ShowImage(0); break;
                case Keys.End: ShowImage(_items.Count - 1); break;
                case Keys.Escape:
                    if (_fullScreen) ToggleFullScreen();
                    else if (_slideTimer.Enabled) ToggleSlideshow();
                    else Close();
                    break;
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

        private void Rotate(bool clockwise)
        {
            if (_image == null) return;
            try
            {
                _image.RotateFlip(clockwise ? RotateFlipType.Rotate90FlipNone : RotateFlipType.Rotate270FlipNone);
                _pic.Image = _image;
                _pic.Refresh();
                ApplySizeMode();
                UpdateInfo();
            }
            catch { }
        }

        private void ToggleSlideshow()
        {
            _slideTimer.Enabled = !_slideTimer.Enabled;
            _slide.Text = _slideTimer.Enabled ? "■ 정지" : "▶ 슬라이드쇼";
            if (_slideTimer.Enabled && !_fitMode) ToggleFit();
        }

        private void ToggleFullScreen()
        {
            _fullScreen = !_fullScreen;
            SuspendLayout();
            if (_fullScreen)
            {
                _savedState = WindowState;
                _top.Visible = _bottom.Visible = _strip.Visible = false;
                FormBorderStyle = FormBorderStyle.None;
                if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal; // re-maximise without the frame
                WindowState = FormWindowState.Maximized;
                _scroll.BackColor = Color.Black;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                WindowState = _savedState;
                _top.Visible = _bottom.Visible = true;
                _strip.Visible = _items.Count > 1;
                _scroll.BackColor = Theme.P.Dark ? Color.FromArgb(24, 24, 24) : Color.FromArgb(240, 240, 240);
            }
            ResumeLayout();
            ApplySizeMode();
        }

        private void UpdateInfo()
        {
            var e = _items[_index];
            _info.Text = (_index + 1) + " / " + _items.Count + " · " + e.Path +
                         (_image != null ? " · " + _image.Width + "×" + _image.Height : "") + " · " + PathUtil.FormatSize(e.Size);
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
            _strip.SetCurrent(index);
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
            UpdateInfo();
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

        /// <summary>Row of small pictures under the viewer; click one to jump to it.</summary>
        private sealed class ThumbStrip : Control
        {
            public event Action<int> Picked;
            private readonly ArchiveReader _reader;
            private readonly List<ArchiveEntry> _items;
            private readonly Dictionary<int, Bitmap> _thumbs = new Dictionary<int, Bitmap>();
            private readonly HashSet<int> _requested = new HashSet<int>();
            private readonly Stack<int> _queue = new Stack<int>();
            private bool _running, _disposed;
            private int _current, _scroll, _hot = -1;

            private int Cell => Theme.S(70);
            private int Gap => Theme.S(6);

            public ThumbStrip(ArchiveReader reader, List<ArchiveEntry> items)
            {
                _reader = reader; _items = items;
                DoubleBuffered = true;
                ResizeRedraw = true;
                Cursor = Cursors.Hand;
            }

            private int MaxScroll => Math.Max(0, _items.Count * (Cell + Gap) + Gap - Width);

            public void SetCurrent(int i)
            {
                _current = i;
                int x = Gap + i * (Cell + Gap);
                if (x < _scroll || x + Cell > _scroll + Width) _scroll = Math.Max(0, Math.Min(MaxScroll, x - (Width - Cell) / 2));
                Invalidate();
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                _scroll = Math.Max(0, Math.Min(MaxScroll, _scroll - Math.Sign(e.Delta) * (Cell + Gap) * 3));
                Invalidate();
            }

            private int HitTest(Point p)
            {
                int i = (p.X + _scroll - Gap) / (Cell + Gap);
                return i >= 0 && i < _items.Count ? i : -1;
            }

            protected override void OnMouseMove(MouseEventArgs e) { int h = HitTest(e.Location); if (h != _hot) { _hot = h; Invalidate(); } }
            protected override void OnMouseLeave(EventArgs e) { _hot = -1; Invalidate(); }
            protected override void OnMouseUp(MouseEventArgs e) { int i = HitTest(e.Location); if (i >= 0 && e.Button == MouseButtons.Left) Picked?.Invoke(i); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                var p = Theme.P;
                g.Clear(p.Dark ? Color.FromArgb(32, 32, 32) : p.Chrome);
                using (var pen = new Pen(p.Border)) g.DrawLine(pen, 0, 0, Width, 0);
                int top = (Height - Cell) / 2;
                int first = Math.Max(0, (_scroll - Gap) / (Cell + Gap)), last = Math.Min(_items.Count - 1, (_scroll + Width) / (Cell + Gap));
                for (int i = first; i <= last; i++)
                {
                    var r = new Rectangle(Gap + i * (Cell + Gap) - _scroll, top, Cell, Cell);
                    using (var b = new SolidBrush(p.Dark ? Color.FromArgb(45, 45, 45) : p.Bg)) g.FillRectangle(b, r);
                    if (_thumbs.TryGetValue(i, out var t) && t != null) g.DrawImage(t, r.X + (Cell - t.Width) / 2, r.Y + (Cell - t.Height) / 2, t.Width, t.Height);
                    else Request(i);
                    if (i == _current || i == _hot)
                        using (var pen = new Pen(i == _current ? p.Accent : p.Input, i == _current ? Theme.S(3) : 1) { Alignment = PenAlignment.Inset })
                            g.DrawRectangle(pen, r);
                }
            }

            private void Request(int i)
            {
                if (_items[i].Encrypted && _reader.Password == null) return;
                if (!_requested.Add(i)) return;
                _queue.Push(i);
                if (_running) return;
                _running = true;
                int cell = Cell;
                Task.Run(() => Work(cell));
            }

            private void Work(int cell)
            {
                while (true)
                {
                    var batch = new List<int>();
                    try
                    {
                        Invoke((Action)(() => { while (batch.Count < 8 && _queue.Count > 0) batch.Add(_queue.Pop()); if (batch.Count == 0) _running = false; }));
                    }
                    catch { return; } // closed
                    if (batch.Count == 0 || _disposed) return;
                    var entries = batch.Select(i => _items[i]).Where(x => x.Size <= 80UL * 1024 * 1024).OrderBy(x => x.Index).ToList();
                    Dictionary<int, byte[]> data;
                    try { data = _reader.ReadEntriesBytes(entries, new Silent()); }
                    catch { data = new Dictionary<int, byte[]>(); }
                    var made = new Dictionary<int, Bitmap>();
                    foreach (int i in batch)
                    {
                        Bitmap bmp = null;
                        if (data.TryGetValue(_items[i].Index, out var bytes))
                        {
                            try
                            {
                                using (var img = ImageDecode.Decode(bytes, _items[i].Name))
                                {
                                    float sc = Math.Min((float)cell / img.Width, (float)cell / img.Height);
                                    int w = Math.Max(1, (int)(img.Width * sc)), h = Math.Max(1, (int)(img.Height * sc));
                                    bmp = new Bitmap(w, h);
                                    using (var g = Graphics.FromImage(bmp)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(img, 0, 0, w, h); }
                                }
                            }
                            catch { }
                        }
                        made[i] = bmp;
                    }
                    try { BeginInvoke((Action)(() => { foreach (var kv in made) _thumbs[kv.Key] = kv.Value; Invalidate(); })); }
                    catch { foreach (var b in made.Values) b?.Dispose(); return; }
                }
            }

            protected override void Dispose(bool disposing)
            {
                _disposed = true;
                if (disposing) { foreach (var b in _thumbs.Values) b?.Dispose(); _thumbs.Clear(); }
                base.Dispose(disposing);
            }

            private sealed class Silent : IOperationUI
            {
                public bool IsCancelled => false;
                public void WaitIfPaused() { }
                public void SetStage(string text) { }
                public void SetTotal(ulong total) { }
                public void SetCompleted(ulong done) { }
                public void SetCurrentFile(string name) { }
                public string AskPassword(string archiveName, bool retry) => null;
                public OverwriteAction AskOverwrite(string targetPath, ArchiveEntry entry) => OverwriteAction.Overwrite;
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

using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ArcBox.Core;

namespace ArcBox.UI
{
    /// <summary>Modal progress window that runs work on a background (MTA) thread and implements IOperationUI.</summary>
    public sealed class ProgressForm : Form, IOperationUI
    {
        private readonly Label _title, _target, _file, _stats1, _stats2, _stats3, _stats4;
        private readonly Panel _bar, _barFill;
        private readonly Button _pause, _cancel;
        private readonly CheckBox _openFolder;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly ManualResetEventSlim _running = new ManualResetEventSlim(true);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly string _verb;

        private volatile bool _cancelled;
        private long _total, _done;
        private volatile string _current = "", _stage = "";
        private bool _finished;
        private OverwriteAction? _overwriteAll;
        private Exception _error;
        private string _subject;

        public bool IsCancelled => _cancelled;
        public bool OpenFolderChecked => _openFolder != null && _openFolder.Checked;
        public Exception Error => _error;

        private ProgressForm(string verb, string subject, string target, bool showOpenFolder)
        {
            _verb = verb; _subject = subject;
            Kit.InitDialog(this, verb, 480, showOpenFolder ? 262 : 236);
            ShowInTaskbar = true;
            ControlBox = true;

            var col = Kit.Column();
            _title = Kit.Lbl(verb + " — " + subject, null, true);
            _target = Kit.Lbl(string.IsNullOrEmpty(target) ? "" : "대상: " + target, "sub");
            _file = new Label { AutoEllipsis = true, AutoSize = false, Height = Theme.S(20), Dock = DockStyle.Fill, Font = Theme.UiFont, Text = "준비 중…" };
            _bar = new Panel { Height = Theme.S(8), Dock = DockStyle.Fill, Tag = "track", Margin = new Padding(0, Theme.S(6), 0, Theme.S(6)) };
            _barFill = new Panel { Width = 0, Dock = DockStyle.Left };
            _bar.Controls.Add(_barFill);
            var stats = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _stats1 = Kit.Lbl("처리: -", "sub"); _stats2 = Kit.Lbl("속도: -", "sub");
            _stats3 = Kit.Lbl("경과: 00:00", "sub"); _stats4 = Kit.Lbl("남은 시간: -", "sub");
            stats.Controls.Add(_stats1, 0, 0); stats.Controls.Add(_stats2, 1, 0);
            stats.Controls.Add(_stats3, 0, 1); stats.Controls.Add(_stats4, 1, 1);
            Kit.AddRow(col, _title, 2);
            if (!string.IsNullOrEmpty(target)) Kit.AddRow(col, _target, 2);
            Kit.AddRow(col, _file, 2);
            Kit.AddRow(col, _bar, 4);
            Kit.AddRow(col, stats, 4);
            if (showOpenFolder)
            {
                _openFolder = Kit.Chk("완료 후 폴더 열기", AppSettings.Current.OpenFolderAfterExtract);
                _openFolder.CheckedChanged += (s, e) => { AppSettings.Current.OpenFolderAfterExtract = _openFolder.Checked; AppSettings.Current.Save(); };
                Kit.AddRow(col, _openFolder, 2);
            }

            _pause = Kit.Btn("일시 정지", false, 92);
            _cancel = Kit.Btn("취소");
            _pause.Click += (s, e) => TogglePause();
            _cancel.Click += (s, e) => RequestCancel();
            Controls.Add(col);
            Controls.Add(Kit.Footer(null, _pause, _cancel));
            Theme.Apply(this);
            _bar.BackColor = Theme.P.Track;
            _barFill.BackColor = Theme.P.Accent;

            _timer = new System.Windows.Forms.Timer { Interval = 120 };
            _timer.Tick += (s, e) => Tick();
            FormClosing += (s, e) =>
            {
                if (!_finished) { e.Cancel = true; RequestCancel(); }
            };
            Opacity = 0; // shown only if the work takes longer than a moment
        }

        /// <summary>Runs <paramref name="work"/> on a thread-pool thread and shows progress. Returns false if cancelled.</summary>
        public static ProgressForm Run(IWin32Window owner, string verb, string subject, string target, bool showOpenFolder, Action<ProgressForm> work)
        {
            var f = new ProgressForm(verb, subject, target, showOpenFolder);
            if (owner == null) f.StartPosition = FormStartPosition.CenterScreen;
            f.Shown += (s, e) =>
            {
                f._timer.Start();
                Task.Run(() =>
                {
                    try { work(f); }
                    catch (OperationCanceledException) { f._cancelled = true; }
                    catch (Exception ex) { f._error = ex; Log.Write(verb + " failed: " + ex); }
                }).ContinueWith(t => f.BeginInvoke((Action)(() => { f._finished = true; f._timer.Stop(); f.Close(); })));
            };
            f.ShowDialog(owner);
            return f;
        }

        private void TogglePause()
        {
            if (_running.IsSet) { _running.Reset(); _pause.Text = "계속"; _clock.Stop(); }
            else { _running.Set(); _pause.Text = "일시 정지"; _clock.Start(); }
        }

        private void RequestCancel()
        {
            if (_cancelled) return;
            _cancelled = true;
            _running.Set();
            _cancel.Enabled = false;
            _pause.Enabled = false;
            _file.Text = "취소하는 중…";
        }

        private void Tick()
        {
            if (Opacity < 1 && _clock.ElapsedMilliseconds > 350) Opacity = 1;
            long total = Interlocked.Read(ref _total), done = Interlocked.Read(ref _done);
            double frac = total > 0 ? Math.Min(1.0, (double)done / total) : 0;
            _barFill.Width = (int)(_bar.Width * frac);
            int pct = (int)(frac * 100);
            Text = (total > 0 ? pct + "% " : "") + _verb + " — " + _subject;
            if (!_cancelled) _file.Text = string.IsNullOrEmpty(_stage) ? _current : _stage + (string.IsNullOrEmpty(_current) ? "" : " · " + _current);
            double sec = Math.Max(0.001, _clock.Elapsed.TotalSeconds);
            double speed = done / sec;
            _stats1.Text = "처리: " + PathUtil.FormatSize((ulong)Math.Max(0, done)) + (total > 0 ? " / " + PathUtil.FormatSize((ulong)total) : "");
            _stats2.Text = "속도: " + (sec > 0.5 ? PathUtil.FormatSize((ulong)speed) + "/s" : "-");
            _stats3.Text = "경과: " + Fmt(_clock.Elapsed);
            _stats4.Text = "남은 시간: " + (speed > 1 && total > done && sec > 1 ? "약 " + Fmt(TimeSpan.FromSeconds((total - done) / speed)) : "-");
        }

        private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

        // ------------------------------------------------------------------ IOperationUI
        public void WaitIfPaused() { if (!_cancelled) _running.Wait(); }
        public void SetStage(string text) => _stage = text ?? "";
        public void SetTotal(ulong total) { Interlocked.Exchange(ref _total, (long)total); Interlocked.Exchange(ref _done, 0); }
        public void SetCompleted(ulong done) => Interlocked.Exchange(ref _done, (long)done);
        public void SetCurrentFile(string name) => _current = name ?? "";
        public void SetSubject(string s) { _subject = s; }

        private T OnUi<T>(Func<T> f)
        {
            if (!InvokeRequired) return f();
            return (T)Invoke((Func<T>)(() => { Opacity = 1; return f(); }));
        }

        public string AskPassword(string archiveName, bool retry)
        {
            if (_cancelled) return null;
            _clock.Stop();
            try { return OnUi(() => PasswordDialog.Ask(this, archiveName, retry)); }
            finally { _clock.Start(); }
        }

        public OverwriteAction AskOverwrite(string targetPath, ArchiveEntry entry)
        {
            if (_overwriteAll.HasValue) return _overwriteAll.Value;
            _clock.Stop();
            try
            {
                return OnUi(() =>
                {
                    var r = OverwriteDialog.Ask(this, targetPath, entry, out bool all);
                    if (all && r != OverwriteAction.Cancel) _overwriteAll = r;
                    if (r == OverwriteAction.Cancel) RequestCancel();
                    return r;
                });
            }
            finally { _clock.Start(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _timer?.Dispose(); _running.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

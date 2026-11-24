using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using ArcBox.Core;

namespace ArcBox.UI
{
    public sealed class MainForm : Form
    {
        // ------------------------------------------------------------ model
        private sealed class Node
        {
            public string Name, Path;
            public Node Parent;
            public readonly SortedDictionary<string, Node> Dirs = new SortedDictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
            public readonly List<ArchiveEntry> Files = new List<ArchiveEntry>();
            public ArchiveEntry DirEntry;
            public TreeNode TreeNode;
            public ulong TotalSize;
        }

        private sealed class Row
        {
            public bool IsDir;
            public string Name;
            public ulong Size;
            public ulong? Packed;
            public DateTime? MTime;
            public bool Lock;
            public Node Node;
            public ArchiveEntry Entry;
        }

        private sealed class TempEdit
        {
            public string TempFile, EntryPath;
            public DateTime Stamp;
        }

        private ArchiveReader _reader;
        private int _codePage;
        private Node _root, _current;
        private List<Row> _rows = new List<Row>();
        private int _sortCol;
        private bool _sortAsc = true;
        private bool _suppressTree, _draggingOut;
        private readonly List<TempEdit> _tempEdits = new List<TempEdit>();
        private readonly List<string> _tempDirs = new List<string>();

        // ------------------------------------------------------------ controls
        private readonly MenuStrip _menu;
        private readonly ToolStrip _tools;
        private readonly Panel _addressBar;
        private readonly Button _upBtn, _backBtn, _fwdBtn;
        private readonly Stack<string> _back = new Stack<string>(), _fwd = new Stack<string>();
        private readonly SplitContainer _split2;
        private readonly PreviewPane _preview;
        private readonly Timer _previewTimer = new Timer { Interval = 160 };
        private ToolStripMenuItem _miPreview, _miPreview2;
        private readonly Label _address;
        private readonly TextBox _search;
        private readonly SplitContainer _split;
        private readonly TreeView _tree;
        private readonly ListView _list;
        private readonly StatusStrip _status;
        private readonly ToolStripStatusLabel _statusLeft, _statusRight;
        private readonly Panel _empty;
        private readonly FlowLayoutPanel _recentPanel;
        private readonly Timer _searchTimer = new Timer { Interval = 220 };
        private readonly ImageList _treeImages = new ImageList { ColorDepth = ColorDepth.Depth32Bit };

        // icon views (중간 / 큰 / 아주 큰 아이콘) with thumbnails of pictures
        private string _viewMode = "details";
        private ImageList _bigImages;
        private readonly Dictionary<string, int> _imgIndex = new Dictionary<string, int>();
        private readonly HashSet<int> _thumbQueued = new HashSet<int>();
        private readonly Stack<ArchiveEntry> _thumbStack = new Stack<ArchiveEntry>();
        private int _thumbGen;
        private bool _thumbRunning;
        private ToolStripDropDownButton _tbView;
        private readonly List<ToolStripMenuItem> _viewItems = new List<ToolStripMenuItem>();
        private static readonly string[] ViewModes = { "xlarge", "large", "medium", "details" };

        private ToolStripButton _tbOpen, _tbNew, _tbExtract, _tbAdd, _tbDelete, _tbTest, _tbInfo, _tbSettings;
        private ToolStripMenuItem _miRecent, _miEncoding, _miTheme;
        private readonly List<ToolStripItem> _needArchive = new List<ToolStripItem>();
        private readonly List<ToolStripItem> _needModify = new List<ToolStripItem>();

        public MainForm(string openPath)
        {
            var s = AppSettings.Current;
            Text = "ArcBox";
            Font = Theme.UiFont;
            AutoScaleMode = AutoScaleMode.None;
            try { Icon = Kit.AppIcon; } catch { }
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Theme.S(s.WindowW), Theme.S(s.WindowH));
            MinimumSize = new Size(Theme.S(560), Theme.S(380));
            if (s.WindowMax) WindowState = FormWindowState.Maximized;
            AllowDrop = true;
            KeyPreview = true;

            // menu
            _menu = new MenuStrip { Font = Theme.UiFont, Padding = new Padding(Theme.S(4), Theme.S(1), 0, Theme.S(1)) };
            BuildMenu();

            // toolbar
            _tools = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden, Font = Theme.UiFont, Padding = new Padding(Theme.S(6), Theme.S(4), Theme.S(6), Theme.S(4)),
                ImageScalingSize = new Size(Theme.S(22), Theme.S(22)), CanOverflow = true, Stretch = true
            };
            BuildToolbar();

            // address bar
            _addressBar = new Panel { Dock = DockStyle.Top, Height = Theme.S(40), Padding = new Padding(Theme.S(8), Theme.S(6), Theme.S(8), Theme.S(6)) };
            var toolTip = new ToolTip();
            Button NavBtn(string tip, Action a)
            {
                var b = new Button { Dock = DockStyle.Left, Width = Theme.S(28), Text = "", FlatStyle = FlatStyle.Flat };
                b.Click += (o, e) => a();
                toolTip.SetToolTip(b, tip);
                return b;
            }
            _backBtn = NavBtn("뒤로 (Alt+←)", GoBack);
            _fwdBtn = NavBtn("앞으로 (Alt+→)", GoForward);
            _upBtn = NavBtn("상위 폴더 (Backspace)", GoUp);
            var addrHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(6), 0, Theme.S(6), 0) };
            _address = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Font = Theme.UiFont, Padding = new Padding(Theme.S(8), 0, 0, 0) };
            var addrBox = new BorderPanel { Dock = DockStyle.Fill };
            addrBox.Controls.Add(_address);
            addrHost.Controls.Add(addrBox);
            var searchBox = new BorderPanel { Dock = DockStyle.Right, Width = Theme.S(200), Padding = new Padding(Theme.S(26), Theme.S(5), Theme.S(6), 0), Glyph = Glyphs.Search };
            _search = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = Theme.UiFont };
            SetCue(_search, "압축 파일 안에서 검색");
            searchBox.Controls.Add(_search);
            _search.TextChanged += (o, e) => { _searchTimer.Stop(); _searchTimer.Start(); };
            _searchTimer.Tick += (o, e) => { _searchTimer.Stop(); RefreshList(); };
            _addressBar.Controls.Add(addrHost);
            _addressBar.Controls.Add(searchBox);
            _addressBar.Controls.Add(_upBtn);
            _addressBar.Controls.Add(new Panel { Dock = DockStyle.Left, Width = Theme.S(4) });
            _addressBar.Controls.Add(_fwdBtn);
            _addressBar.Controls.Add(new Panel { Dock = DockStyle.Left, Width = Theme.S(4) });
            _addressBar.Controls.Add(_backBtn);
            var addrLine = new Panel { Dock = DockStyle.Top, Height = 1, Tag = "line" };
            var toolLine = new Panel { Dock = DockStyle.Top, Height = 1, Tag = "line" };

            // tree + list
            _split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = Theme.S(4) };
            _tree = new NoHScrollTree
            {
                ShowNodeToolTips = true,
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, HideSelection = false, ShowLines = false, FullRowSelect = true,
                ItemHeight = Theme.S(24), ImageList = _treeImages, Font = Theme.UiFont, Indent = Theme.S(14)
            };
            _treeImages.ImageSize = new Size(Theme.S(16), Theme.S(16));
            _tree.DrawMode = TreeViewDrawMode.OwnerDrawText;
            _tree.DrawNode += (o, e) =>
            {
                if (e.Node == null || e.Bounds.Width <= 0) return;
                var p = Theme.P;
                bool sel = e.Node == _tree.SelectedNode;
                var r = new Rectangle(e.Bounds.X, e.Bounds.Y, Math.Max(e.Bounds.Width, _tree.ClientSize.Width - e.Bounds.X), e.Bounds.Height);
                using (var b = new SolidBrush(sel ? p.Hover : p.Side)) e.Graphics.FillRectangle(b, r);
                // never wider than the visible pane, so long names end in "…" instead of being cut
                int tw = Math.Max(Theme.S(10), _tree.ClientSize.Width - e.Bounds.X - Theme.S(6));
                TextRenderer.DrawText(e.Graphics, e.Node.Text, Theme.UiFont, new Rectangle(e.Bounds.X + Theme.S(2), e.Bounds.Y, tw, r.Height),
                    p.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            _tree.AfterSelect += (o, e) => { if (!_suppressTree && e.Node?.Tag is Node n) ShowFolder(n, false); };
            _split.Panel1.Controls.Add(_tree);
            _split.SplitterMoved += (o, e) => _tree.Invalidate();
            _tree.Resize += (o, e) => _tree.Invalidate();
            _split.Panel1.Padding = new Padding(Theme.S(4), Theme.S(6), 0, 0);

            _list = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, VirtualMode = true, FullRowSelect = true, OwnerDraw = true,
                BorderStyle = BorderStyle.None, HideSelection = false, Font = Theme.UiFont, AllowDrop = true,
                SmallImageList = new ImageList { ImageSize = new Size(1, Theme.S(26)) }
            };
            _list.Columns.Add("이름", Theme.S(300));
            _list.Columns.Add("크기", Theme.S(80), HorizontalAlignment.Right);
            _list.Columns.Add("압축 크기", Theme.S(80), HorizontalAlignment.Right);
            _list.Columns.Add("수정한 날짜", Theme.S(130));
            _list.RetrieveVirtualItem += (o, e) =>
            {
                if (_list.View == View.Details || e.ItemIndex < 0 || e.ItemIndex >= _rows.Count) { e.Item = new ListViewItem(new[] { "", "", "", "" }); return; }
                var row = _rows[e.ItemIndex];
                e.Item = new ListViewItem(row.Name) { ImageIndex = BigImageFor(row) };
            };
            _list.MouseWheel += (o, e) =>
            {
                if ((ModifierKeys & Keys.Control) == 0) return;
                if (e is HandledMouseEventArgs h) h.Handled = true;
                int i = Array.IndexOf(ViewModes, _viewMode) + (e.Delta > 0 ? -1 : 1);
                if (i >= 0 && i < ViewModes.Length) SetViewMode(ViewModes[i]);
            };
            _list.DrawColumnHeader += DrawHeader;
            _list.DrawItem += (o, e) => { };
            _list.DrawSubItem += DrawCell;
            _list.ColumnClick += (o, e) => { if (_sortCol == e.Column) _sortAsc = !_sortAsc; else { _sortCol = e.Column; _sortAsc = true; } RefreshList(); };
            _list.DoubleClick += (o, e) => OpenSelected();
            _list.SelectedIndexChanged += (o, e) => UpdateState();
            _list.VirtualItemsSelectionRangeChanged += (o, e) => UpdateState();
            _list.ItemDrag += (o, e) => DragOut();
            _list.Resize += (o, e) => FitColumns();
            _list.MouseUp += (o, e) => { if (e.Button == MouseButtons.Right) ShowListMenu(e.Location); };
            _list.HandleCreated += (o, e) => ApplyIconSpacing();
            _split2 = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, SplitterWidth = Theme.S(4) };
            _preview = new PreviewPane { Dock = DockStyle.Fill };
            _split2.Panel1.Controls.Add(_list);
            _split2.Panel2.Controls.Add(_preview);
            _split2.Panel2Collapsed = !s.PreviewPane;
            _split.Panel2.Controls.Add(_split2);
            _list.SelectedIndexChanged += (o, e) => SchedulePreview();
            _list.VirtualItemsSelectionRangeChanged += (o, e) => SchedulePreview();
            _previewTimer.Tick += (o, e) => { _previewTimer.Stop(); UpdatePreview(); };

            // status
            _status = new StatusStrip { SizingGrip = false, Font = Theme.UiFont };
            _statusLeft = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _statusRight = new ToolStripStatusLabel { TextAlign = ContentAlignment.MiddleRight };
            _status.Items.Add(_statusLeft); _status.Items.Add(_statusRight);

            // empty state
            _empty = new Panel { Dock = DockStyle.Fill, Tag = "side" };
            var drop = new DropZone { Width = Theme.S(520), Height = Theme.S(330), Padding = new Padding(Theme.S(4)) };
            var dz = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(Theme.S(28), Theme.S(28), Theme.S(28), Theme.S(16)), BackColor = Color.Transparent };
            var heading = Kit.Lbl("파일을 여기로 끌어 놓으세요", null, true); heading.Font = Theme.TitleFont; heading.Anchor = AnchorStyles.None;
            var sub = Kit.Lbl("압축 파일은 바로 열리고, 일반 파일과 폴더는 새로 압축합니다.", "sub"); sub.Anchor = AnchorStyles.None;
            var openBtn = Kit.Btn("압축 파일 열기", true, 120); openBtn.Click += (o, e) => OpenDialog();
            var newBtn = Kit.Btn("새로 압축하기", false, 120); newBtn.Click += (o, e) => NewArchiveFromFiles();
            var btnRow = Kit.Row(openBtn, newBtn); btnRow.Anchor = AnchorStyles.None; btnRow.Dock = DockStyle.None;
            _recentPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, Theme.S(14), 0, 0) };
            var icon = new PictureBox { Size = new Size(Theme.S(54), Theme.S(54)), Anchor = AnchorStyles.None, SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Color.Transparent };
            dz.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            dz.Controls.Add(icon, 0, 0); dz.Controls.Add(heading, 0, 1); dz.Controls.Add(sub, 0, 2); dz.Controls.Add(btnRow, 0, 3); dz.Controls.Add(_recentPanel, 0, 4);
            drop.Controls.Add(dz);
            drop.Tag = icon;
            _empty.Controls.Add(drop);
            _empty.Resize += (o, e) => drop.Location = new Point(Math.Max(0, (_empty.Width - drop.Width) / 2), Math.Max(0, (_empty.Height - drop.Height) / 2));

            // docking order: fill first, then top bars from inner to outer
            Controls.Add(_split);
            Controls.Add(_empty);
            Controls.Add(addrLine);
            Controls.Add(_addressBar);
            Controls.Add(toolLine);
            Controls.Add(_tools);
            Controls.Add(_menu);
            Controls.Add(_status);
            MainMenuStrip = _menu;

            // events
            DragEnter += OnDragEnter; DragDrop += OnDragDrop;
            _list.DragEnter += OnDragEnter; _list.DragDrop += OnDragDrop;
            _empty.AllowDrop = true; _empty.DragEnter += OnDragEnter; _empty.DragDrop += OnDragDrop;
            KeyDown += OnKey;
            Activated += (o, e) => CheckTempEdits();
            FormClosing += (o, e) => SaveWindow();
            FormClosed += (o, e) => { CloseArchive(); CleanupTemp(); };
            Theme.Changed += ApplyTheme;
            Load += (o, e) =>
            {
                _split.Panel1MinSize = Theme.S(120);
                _split.SplitterDistance = Theme.S(Math.Max(120, s.TreeWidth));
                try { _split2.Panel2MinSize = Theme.S(160); _split2.SplitterDistance = Math.Max(Theme.S(200), _split2.Width - Theme.S(Math.Max(160, s.PreviewWidth))); } catch { }
                ApplyTheme();
                SetViewMode(s.ViewMode);
                ShowEmpty();
                if (!string.IsNullOrEmpty(openPath)) BeginInvoke((Action)(() => OpenArchive(openPath)));
            };
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        private static void SetCue(TextBox tb, string cue) => tb.HandleCreated += (s, e) => SendMessage(tb.Handle, 0x1501, (IntPtr)1, cue);

        // ------------------------------------------------------------ menu / toolbar
        private ToolStripMenuItem Mi(ToolStripMenuItem parent, string text, Action click, Keys keys = Keys.None, int needs = 0)
        {
            var m = new ToolStripMenuItem(text) { ShortcutKeys = keys };
            if (keys == Keys.Delete) m.ShortcutKeyDisplayString = "Del";
            if (keys == Keys.F2) m.ShortcutKeyDisplayString = "F2";
            if (click != null) m.Click += (o, e) => click();
            parent.DropDownItems.Add(m);
            if (needs >= 1) _needArchive.Add(m);
            if (needs >= 2) _needModify.Add(m);
            return m;
        }

        private void BuildMenu()
        {
            var file = new ToolStripMenuItem("파일(&F)");
            Mi(file, "열기…", OpenDialog, Keys.Control | Keys.O);
            Mi(file, "새로 압축하기 (파일 선택)…", NewArchiveFromFiles, Keys.Control | Keys.N);
            Mi(file, "새로 압축하기 (폴더 선택)…", NewArchiveFromFolder);
            _miRecent = Mi(file, "최근 파일", null);
            file.DropDownOpening += (o, e) => FillRecentMenu();
            file.DropDownItems.Add(new ToolStripSeparator());
            Mi(file, "압축 파일 정보", ShowInfo, Keys.Control | Keys.I, 1);
            Mi(file, "닫기", () => { CloseArchive(); ShowEmpty(); }, Keys.Control | Keys.W, 1);
            file.DropDownItems.Add(new ToolStripSeparator());
            Mi(file, "끝내기", Close);

            var edit = new ToolStripMenuItem("편집(&E)");
            Mi(edit, "파일 추가…", AddFilesDialog, Keys.None, 2);
            Mi(edit, "폴더 추가…", AddFolderDialog, Keys.None, 2);
            Mi(edit, "새 폴더…", NewFolder, Keys.None, 2);
            edit.DropDownItems.Add(new ToolStripSeparator());
            Mi(edit, "이름 바꾸기", Rename, Keys.F2, 2);
            Mi(edit, "삭제", DeleteSelected, Keys.Delete, 2);
            edit.DropDownItems.Add(new ToolStripSeparator());
            Mi(edit, "모두 선택", SelectAll, Keys.Control | Keys.A, 1);

            var act = new ToolStripMenuItem("작업(&A)");
            Mi(act, "압축 풀기…", ExtractDialog, Keys.Control | Keys.E, 1);
            Mi(act, "여기에 풀기 (스마트)", () => ExtractQuick(ExtractTarget.Smart), Keys.None, 1);
            Mi(act, "폴더로 풀기", () => ExtractQuick(ExtractTarget.Folder), Keys.None, 1);
            act.DropDownItems.Add(new ToolStripSeparator());
            Mi(act, "압축 파일 테스트", TestArchive, Keys.Control | Keys.T, 1);
            act.DropDownItems.Add(new ToolStripSeparator());
            Mi(act, "이미지 미리보기 (Space)", PreviewSelected, Keys.None, 1);
            Mi(act, "다른 프로그램으로 열기", OpenExternally, Keys.None, 1);

            var view = new ToolStripMenuItem("보기(&V)");
            foreach (var mi in ViewMenuItems()) view.DropDownItems.Add(mi);
            view.DropDownItems.Add(new ToolStripSeparator());
            _miPreview = new ToolStripMenuItem("미리보기 창", null, (o, e) => TogglePreview()) { ShortcutKeys = Keys.Alt | Keys.P };
            view.DropDownItems.Add(_miPreview);
            view.DropDownItems.Add(new ToolStripSeparator());
            view.DropDownOpening += (o, e) => SyncViewChecks();
            Mi(view, "새로 고침", () => Reopen(), Keys.F5, 1);
            _miEncoding = Mi(view, "파일 이름 인코딩", null, Keys.None, 1);
            foreach (var kv in new[] { (0, "자동 (시스템 기본)"), (65001, "유니코드 (UTF-8)"), (949, "한국어 (CP949)"), (932, "일본어 (Shift-JIS)"), (936, "중국어 간체 (GBK)"), (950, "중국어 번체 (Big5)"), (1252, "서유럽 (Windows-1252)"), (866, "키릴 (CP866)") })
            {
                var cp = kv.Item1;
                var m = new ToolStripMenuItem(kv.Item2) { Tag = cp };
                m.Click += (o, e) => { if (_reader != null) OpenArchive(_reader.FilePath, cp); };
                _miEncoding.DropDownItems.Add(m);
            }
            _miEncoding.DropDownOpening += (o, e) => { foreach (ToolStripMenuItem m in _miEncoding.DropDownItems) m.Checked = (int)m.Tag == _codePage; };
            _miTheme = Mi(view, "테마", null);
            foreach (var kv in new[] { ("system", "Windows 설정 따르기"), ("light", "라이트"), ("dark", "다크") })
            {
                var key = kv.Item1;
                var m = new ToolStripMenuItem(kv.Item2) { Tag = key };
                m.Click += (o, e) => { AppSettings.Current.Theme = key; AppSettings.Current.Save(); Theme.SetMode(key); };
                _miTheme.DropDownItems.Add(m);
            }
            _miTheme.DropDownOpening += (o, e) => { foreach (ToolStripMenuItem m in _miTheme.DropDownItems) m.Checked = (string)m.Tag == AppSettings.Current.Theme; };

            var tools = new ToolStripMenuItem("도구(&T)");
            Mi(tools, "설정…", () => ShowSettings("일반"));
            Mi(tools, "파일 연결…", () => ShowSettings("파일 연결"));
            Mi(tools, "Windows 기본 앱 설정 열기", () => { try { Kit.ShellOpen("ms-settings:defaultapps?registeredAppUser=ArcBox"); } catch { } });

            var help = new ToolStripMenuItem("도움말(&H)");
            Mi(help, "ArcBox 정보", About);

            _menu.Items.AddRange(new ToolStripItem[] { file, edit, act, view, tools, help });
        }

        private ToolStripButton Tb(string text, string glyph, Action click, int needs = 0, string tip = null)
        {
            var b = new ToolStripButton(text)
            {
                TextImageRelation = TextImageRelation.ImageAboveText, ImageScaling = ToolStripItemImageScaling.None,
                AutoSize = false, Size = new Size(Theme.S(58), Theme.S(54)), Tag = glyph, ToolTipText = tip ?? text, Margin = new Padding(0, 0, Theme.S(2), 0)
            };
            b.Click += (o, e) => click();
            _tools.Items.Add(b);
            if (needs >= 1) _needArchive.Add(b);
            if (needs >= 2) _needModify.Add(b);
            return b;
        }

        private void BuildToolbar()
        {
            _tbOpen = Tb("열기", Glyphs.Open, OpenDialog, 0, "압축 파일 열기 (Ctrl+O)");
            _tbNew = Tb("새 압축", Glyphs.NewArchive, NewArchiveFromFiles, 0, "새로 압축하기 (Ctrl+N)");
            _tbExtract = Tb("풀기", Glyphs.Extract, ExtractDialog, 1, "압축 풀기 (Ctrl+E)");
            _tbAdd = Tb("추가", Glyphs.Add, AddFilesDialog, 2, "파일 추가");
            _tbDelete = Tb("삭제", Glyphs.Delete, DeleteSelected, 2, "삭제 (Del)");
            _tbTest = Tb("테스트", Glyphs.Test, TestArchive, 1, "압축 파일 테스트 (Ctrl+T)");
            _tbInfo = Tb("정보", Glyphs.Info, ShowInfo, 1, "압축 파일 정보 (Ctrl+I)");
            _tbView = new ToolStripDropDownButton("보기")
            {
                TextImageRelation = TextImageRelation.ImageAboveText, ImageScaling = ToolStripItemImageScaling.None, ShowDropDownArrow = false,
                AutoSize = false, Size = new Size(Theme.S(58), Theme.S(54)), Tag = Glyphs.View, ToolTipText = "보기 방식 (Ctrl+마우스 휠)", Margin = new Padding(0, 0, Theme.S(2), 0)
            };
            _tbView.DropDown.Renderer = new Theme.FlatRenderer();
            foreach (var mi in ViewMenuItems(false)) _tbView.DropDownItems.Add(mi);
            _tbView.DropDownItems.Add(new ToolStripSeparator());
            _miPreview2 = new ToolStripMenuItem("미리보기 창", null, (o, e) => TogglePreview()) { ShortcutKeyDisplayString = "Alt+P" };
            _tbView.DropDownItems.Add(_miPreview2);
            _tbView.DropDownOpening += (o, e) => SyncViewChecks();
            _tools.Items.Add(_tbView);
            _needArchive.Add(_tbView);
            _tools.Items.Add(new ToolStripSeparator { AutoSize = false, Size = new Size(Theme.S(12), Theme.S(54)) });
            _tbSettings = Tb("설정", Glyphs.Settings, () => ShowSettings("일반"));
        }

        private void ApplyTheme()
        {
            var p = Theme.P;
            Theme.Apply(this);
            foreach (ToolStripItem it in _tools.Items)
            {
                if ((it is ToolStripButton || it is ToolStripDropDownButton) && it.Tag is string g)
                {
                    bool accent = it == _tbNew || it == _tbExtract;
                    it.Image = Glyphs.Get(g, Theme.S(22), accent ? p.Accent : p.Icon, it.Text.Substring(0, 1));
                }
            }
            _upBtn.Image = Glyphs.Get(Glyphs.Up, Theme.S(14), p.Text, "↑");
            _backBtn.Image = Glyphs.Get(Glyphs.Back, Theme.S(14), p.Text, "←");
            _fwdBtn.Image = Glyphs.Get(Glyphs.Forward, Theme.S(14), p.Text, "→");
            foreach (var nb in new[] { _upBtn, _backBtn, _fwdBtn })
            {
                nb.FlatAppearance.BorderColor = p.Border;
                nb.BackColor = p.Btn;
                nb.FlatAppearance.MouseOverBackColor = p.Hover;
            }
            _preview.ApplyTheme();
            _split2.BackColor = p.Border;
            _split2.Panel1.BackColor = _split2.Panel2.BackColor = p.Bg;
            _search.BackColor = p.Field; _search.ForeColor = p.Text;
            _split.BackColor = p.Border;
            _split.Panel1.BackColor = p.Side;
            _split.Panel2.BackColor = p.Bg;
            _addressBar.BackColor = p.Bg;
            _treeImages.Images.Clear();
            _treeImages.Images.Add("dir", ShellIcons.Icon("x", true));
            _treeImages.Images.Add("arc", Glyphs.Get(Glyphs.NewArchive, Theme.S(16), p.Accent, "A"));
            if (_empty.Controls.Count > 0 && _empty.Controls[0].Tag is PictureBox pb)
                pb.Image = Glyphs.Get(Glyphs.NewArchive, Theme.S(48), p.Accent, "A");
            FillRecentPanel();
            _list.Invalidate();
            _tree.Invalidate();
            Invalidate(true);
        }

        // ------------------------------------------------------------ list drawing
        private void DrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            var p = Theme.P;
            using (var b = new SolidBrush(p.Bg)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var pen = new Pen(p.Border)) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            string text = e.Header.Text;
            var r = Rectangle.Inflate(e.Bounds, -Theme.S(8), 0);
            bool right = e.Header.TextAlign == HorizontalAlignment.Right;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine |
                        (right ? TextFormatFlags.Right : TextFormatFlags.Left);
            TextRenderer.DrawText(e.Graphics, text, Theme.UiFont, r, p.Sub, flags);
            if (e.ColumnIndex == _sortCol)
            {
                var tw = TextRenderer.MeasureText(e.Graphics, text, Theme.UiFont).Width;
                int cx = right ? r.Right - tw - Theme.S(8) : r.Left + tw + Theme.S(4);
                int cy = e.Bounds.Top + e.Bounds.Height / 2;
                int a = Theme.S(3);
                var tri = _sortAsc
                    ? new[] { new Point(cx - a, cy + a / 2 + 1), new Point(cx + a, cy + a / 2 + 1), new Point(cx, cy - a + 1) }
                    : new[] { new Point(cx - a, cy - a / 2), new Point(cx + a, cy - a / 2), new Point(cx, cy + a) };
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(p.Sub)) e.Graphics.FillPolygon(b, tri);
            }
        }

        private void DrawCell(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= _rows.Count) return;
            var p = Theme.P;
            var row = _rows[e.ItemIndex];
            bool sel = _list.SelectedIndices.Contains(e.ItemIndex);
            var bounds = e.Bounds;
            using (var b = new SolidBrush(sel ? p.Sel : p.Bg)) e.Graphics.FillRectangle(b, bounds);
            using (var pen = new Pen(p.RowLine)) e.Graphics.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            switch (e.ColumnIndex)
            {
                case 0:
                {
                    var icon = ShellIcons.Icon(row.Name, row.IsDir);
                    int isz = Theme.S(16);
                    e.Graphics.DrawImage(icon, new Rectangle(bounds.Left + Theme.S(10), bounds.Top + (bounds.Height - isz) / 2, isz, isz));
                    int x = bounds.Left + Theme.S(34);
                    var tr = new Rectangle(x, bounds.Top, bounds.Right - x - Theme.S(4), bounds.Height);
                    var sz = TextRenderer.MeasureText(e.Graphics, row.Name, Theme.UiFont, tr.Size, flags);
                    TextRenderer.DrawText(e.Graphics, row.Name, Theme.UiFont, tr, p.Text, flags);
                    if (row.Lock && sz.Width + Theme.S(18) < tr.Width)
                    {
                        int lsz = Theme.S(12);
                        e.Graphics.DrawImage(Glyphs.Get(Glyphs.Lock, lsz, p.Warn, "*"), x + sz.Width + Theme.S(2), bounds.Top + (bounds.Height - lsz) / 2);
                    }
                    break;
                }
                case 1:
                    TextRenderer.DrawText(e.Graphics, row.IsDir ? "" : PathUtil.FormatSize(row.Size), Theme.UiFont, Rectangle.Inflate(bounds, -Theme.S(8), 0), p.Text, flags | TextFormatFlags.Right);
                    break;
                case 2:
                    TextRenderer.DrawText(e.Graphics, row.Packed.HasValue && !row.IsDir ? PathUtil.FormatSize(row.Packed.Value) : "", Theme.UiFont, Rectangle.Inflate(bounds, -Theme.S(8), 0), p.Sub, flags | TextFormatFlags.Right);
                    break;
                case 3:
                    TextRenderer.DrawText(e.Graphics, row.MTime.HasValue ? row.MTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "", Theme.UiFont, Rectangle.Inflate(bounds, -Theme.S(8), 0), p.Sub, flags);
                    break;
            }
        }

        private void FitColumns()
        {
            if (_list.Columns.Count < 4) return;
            int others = _list.Columns[1].Width + _list.Columns[2].Width + _list.Columns[3].Width;
            int w = _list.ClientSize.Width - others - 2;
            _list.Columns[0].Width = Math.Max(Theme.S(160), w);
        }

        // ------------------------------------------------------------ archive lifecycle
        private void OpenDialog()
        {
            using (var d = new OpenFileDialog { Title = "압축 파일 열기", Filter = "압축 파일|" + string.Join(";", Shell.Formats.All.Select(f => "*." + f.Ext)) + "|모든 파일|*.*" })
                if (d.ShowDialog(this) == DialogResult.OK) OpenArchive(d.FileName);
        }

        public void OpenArchive(string path, int codePage = 0, string keepFolder = null, string password = null)
        {
            ArchiveReader nr = null;
            var old = _reader;
            if (old != null && string.Equals(old.FilePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                password = password ?? old.Password;
                CloseArchive(); // release the file before reopening it
            }
            var pf = ProgressForm.Run(this, "여는 중", Path.GetFileName(path), null, false, ui => nr = ArchiveReader.Open(path, ui, codePage, password));
            if (pf.IsCancelled || nr == null)
            {
                if (pf.Error != null) Kit.Error(this, Friendly(pf.Error));
                if (_reader == null) ShowEmpty();
                return;
            }
            CloseArchive();
            _reader = nr;
            _codePage = codePage;
            AppSettings.Current.AddRecent(nr.FilePath);
            BuildModel();
            ShowArchive();
            var target = keepFolder != null ? FindNode(keepFolder) : null;
            if (target == null && _root.Dirs.Count == 1 && _root.Files.Count == 0) target = _root.Dirs.Values.First();
            ShowFolder(target ?? _root, true);
            if (nr.Info.Any(kv => kv.Key == "경고"))
                _statusRight.Text = "⚠ 압축 파일 구조에 문제가 있습니다 (정보 참고)";
        }

        private static string Friendly(Exception ex) => ex is ArchiveException ? ex.Message : "압축 파일을 열 수 없습니다.\n" + ex.Message;

        private void Reopen() { if (_reader != null) OpenArchive(_reader.FilePath, _codePage, _current?.Path, _reader.Password); }

        private void CloseArchive()
        {
            if (_reader == null) return;
            try { _reader.Close(); } catch (Exception ex) { Log.Write("close: " + ex.Message); }
            _reader = null;
            _root = _current = null;
            _rows = new List<Row>();
            _list.VirtualListSize = 0;
            ResetThumbs();
            _back.Clear(); _fwd.Clear();
            _preview.Clear();
            _tree.Nodes.Clear();
            Text = "ArcBox";
        }

        private void ShowEmpty()
        {
            _split.Visible = false; _addressBar.Visible = false;
            _empty.Visible = true; _empty.BringToFront();
            FillRecentPanel();
            _statusLeft.Text = "준비됨"; _statusRight.Text = "";
            UpdateState();
        }

        private void ShowArchive()
        {
            _empty.Visible = false;
            _split.Visible = true; _addressBar.Visible = true;
            _split.BringToFront();
            Text = _reader.DisplayName + " — ArcBox";
        }

        private void FillRecentPanel()
        {
            _recentPanel.Controls.Clear();
            var recent = AppSettings.Current.Recent.Where(File.Exists).Take(4).ToList();
            if (recent.Count == 0) return;
            _recentPanel.Controls.Add(Kit.Lbl("최근 파일", "sub"));
            foreach (var r in recent)
            {
                var link = new LinkLabel { Text = Path.GetFileName(r), AutoSize = true, Font = Theme.UiFont, Margin = new Padding(0, Theme.S(2), 0, Theme.S(2)), LinkBehavior = LinkBehavior.HoverUnderline };
                var path = r;
                link.LinkClicked += (o, e) => OpenArchive(path);
                new ToolTip().SetToolTip(link, r);
                _recentPanel.Controls.Add(link);
            }
            Theme.Apply(_recentPanel);
        }

        private void FillRecentMenu()
        {
            _miRecent.DropDownItems.Clear();
            foreach (var r in AppSettings.Current.Recent.Take(10))
            {
                var path = r;
                var m = new ToolStripMenuItem(r) { Enabled = File.Exists(r) };
                m.Click += (o, e) => OpenArchive(path);
                _miRecent.DropDownItems.Add(m);
            }
            if (_miRecent.DropDownItems.Count == 0) _miRecent.DropDownItems.Add(new ToolStripMenuItem("(없음)") { Enabled = false });
        }

        // ------------------------------------------------------------ model / navigation
        private void BuildModel()
        {
            _root = new Node { Name = _reader.DisplayName, Path = "" };
            foreach (var e in _reader.Entries)
            {
                var parts = e.Path.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                var n = _root;
                int dirDepth = e.IsDir ? parts.Length : parts.Length - 1;
                for (int i = 0; i < dirDepth; i++)
                {
                    if (!n.Dirs.TryGetValue(parts[i], out var c))
                    {
                        c = new Node { Name = parts[i], Path = n.Path.Length == 0 ? parts[i] : n.Path + "\\" + parts[i], Parent = n };
                        n.Dirs[parts[i]] = c;
                    }
                    n = c;
                }
                if (e.IsDir) n.DirEntry = e;
                else n.Files.Add(e);
            }
            ComputeSizes(_root);

            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            var rootTn = new TreeNode(_root.Name) { Tag = _root, ImageKey = "arc", SelectedImageKey = "arc", ToolTipText = _root.Name };
            _root.TreeNode = rootTn;
            AddTreeChildren(_root, rootTn);
            _tree.Nodes.Add(rootTn);
            rootTn.Expand();
            _tree.EndUpdate();
            FitTreeWidth();
        }

        /// <summary>Widens the folder pane (up to 40% of the window) so the archive name and the first-level folders fit.</summary>
        private void FitTreeWidth()
        {
            if (_root?.TreeNode == null) return;
            int need = 0;
            void Measure(TreeNode tn, int level)
            {
                int w = Theme.S(4) + _tree.Indent * (level + 1) + Theme.S(20) + TextRenderer.MeasureText(tn.Text, Theme.UiFont).Width + Theme.S(16);
                need = Math.Max(need, w);
            }
            Measure(_root.TreeNode, 0);
            foreach (TreeNode c in _root.TreeNode.Nodes) Measure(c, 1);
            int max = (int)(_split.Width * 0.4);
            int want = Math.Min(need, max);
            if (want > _split.SplitterDistance)
            {
                try { _split.SplitterDistance = want; } catch { }
            }
        }

        private static ulong ComputeSizes(Node n)
        {
            ulong s = 0;
            foreach (var f in n.Files) s += f.Size;
            foreach (var d in n.Dirs.Values) s += ComputeSizes(d);
            n.TotalSize = s;
            return s;
        }

        private static void AddTreeChildren(Node n, TreeNode tn)
        {
            foreach (var d in n.Dirs.Values)
            {
                var c = new TreeNode(d.Name) { Tag = d, ImageKey = "dir", SelectedImageKey = "dir", ToolTipText = d.Name };
                d.TreeNode = c;
                AddTreeChildren(d, c);
                tn.Nodes.Add(c);
            }
        }

        private Node FindNode(string path)
        {
            if (_root == null) return null;
            if (string.IsNullOrEmpty(path)) return _root;
            var n = _root;
            foreach (var part in path.Split('\\'))
                if (!n.Dirs.TryGetValue(part, out n)) return null;
            return n;
        }

        private void ShowFolder(Node n, bool syncTree, bool record = true)
        {
            if (record && _current != null && _current != n)
            {
                _back.Push(_current.Path);
                _fwd.Clear();
            }
            _current = n;
            _backBtn.Enabled = _back.Count > 0;
            _fwdBtn.Enabled = _fwd.Count > 0;
            lock (_thumbStack) { _thumbStack.Clear(); _thumbQueued.Clear(); }
            if (_search.Text.Length > 0) _search.Text = "";
            if (syncTree && n.TreeNode != null)
            {
                _suppressTree = true;
                _tree.SelectedNode = n.TreeNode;
                n.TreeNode.EnsureVisible();
                _suppressTree = false;
            }
            var parts = new List<string> { _reader.DisplayName };
            if (n.Path.Length > 0) parts.AddRange(n.Path.Split('\\'));
            _address.Text = string.Join("  ›  ", parts);
            RefreshList();
            _list.SelectedIndices.Clear();
            if (_rows.Count > 0) _list.EnsureVisible(0);
            SchedulePreview();
        }

        private void GoBack() => Travel(_back, _fwd);
        private void GoForward() => Travel(_fwd, _back);

        private void Travel(Stack<string> from, Stack<string> to)
        {
            if (_reader == null || _current == null) return;
            while (from.Count > 0)
            {
                var n = FindNode(from.Pop());
                if (n == null) continue; // folder no longer exists (after a delete)
                to.Push(_current.Path);
                ShowFolder(n, true, false);
                return;
            }
        }

        private const int WM_APPCOMMAND = 0x319;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_APPCOMMAND)
            {
                int cmd = (int)(((long)m.LParam >> 16) & 0x0FFF);
                if (cmd == 1) { GoBack(); m.Result = (IntPtr)1; return; }    // APPCOMMAND_BROWSER_BACKWARD (mouse back button)
                if (cmd == 2) { GoForward(); m.Result = (IntPtr)1; return; } // APPCOMMAND_BROWSER_FORWARD
            }
            base.WndProc(ref m);
        }

        private void GoUp()
        {
            if (_current?.Parent != null)
            {
                var from = _current;
                ShowFolder(_current.Parent, true);
                int idx = _rows.FindIndex(r => r.Node == from);
                if (idx >= 0) { _list.SelectedIndices.Add(idx); _list.EnsureVisible(idx); _list.FocusedItem = _list.Items[idx]; }
            }
        }

        private void RefreshList()
        {
            if (_current == null) return;
            var rows = new List<Row>();
            string q = _search.Text.Trim();
            if (q.Length > 0)
            {
                foreach (var e in _reader.Entries)
                {
                    if (e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (e.IsDir) { var dn = FindNode(e.Path); if (dn != null) rows.Add(DirRow(dn, e.Path)); }
                    else rows.Add(FileRow(e, e.Path));
                }
            }
            else
            {
                foreach (var d in _current.Dirs.Values) rows.Add(DirRow(d, d.Name));
                foreach (var f in _current.Files) rows.Add(FileRow(f, f.Name));
            }
            Comparison<Row> cmp;
            switch (_sortCol)
            {
                case 1: cmp = (a, b) => (a.IsDir ? a.Node.TotalSize : a.Size).CompareTo(b.IsDir ? b.Node.TotalSize : b.Size); break;
                case 2: cmp = (a, b) => (a.Packed ?? 0).CompareTo(b.Packed ?? 0); break;
                case 3: cmp = (a, b) => (a.MTime ?? DateTime.MinValue).CompareTo(b.MTime ?? DateTime.MinValue); break;
                default: cmp = (a, b) => NaturalCompare(a.Name, b.Name); break;
            }
            rows.Sort((a, b) =>
            {
                if (a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
                int c = cmp(a, b);
                if (c == 0) c = NaturalCompare(a.Name, b.Name);
                return _sortAsc ? c : -c;
            });
            _rows = rows;
            _list.VirtualListSize = rows.Count;
            _list.Invalidate();
            UpdateState();
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int StrCmpLogicalW(string a, string b);
        private static int NaturalCompare(string a, string b)
        {
            try { return StrCmpLogicalW(a, b); } catch { return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase); }
        }

        private static Row DirRow(Node d, string name) => new Row { IsDir = true, Name = name, Node = d, MTime = d.DirEntry?.MTime, Size = d.TotalSize };
        private static Row FileRow(ArchiveEntry e, string name) => new Row { Name = name, Entry = e, Size = e.Size, Packed = e.PackedSize, MTime = e.MTime, Lock = e.Encrypted };

        private List<Row> SelectedRows() => _list.SelectedIndices.Cast<int>().Where(i => i >= 0 && i < _rows.Count).Select(i => _rows[i]).ToList();

        /// <summary>All archive entries covered by the given rows (folders include everything below them).</summary>
        private List<ArchiveEntry> EntriesOf(IEnumerable<Row> rows)
        {
            var set = new HashSet<int>();
            var result = new List<ArchiveEntry>();
            foreach (var r in rows)
            {
                if (!r.IsDir) { if (set.Add(r.Entry.Index)) result.Add(r.Entry); continue; }
                string prefix = r.Node.Path + "\\";
                foreach (var e in _reader.Entries)
                {
                    if ((e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Path, r.Node.Path, StringComparison.OrdinalIgnoreCase)) && set.Add(e.Index))
                        result.Add(e);
                }
            }
            return result;
        }

        private void UpdateState()
        {
            bool has = _reader != null;
            bool canMod = has && _reader.CanModify;
            foreach (var i in _needArchive) i.Enabled = has;
            foreach (var i in _needModify) i.Enabled = canMod;
            if (!has) return;
            var sel = SelectedRows();
            _tbDelete.Enabled = canMod && sel.Count > 0;
            int files = _rows.Count(r => !r.IsDir), dirs = _rows.Count - files;
            string left = "파일 " + files.ToString("N0") + "개, 폴더 " + dirs.ToString("N0") + "개";
            if (sel.Count > 0)
            {
                ulong size = 0;
                foreach (var r in sel) size += r.IsDir ? r.Node.TotalSize : r.Size;
                left += " · " + sel.Count + "개 선택됨 (" + PathUtil.FormatSize(size) + ")";
            }
            if (_search.Text.Trim().Length > 0) left = "검색 결과 · " + left;
            _statusLeft.Text = left;
            ulong total = _root?.TotalSize ?? 0;
            string right = _reader.Format.Name.ToUpperInvariant();
            if (_reader.HasEncrypted || _reader.HeadersEncrypted) right += " · 암호";
            if (_reader.IsSolid) right += " · 솔리드";
            right += " · " + PathUtil.FormatSize(total) + " → " + PathUtil.FormatSize(_reader.PhysicalSize);
            if (total > 0) right += " (" + (100.0 * _reader.PhysicalSize / total).ToString("0.0") + "%)";
            if (!_reader.CanModify) right += " · 읽기 전용";
            _statusRight.Text = right;
        }

        // ------------------------------------------------------------ keyboard / context menu
        private void OnKey(object sender, KeyEventArgs e)
        {
            if (_search.Focused) { if (e.KeyCode == Keys.Escape) { _search.Text = ""; _list.Focus(); e.Handled = true; } return; }
            if (_reader == null) return;
            if (e.Alt && e.KeyCode == Keys.Left) { GoBack(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.Alt && e.KeyCode == Keys.Right) { GoForward(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.Alt && e.KeyCode == Keys.Up) { GoUp(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Back) { GoUp(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && _list.Focused) { OpenSelected(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); e.Handled = true; }
            else if (e.KeyCode == Keys.Space && _list.Focused) { PreviewSelected(); e.Handled = true; }
        }

        private void ShowListMenu(Point at)
        {
            if (_reader == null) return;
            var sel = SelectedRows();
            var m = new ContextMenuStrip { Renderer = new Theme.FlatRenderer(), Font = Theme.UiFont };
            void Add(string text, Action a, bool enabled = true) { var i = new ToolStripMenuItem(text) { Enabled = enabled }; i.Click += (o, e) => a(); m.Items.Add(i); }
            bool canMod = _reader.CanModify;
            if (sel.Count > 0)
            {
                bool img = sel.Count == 1 && !sel[0].IsDir && (ImageDecode.IsImage(sel[0].Name) || TextUtil.IsText(sel[0].Name));
                Add(img ? "미리보기" : "열기", OpenSelected, sel.Count == 1);
                if (sel.Count == 1 && !sel[0].IsDir) Add("다른 프로그램으로 열기", OpenExternally);
                Add("선택 항목 풀기…", ExtractDialog);
                m.Items.Add(new ToolStripSeparator());
                Add("이름 바꾸기", Rename, canMod && sel.Count == 1);
                Add("삭제", DeleteSelected, canMod);
            }
            else
            {
                Add("파일 추가…", AddFilesDialog, canMod);
                Add("새 폴더…", NewFolder, canMod);
                Add("모두 풀기…", ExtractDialog);
            }
            m.Show(_list, at);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LVITEM { public uint mask; public int iItem, iSubItem; public uint state, stateMask; public IntPtr pszText; public int cchTextMax, iImage; public IntPtr lParam; }
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref LVITEM l);

        private void SelectAll()
        {
            if (_search.Focused) { _search.SelectAll(); return; }
            var it = new LVITEM { mask = 8, state = 2, stateMask = 2 };
            SendMessage(_list.Handle, 0x1000 + 43, (IntPtr)(-1), ref it); // LVM_SETITEMSTATE on all items
            UpdateState();
        }

        // ------------------------------------------------------------ open / extract
        private void OpenSelected()
        {
            var sel = SelectedRows();
            if (sel.Count != 1) return;
            var r = sel[0];
            if (r.IsDir) { ShowFolder(r.Node, true); return; }
            if (ImageDecode.IsImage(r.Name)) { PreviewImage(r); return; }
            if (TextUtil.IsText(r.Name)) { using (var tv = new TextViewerForm(_reader, r.Entry)) tv.ShowDialog(this); return; }
            if (Shell.Formats.IsArchiveExtension(r.Name) && Kit.Confirm(this, "압축 파일 안의 압축 파일입니다. 풀어서 ArcBox로 열까요?\n(아니오: Windows 기본 프로그램으로 열기)"))
            {
                var p = ExtractToTemp(sel);
                if (p != null && p.Count == 1) OpenArchive(p[0]);
                return;
            }
            var paths = ExtractToTemp(sel);
            if (paths == null || paths.Count == 0) return;
            try
            {
                Kit.ShellOpen(paths[0]);
                _tempEdits.Add(new TempEdit { TempFile = paths[0], EntryPath = r.Entry.Path, Stamp = File.GetLastWriteTimeUtc(paths[0]) });
            }
            catch (Exception ex) { Kit.Error(this, "파일을 열 수 없습니다.\n" + ex.Message); }
        }

        /// <summary>Opens the image viewer on the given row; arrow keys browse the other images in the current list.</summary>
        private void PreviewImage(Row start)
        {
            var images = _rows.Where(x => !x.IsDir && ImageDecode.IsImage(x.Name)).ToList();
            int idx = images.IndexOf(start);
            if (idx < 0) return;
            using (var f = new ImageViewerForm(_reader, images.Select(x => x.Entry).ToList(), idx)) f.ShowDialog(this);
        }

        private void PreviewSelected()
        {
            var sel = SelectedRows();
            var r = sel.FirstOrDefault(x => !x.IsDir && (ImageDecode.IsImage(x.Name) || TextUtil.IsText(x.Name)));
            if (r == null) return;
            if (ImageDecode.IsImage(r.Name)) PreviewImage(r);
            else using (var tv = new TextViewerForm(_reader, r.Entry)) tv.ShowDialog(this);
        }

        /// <summary>Old behaviour: extract to a temp folder and open with the default Windows program.</summary>
        private void OpenExternally()
        {
            var sel = SelectedRows();
            if (sel.Count != 1 || sel[0].IsDir) return;
            var paths = ExtractToTemp(sel);
            if (paths == null || paths.Count == 0) return;
            try
            {
                Kit.ShellOpen(paths[0]);
                _tempEdits.Add(new TempEdit { TempFile = paths[0], EntryPath = sel[0].Entry.Path, Stamp = File.GetLastWriteTimeUtc(paths[0]) });
            }
            catch (Exception ex) { Kit.Error(this, "파일을 열 수 없습니다.\n" + ex.Message); }
        }

        private static string TempRoot => Path.Combine(Path.GetTempPath(), "ArcBox");

        /// <summary>Extracts rows into a fresh temp folder; returns the top-level paths.</summary>
        private List<string> ExtractToTemp(List<Row> rows)
        {
            string dir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(dir);
            _tempDirs.Add(dir);
            var entries = EntriesOf(rows);
            string strip = _search.Text.Trim().Length > 0 ? null : _current.Path;
            var res = new OpResult();
            var reader = _reader;
            var pf = ProgressForm.Run(this, "꺼내는 중", rows.Count == 1 ? rows[0].Name : rows.Count + "개 항목", null, false, ui =>
            {
                if (strip == null)
                {
                    // search results: flatten each item next to each other
                    foreach (var r in rows)
                    {
                        string parent = Path.GetDirectoryName(r.IsDir ? r.Node.Path : r.Entry.Path) ?? "";
                        reader.Extract(reader.PlanFor(EntriesOf(new[] { r }), dir, parent), OverwriteMode.Rename, ui, res);
                    }
                }
                else reader.Extract(reader.PlanFor(entries, dir, strip), OverwriteMode.Overwrite, ui, res);
            });
            if (pf.Error != null) { Kit.Error(this, Friendly(pf.Error)); return null; }
            if (pf.IsCancelled || res.Cancelled) return null;
            if (res.HasErrors) { ListDialog.Result(this, "꺼내기", res, null); if (res.FilesProcessed == 0) return null; }
            return rows.Select(r => Path.Combine(dir, PathUtil.SafeRelative(Path.GetFileName(r.Name)))).Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        }

        private void ExtractDialog()
        {
            if (_reader == null) return;
            var sel = SelectedRows();
            bool selection = sel.Count > 0 && sel.Count < _rows.Count || (sel.Count > 0 && _current != _root);
            using (var f = new ExtractForm(_reader.FilePath, selection))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                if (selection) ExtractRows(sel, Path.GetFullPath(f.Destination), f.Overwrite);
                else ExtractAll(Path.GetFullPath(f.Destination), f.SmartFolder ? ExtractTarget.Smart : ExtractTarget.Exact, f.Overwrite);
            }
        }

        private void ExtractQuick(ExtractTarget mode)
        {
            if (_reader == null) return;
            ExtractAll(Path.GetDirectoryName(_reader.FilePath), mode, AppSettings.Current.Overwrite);
        }

        private void ExtractAll(string baseDir, ExtractTarget mode, OverwriteMode ow)
        {
            var reader = _reader;
            string dest = Ops.ResolveDest(reader, baseDir, mode);
            RunExtract(reader.PlanFor(reader.Entries, dest, ""), dest, ow, baseDir);
        }

        private void ExtractRows(List<Row> rows, string dest, OverwriteMode ow)
        {
            string strip = _search.Text.Trim().Length > 0 ? "" : _current.Path;
            RunExtract(_reader.PlanFor(EntriesOf(rows), dest, strip), dest, ow, null);
        }

        private void RunExtract(Dictionary<int, string> plan, string dest, OverwriteMode ow, string archiveDir)
        {
            var reader = _reader;
            var res = new OpResult();
            var pf = ProgressForm.Run(this, "압축 푸는 중", reader.DisplayName, dest, true, ui => reader.Extract(plan, ow, ui, res));
            if (pf.Error != null) res.Errors.Add(Friendly(pf.Error));
            ListDialog.Result(this, "압축 풀기", res, null);
            bool sameDir = archiveDir != null && string.Equals(dest.TrimEnd('\\'), Path.GetDirectoryName(reader.FilePath)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            if (!res.Cancelled && !res.HasErrors && pf.OpenFolderChecked && !sameDir) Kit.OpenFolder(dest);
        }

        private void TestArchive()
        {
            if (_reader == null) return;
            var reader = _reader;
            var res = new OpResult();
            var pf = ProgressForm.Run(this, "테스트 중", reader.DisplayName, null, false, ui => reader.Test(ui, res));
            if (pf.Error != null) res.Errors.Add(Friendly(pf.Error));
            if (pf.IsCancelled || res.Cancelled) return;
            ListDialog.Result(this, "압축 파일 테스트", res, "검사를 마쳤습니다. 오류가 없습니다.\n(파일 " + res.FilesProcessed.ToString("N0") + "개 확인)");
        }

        private void ShowInfo()
        {
            if (_reader == null) return;
            ListDialog.Show(this, "압축 파일 정보", _reader.DisplayName, _reader.Info);
        }

        // ------------------------------------------------------------ compress / modify
        private void NewArchiveFromFiles()
        {
            using (var d = new OpenFileDialog { Title = "압축할 파일 선택", Multiselect = true })
                if (d.ShowDialog(this) == DialogResult.OK) CompressNew(d.FileNames);
        }

        private void NewArchiveFromFolder()
        {
            using (var d = new FolderBrowserDialog { Description = "압축할 폴더를 선택하세요" })
                if (d.ShowDialog(this) == DialogResult.OK) CompressNew(new[] { d.SelectedPath });
        }

        private void CompressNew(IList<string> files)
        {
            using (var f = new CompressForm(files))
            {
                f.StartPosition = FormStartPosition.CenterParent;
                if (f.ShowDialog(this) != DialogResult.OK) return;
                bool ok = Ops.Compress(this, files, f.ArchivePath, f.Options, f.Separately);
                if (ok && !f.Separately && File.Exists(f.ArchivePath)) OpenArchive(f.ArchivePath);
                else if (ok && f.Options.VolumeSize > 0) Kit.OpenFolder(Path.GetDirectoryName(f.ArchivePath));
            }
        }

        private bool EnsureModifiable()
        {
            if (_reader == null) return false;
            if (_reader.CanModify) return true;
            Kit.Error(this, _reader.Format.Name.ToUpperInvariant() + " 형식" + (_reader.IsSplitVolume ? "(분할 압축)" : "") + "은 수정할 수 없습니다.\n압축을 푼 뒤 다시 압축해 주세요.");
            return false;
        }

        private void RunModify(string verb, ModifyRequest req)
        {
            if (req.IsEmpty) return;
            var reader = _reader;
            string keep = _current?.Path;
            var res = new OpResult();
            var opts = new CompressOptions { Utf8Names = AppSettings.Current.Utf8Names, Level = 5 };
            var pf = ProgressForm.Run(this, verb, reader.DisplayName, null, false, ui => ArchiveWriter.Modify(reader, req, opts, ui, res));
            if (pf.Error != null) res.Errors.Add(Friendly(pf.Error));
            OpenArchive(reader.FilePath, _codePage, keep, reader.Password);
            ListDialog.Result(this, verb, res, null);
        }

        private void AddFilesDialog()
        {
            if (!EnsureModifiable()) return;
            using (var d = new OpenFileDialog { Title = "추가할 파일 선택", Multiselect = true })
                if (d.ShowDialog(this) == DialogResult.OK) AddPaths(d.FileNames, false);
        }

        private void AddFolderDialog()
        {
            if (!EnsureModifiable()) return;
            using (var d = new FolderBrowserDialog { Description = "추가할 폴더를 선택하세요" })
                if (d.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { d.SelectedPath }, false);
        }

        private void AddPaths(IList<string> paths, bool confirm)
        {
            if (!EnsureModifiable()) return;
            string where = _current?.Path ?? "";
            if (confirm && !Kit.Confirm(this, paths.Count + "개 항목을 '" + (where.Length == 0 ? _reader.DisplayName : where) + "'에 추가할까요?")) return;
            var scanRes = new OpResult();
            var items = ArchiveWriter.Scan(paths, where, scanRes, null, _reader.FilePath);
            if (items.Count == 0) { ListDialog.Result(this, "파일 추가", scanRes, "추가할 파일이 없습니다."); return; }
            var existing = new HashSet<string>(_reader.Entries.Where(e => !e.IsDir).Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
            int dup = items.Count(i => !i.IsDir && existing.Contains(i.ArchivePath));
            if (dup > 0 && !Kit.Confirm(this, "같은 이름의 파일 " + dup + "개가 이미 있습니다. 새 파일로 바꿀까요?")) return;
            var req = new ModifyRequest();
            req.Add.AddRange(items);
            RunModify("파일 추가하는 중", req);
            if (scanRes.Warnings.Count > 0) ListDialog.Result(this, "파일 추가", scanRes, null);
        }

        private void NewFolder()
        {
            if (!EnsureModifiable()) return;
            string name = InputDialog.Ask(this, "새 폴더", "폴더 이름", "새 폴더");
            if (string.IsNullOrEmpty(name)) return;
            if (!ValidName(name)) return;
            string path = (_current.Path.Length > 0 ? _current.Path + "\\" : "") + name;
            if (FindNode(path) != null || _reader.Entries.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))) { Kit.Error(this, "같은 이름이 이미 있습니다."); return; }
            var req = new ModifyRequest();
            req.Add.Add(new NewItem { ArchivePath = path, IsDir = true, Attributes = 0x10 });
            RunModify("폴더 만드는 중", req);
        }

        private bool ValidName(string name)
        {
            if (name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0 || name.Trim('.', ' ').Length == 0)
            {
                Kit.Error(this, "이름에 \\ / : * ? \" < > | 문자는 쓸 수 없습니다.");
                return false;
            }
            return true;
        }

        private void Rename()
        {
            if (!EnsureModifiable()) return;
            var sel = SelectedRows();
            if (sel.Count != 1) return;
            var r = sel[0];
            string oldName = r.IsDir ? r.Node.Name : r.Entry.Name;
            string name = InputDialog.Ask(this, "이름 바꾸기", "새 이름", oldName);
            if (string.IsNullOrEmpty(name) || name == oldName || !ValidName(name)) return;
            string oldPath = r.IsDir ? r.Node.Path : r.Entry.Path;
            string parent = Path.GetDirectoryName(oldPath) ?? "";
            string newPath = parent.Length > 0 ? parent + "\\" + name : name;
            if (!string.Equals(oldName, name, StringComparison.OrdinalIgnoreCase) &&
                (FindNode(newPath) != null || _reader.Entries.Any(e => string.Equals(e.Path, newPath, StringComparison.OrdinalIgnoreCase))))
            { Kit.Error(this, "같은 이름이 이미 있습니다."); return; }
            var req = new ModifyRequest();
            foreach (var e in EntriesOf(sel))
                req.Rename[e.Index] = newPath + e.Path.Substring(oldPath.Length);
            RunModify("이름 바꾸는 중", req);
        }

        private void DeleteSelected()
        {
            if (!EnsureModifiable()) return;
            var sel = SelectedRows();
            if (sel.Count == 0) return;
            var entries = EntriesOf(sel);
            string what = sel.Count == 1 ? "'" + sel[0].Name + "'" : sel.Count + "개 항목";
            if (!Kit.Confirm(this, what + "을(를) 압축 파일에서 삭제할까요?\n(파일 " + entries.Count(e => !e.IsDir) + "개, 되돌릴 수 없습니다)")) return;
            var req = new ModifyRequest();
            foreach (var e in entries) req.Delete.Add(e.Index);
            RunModify("삭제하는 중", req);
        }

        private void CheckTempEdits()
        {
            if (_reader == null || _tempEdits.Count == 0) return;
            foreach (var t in _tempEdits.ToList())
            {
                DateTime now;
                try { if (!File.Exists(t.TempFile)) { _tempEdits.Remove(t); continue; } now = File.GetLastWriteTimeUtc(t.TempFile); }
                catch { continue; }
                if (now <= t.Stamp) continue;
                t.Stamp = now;
                if (!_reader.CanModify) continue;
                if (!Kit.Confirm(this, "'" + Path.GetFileName(t.TempFile) + "' 파일이 수정되었습니다.\n압축 파일에 반영할까요?")) continue;
                var res = new OpResult();
                var items = ArchiveWriter.Scan(new[] { t.TempFile }, Path.GetDirectoryName(t.EntryPath) ?? "", res, null);
                if (items.Count == 0) { ListDialog.Result(this, "반영", res, null); continue; }
                items[0].ArchivePath = t.EntryPath;
                var req = new ModifyRequest();
                req.Add.Add(items[0]);
                RunModify("수정 내용 반영 중", req);
                return;
            }
        }

        // ------------------------------------------------------------ drag & drop
        private void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = !_draggingOut && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            if (_draggingOut || !(e.Data.GetData(DataFormats.FileDrop) is string[] files) || files.Length == 0) return;
            BeginInvoke((Action)(() =>
            {
                if (_reader == null)
                {
                    if (files.Length == 1 && File.Exists(files[0]) && Shell.Formats.IsArchiveExtension(files[0])) OpenArchive(files[0]);
                    else CompressNew(files);
                }
                else if (files.Length == 1 && File.Exists(files[0]) && Shell.Formats.IsArchiveExtension(files[0]) && !_reader.CanModify) OpenArchive(files[0]);
                else AddPaths(files, true);
            }));
        }

        private void DragOut()
        {
            var sel = SelectedRows();
            if (sel.Count == 0) return;
            var paths = ExtractToTemp(sel);
            if (paths == null || paths.Count == 0) return;
            _draggingOut = true;
            try
            {
                var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
                DoDragDrop(data, DragDropEffects.Copy);
            }
            finally { _draggingOut = false; }
        }

        // ------------------------------------------------------------ preview pane
        private int _previewToken;

        private void TogglePreview()
        {
            bool show = _split2.Panel2Collapsed;
            _split2.Panel2Collapsed = !show;
            AppSettings.Current.PreviewPane = show;
            AppSettings.Current.Save();
            if (show) UpdatePreview();
        }

        private void SchedulePreview()
        {
            if (_split2.Panel2Collapsed) return;
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private async void UpdatePreview()
        {
            if (_split2.Panel2Collapsed || _reader == null || _current == null) return;
            int token = ++_previewToken;
            int big = Theme.S(96);
            var sel = SelectedRows();
            if (sel.Count == 0)
            {
                var n = _current;
                _preview.ShowIcon(n == _root ? Glyphs.Get(Glyphs.NewArchive, big, Theme.P.Accent, "A") : ShellIcons.LargeIcon("x", true, big));
                _preview.SetInfo(n == _root ? _reader.DisplayName : n.Name, new[]
                {
                    "파일 " + n.Files.Count.ToString("N0") + "개, 폴더 " + n.Dirs.Count.ToString("N0") + "개",
                    "크기  " + PathUtil.FormatSize(n.TotalSize),
                });
                return;
            }
            if (sel.Count > 1)
            {
                ulong total = 0;
                foreach (var x in sel) total += x.IsDir ? x.Node.TotalSize : x.Size;
                _preview.ShowIcon(ShellIcons.LargeIcon("x", false, big));
                _preview.SetInfo(sel.Count.ToString("N0") + "개 항목 선택됨", new[] { "크기  " + PathUtil.FormatSize(total) });
                return;
            }
            var r = sel[0];
            _preview.ShowIcon(ShellIcons.LargeIcon(r.Name, r.IsDir, big));
            var lines = new List<string> { ShellIcons.TypeName(r.Name, r.IsDir) };
            if (r.IsDir)
            {
                lines.Add("파일 " + r.Node.Files.Count.ToString("N0") + "개, 폴더 " + r.Node.Dirs.Count.ToString("N0") + "개");
                lines.Add("크기  " + PathUtil.FormatSize(r.Node.TotalSize));
                if (r.MTime.HasValue) lines.Add("수정한 날짜  " + r.MTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                _preview.SetInfo(r.Name, lines);
                return;
            }
            var e = r.Entry;
            lines.Add("크기  " + PathUtil.FormatSize(e.Size) + (e.PackedSize.HasValue ? "  (압축 " + PathUtil.FormatSize(e.PackedSize.Value) + ")" : ""));
            if (e.MTime.HasValue) lines.Add("수정한 날짜  " + e.MTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            if (e.Crc.HasValue) lines.Add("CRC32  " + e.Crc.Value.ToString("X8"));
            if (!string.IsNullOrEmpty(e.Method)) lines.Add("압축 방식  " + e.Method);
            if (e.Encrypted) lines.Add("암호  있음");
            _preview.SetInfo(r.Name, lines);

            bool isImg = ImageDecode.IsImage(r.Name) && e.Size <= 50UL * 1024 * 1024;
            bool isText = TextUtil.IsText(r.Name) && e.Size <= 8UL * 1024 * 1024;
            if (!isImg && !isText) return;
            if (e.Encrypted && _reader.Password == null) { lines.Add("암호가 걸려 있어 미리볼 수 없습니다. 열기(Enter)로 보세요."); _preview.SetInfo(r.Name, lines); return; }
            var reader = _reader;
            try
            {
                var data = await Task.Run(() => reader.ReadEntryBytes(e, new SilentUI()));
                if (token != _previewToken || data == null) return;
                if (isImg)
                {
                    var img = await Task.Run(() => ImageDecode.Decode(data, r.Name));
                    if (token != _previewToken) { img.Dispose(); return; }
                    lines.Insert(1, img.Width + " × " + img.Height);
                    _preview.ShowImage(img);
                    _preview.SetInfo(r.Name, lines);
                }
                else
                {
                    string text = await Task.Run(() =>
                    {
                        string t = TextUtil.Decode(data, 0, out _);
                        return TextUtil.Lines(t.Length > 200000 ? t.Substring(0, 200000) + "\n…" : t);
                    });
                    if (token != _previewToken) return;
                    _preview.ShowText(text);
                }
            }
            catch (Exception ex)
            {
                if (token != _previewToken) return;
                lines.Add("미리볼 수 없습니다: " + ex.Message);
                _preview.SetInfo(r.Name, lines);
            }
        }

        /// <summary>Right-hand pane: picture / text / file details of the selection.</summary>
        private sealed class PreviewPane : Panel
        {
            private readonly PictureBox _pic;
            private readonly TextBox _text;
            private readonly Label _title, _details;
            private readonly Panel _info;
            private bool _ownsImage;

            public PreviewPane()
            {
                Padding = new Padding(Theme.S(12), Theme.S(12), Theme.S(12), Theme.S(8));
                _pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
                _text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = true, BorderStyle = BorderStyle.None, Visible = false, Font = TextUtil.Mono };
                _info = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(60), Padding = new Padding(0, Theme.S(10), 0, 0) };
                _title = new Label { Dock = DockStyle.Top, Height = Theme.S(24), AutoEllipsis = true, Font = Theme.UiBold, UseMnemonic = false };
                _details = new Label { Dock = DockStyle.Fill, Font = Theme.UiFont, UseMnemonic = false };
                _info.Controls.Add(_details);
                _info.Controls.Add(_title);
                Controls.Add(_pic);
                Controls.Add(_text);
                Controls.Add(_info);
            }

            public void ApplyTheme()
            {
                var p = Theme.P;
                BackColor = _info.BackColor = p.Bg;
                _text.BackColor = p.Bg; _text.ForeColor = p.Text;
                _title.ForeColor = p.Text; _details.ForeColor = p.Sub;
            }

            private void SetImage(Image img, bool owned)
            {
                var old = _pic.Image;
                bool oldOwned = _ownsImage;
                _pic.Image = img;
                _ownsImage = owned;
                if (oldOwned && old != null && old != img) old.Dispose();
            }

            public void ShowIcon(Image icon)
            {
                SetImage(icon, false);
                _pic.SizeMode = PictureBoxSizeMode.CenterImage;
                _pic.Visible = true; _text.Visible = false;
            }

            public void ShowImage(Image img)
            {
                SetImage(img, true);
                _pic.SizeMode = img.Width <= _pic.Width && img.Height <= _pic.Height ? PictureBoxSizeMode.CenterImage : PictureBoxSizeMode.Zoom;
                _pic.Visible = true; _text.Visible = false;
            }

            public void ShowText(string text)
            {
                SetImage(null, false);
                _text.Text = text;
                _text.Select(0, 0);
                _text.Visible = true; _pic.Visible = false;
            }

            public void SetInfo(string title, IEnumerable<string> lines)
            {
                _title.Text = title;
                var list = lines.ToList();
                _details.Text = string.Join("\n", list);
                _info.Height = Theme.S(10) + Theme.S(24) + list.Count * (Theme.UiFont.Height + Theme.S(3)) + Theme.S(4);
            }

            public void Clear()
            {
                SetImage(null, false);
                _text.Text = "";
                _title.Text = _details.Text = "";
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) SetImage(null, false);
                base.Dispose(disposing);
            }
        }

        // ------------------------------------------------------------ view modes / thumbnails
        private IEnumerable<ToolStripMenuItem> ViewMenuItems(bool shortcuts = true)
        {
            var defs = new[]
            {
                ("xlarge", "아주 큰 아이콘", Keys.Control | Keys.Shift | Keys.D1),
                ("large", "큰 아이콘", Keys.Control | Keys.Shift | Keys.D2),
                ("medium", "중간 아이콘", Keys.Control | Keys.Shift | Keys.D3),
                ("details", "자세히", Keys.Control | Keys.Shift | Keys.D6),
            };
            foreach (var d in defs)
            {
                var mode = d.Item1;
                var mi = new ToolStripMenuItem(d.Item2) { Tag = mode };
                if (shortcuts) mi.ShortcutKeys = d.Item3;
                else mi.ShortcutKeyDisplayString = "Ctrl+Shift+" + (d.Item3 & Keys.KeyCode).ToString().Substring(1);
                mi.Click += (o, e) => SetViewMode(mode);
                _viewItems.Add(mi);
                yield return mi;
            }
        }

        private void SyncViewChecks()
        {
            foreach (var mi in _viewItems) mi.Checked = (string)mi.Tag == _viewMode;
            if (_miPreview != null) _miPreview.Checked = !_split2.Panel2Collapsed;
            if (_miPreview2 != null) _miPreview2.Checked = !_split2.Panel2Collapsed;
        }

        private int ThumbSize => Theme.S(_viewMode == "xlarge" ? 192 : _viewMode == "large" ? 96 : 48);

        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        private void SetViewMode(string mode)
        {
            if (Array.IndexOf(ViewModes, mode) < 0) mode = "details";
            _viewMode = mode;
            ResetThumbs();
            if (mode == "details")
            {
                _list.View = View.Details;
                _list.OwnerDraw = true;
            }
            else
            {
                int sz = ThumbSize;
                var old = _bigImages;
                _bigImages = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(sz, sz) };
                _list.LargeImageList = _bigImages;
                old?.Dispose();
                _list.OwnerDraw = false;
                _list.View = View.LargeIcon;
                ApplyIconSpacing();
            }
            _list.Invalidate();
            if (AppSettings.Current.ViewMode != mode) { AppSettings.Current.ViewMode = mode; AppSettings.Current.Save(); }
        }

        private void ApplyIconSpacing()
        {
            if (_viewMode == "details" || !_list.IsHandleCreated) return;
            int sz = ThumbSize;
            int cx = sz + Theme.S(_viewMode == "medium" ? 40 : 24), cy = sz + Theme.S(46);
            SendMessage(_list.Handle, 0x1000 + 53, IntPtr.Zero, (IntPtr)((cy << 16) | (cx & 0xFFFF))); // LVM_SETICONSPACING
            _list.Invalidate();
        }

        private void ResetThumbs()
        {
            lock (_thumbStack) { _thumbGen++; _thumbStack.Clear(); _thumbQueued.Clear(); }
            _imgIndex.Clear();
            _bigImages?.Images.Clear();
        }

        private int AddBigImage(string key, Bitmap b)
        {
            _bigImages.Images.Add(b);
            return _imgIndex[key] = _bigImages.Images.Count - 1;
        }

        private int TypeImage(Row row)
        {
            string key = row.IsDir ? "<dir>" : "x" + Path.GetExtension(row.Name).ToLowerInvariant();
            return _imgIndex.TryGetValue(key, out int i) ? i : AddBigImage(key, ShellIcons.LargeIcon(row.Name, row.IsDir, ThumbSize));
        }

        private bool CanThumb(Row row) =>
            !row.IsDir && row.Entry != null && ImageDecode.IsImage(row.Name) && row.Entry.Size <= 80UL * 1024 * 1024 &&
            (!row.Entry.Encrypted || _reader?.Password != null);

        private int BigImageFor(Row row)
        {
            if (_bigImages == null) return -1;
            if (CanThumb(row))
            {
                if (_imgIndex.TryGetValue("t" + row.Entry.Index, out int ti)) return ti;
                QueueThumb(row.Entry);
            }
            return TypeImage(row);
        }

        private void QueueThumb(ArchiveEntry e)
        {
            if (_reader == null) return;
            lock (_thumbStack)
            {
                if (!_thumbQueued.Add(e.Index)) return;
                _thumbStack.Push(e); // newest request first: what is on screen now
                if (_thumbRunning) return;
                _thumbRunning = true;
            }
            var reader = _reader;
            int gen = _thumbGen, sz = ThumbSize;
            Task.Run(() => ThumbLoop(reader, gen, sz));
        }

        private void ThumbLoop(ArchiveReader reader, int gen, int sz)
        {
            while (true)
            {
                var batch = new List<ArchiveEntry>();
                lock (_thumbStack)
                {
                    if (gen != _thumbGen || _thumbStack.Count == 0) { _thumbRunning = false; return; }
                    while (batch.Count < 8 && _thumbStack.Count > 0) batch.Add(_thumbStack.Pop());
                }
                Dictionary<int, byte[]> data;
                try { data = reader.ReadEntriesBytes(batch.OrderBy(x => x.Index).ToList(), new SilentUI()); }
                catch (Exception ex) { Log.Write("thumb: " + ex.Message); data = new Dictionary<int, byte[]>(); }
                var done = new List<KeyValuePair<ArchiveEntry, Bitmap>>();
                foreach (var e in batch)
                {
                    Bitmap thumb = null;
                    if (data.TryGetValue(e.Index, out var bytes))
                    {
                        try { using (var img = ImageDecode.Decode(bytes, e.Name)) thumb = MakeThumb(img, sz); }
                        catch { }
                    }
                    done.Add(new KeyValuePair<ArchiveEntry, Bitmap>(e, thumb));
                }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        bool current;
                        lock (_thumbStack) current = gen == _thumbGen;
                        if (!current || _bigImages == null) { foreach (var kv in done) kv.Value?.Dispose(); return; }
                        if (_bigImages.Images.Count > 1500) { ResetThumbs(); _list.Invalidate(); return; }
                        foreach (var kv in done)
                        {
                            string key = "t" + kv.Key.Index;
                            if (kv.Value != null) { AddBigImage(key, kv.Value); kv.Value.Dispose(); }
                            else _imgIndex[key] = TypeImage(new Row { Name = kv.Key.Name, Entry = kv.Key });
                        }
                        _list.Invalidate();
                    }));
                }
                catch { lock (_thumbStack) _thumbRunning = false; return; } // window closed
            }
        }

        private static Bitmap MakeThumb(Image img, int sz)
        {
            var b = new Bitmap(sz, sz);
            using (var g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int pad = Math.Max(2, sz / 32);
                float scale = Math.Min((float)(sz - pad * 2) / img.Width, (float)(sz - pad * 2) / img.Height);
                scale = Math.Min(scale, 1f * Math.Max(1, sz / 48)); // tiny images are not blown up too much
                int w = Math.Max(1, (int)(img.Width * scale)), h = Math.Max(1, (int)(img.Height * scale));
                var r = new Rectangle((sz - w) / 2, (sz - h) / 2, w, h);
                g.DrawImage(img, r);
                using (var pen = new Pen(Color.FromArgb(60, 128, 128, 128))) g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
            }
            return b;
        }

        private sealed class SilentUI : IOperationUI
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

        /// <summary>TreeView without a horizontal scrollbar: long names are shortened with "…" instead of scrolling the pane sideways.</summary>
        private sealed class NoHScrollTree : TreeView
        {
            protected override CreateParams CreateParams
            {
                get { var cp = base.CreateParams; cp.Style |= 0x8000; return cp; } // TVS_NOHSCROLL
            }
        }

        // ------------------------------------------------------------ misc
        private void ShowSettings(string page)
        {
            using (var f = new SettingsForm(page)) f.ShowDialog(this);
        }

        private void About()
        {
            MessageBox.Show(this, "ArcBox " + Application.ProductVersion + "\n\n가볍고 안전한 압축 프로그램\n압축 엔진: 7-Zip (Igor Pavlov, GNU LGPL + unRAR 제한)\n\n설정 · 로그: " + AppSettings.Dir,
                "ArcBox 정보", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SaveWindow()
        {
            var s = AppSettings.Current;
            s.WindowMax = WindowState == FormWindowState.Maximized;
            if (WindowState == FormWindowState.Normal)
            {
                s.WindowW = (int)(ClientSize.Width / Theme.Scale);
                s.WindowH = (int)(ClientSize.Height / Theme.Scale);
            }
            if (_split.Visible) s.TreeWidth = (int)(_split.SplitterDistance / Theme.Scale);
            if (_split.Visible && !_split2.Panel2Collapsed) s.PreviewWidth = (int)(_split2.Panel2.Width / Theme.Scale);
            s.Save();
        }

        private void CleanupTemp()
        {
            foreach (var d in _tempDirs)
            {
                try { Directory.Delete(d, true); } catch { } // files still open in other apps stay; cleaned on next start
            }
        }

        public static void CleanupStaleTemp()
        {
            try
            {
                if (!Directory.Exists(TempRoot)) return;
                foreach (var d in Directory.GetDirectories(TempRoot))
                {
                    try { if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddDays(-1)) Directory.Delete(d, true); } catch { }
                }
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Theme.Changed -= ApplyTheme; _searchTimer.Dispose(); }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------ small custom controls
        /// <summary>Panel with a 1px rounded border (address and search boxes).</summary>
        private sealed class BorderPanel : Panel
        {
            public string Glyph;
            public BorderPanel() { DoubleBuffered = true; ResizeRedraw = true; }
            protected override void OnPaint(PaintEventArgs e)
            {
                var p = Theme.P;
                BackColor = p.Field;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(5)))
                using (var pen = new Pen(p.Input)) e.Graphics.DrawPath(pen, path);
                if (Glyph != null)
                {
                    int s = Theme.S(13);
                    e.Graphics.DrawImage(Glyphs.Get(Glyph, s, p.Sub, "?"), Theme.S(8), (Height - s) / 2);
                }
            }
        }

        /// <summary>Rounded dashed drop target shown when no archive is open.</summary>
        private sealed class DropZone : Panel
        {
            public DropZone() { DoubleBuffered = true; ResizeRedraw = true; }
            protected override void OnPaint(PaintEventArgs e)
            {
                var p = Theme.P;
                BackColor = p.Bg;
                Parent.BackColor = p.Side;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Theme.Round(new Rectangle(1, 1, Width - 3, Height - 3), Theme.S(14)))
                using (var pen = new Pen(p.Dash, Theme.S(2)) { DashStyle = DashStyle.Dash })
                    e.Graphics.DrawPath(pen, path);
            }
        }
    }
}

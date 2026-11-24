using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ArcBox.Shell;

namespace ArcBox.UI
{
    public sealed class SettingsForm : Form
    {
        private readonly Panel _content;
        private readonly Dictionary<string, Control> _pages = new Dictionary<string, Control>();
        private readonly Dictionary<string, Button> _nav = new Dictionary<string, Button>();
        private readonly Dictionary<string, CheckBox> _assoc = new Dictionary<string, CheckBox>();
        private readonly Dictionary<string, CheckBox> _menuItems = new Dictionary<string, CheckBox>();
        private RadioButton _themeSystem, _themeLight, _themeDark;
        private RadioButton _dblOpen, _dblSmart, _dblFolder, _dblMenu;
        private CheckBox _openAfter, _utf8, _menuOn;
        private Label _userChoiceNote;

        public SettingsForm(string page = "파일 연결")
        {
            Kit.InitDialog(this, "ArcBox 설정", 820, 640);
            ShowInTaskbar = true;
            var nav = new FlowLayoutPanel { Dock = DockStyle.Left, Width = Theme.S(160), FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(Theme.S(8), Theme.S(10), Theme.S(8), 0), Tag = "side" };
            var navLine = new Panel { Dock = DockStyle.Left, Width = 1, Tag = "line" };
            _content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(20), Theme.S(16), Theme.S(20), Theme.S(8)), AutoScroll = true };

            AddPage(nav, "일반", BuildGeneral());
            AddPage(nav, "파일 연결", BuildAssoc());
            AddPage(nav, "탐색기 메뉴", BuildMenu());

            var ok = Kit.Btn("확인", true); var cancel = Kit.Btn("취소"); var apply = Kit.Btn("적용");
            cancel.DialogResult = DialogResult.Cancel;
            ok.Click += (s, e) => { if (Apply()) DialogResult = DialogResult.OK; };
            apply.Click += (s, e) => Apply();
            CancelButton = cancel;

            Controls.Add(_content);
            Controls.Add(navLine);
            Controls.Add(nav);
            Controls.Add(Kit.Footer(null, ok, cancel, apply));
            Theme.Apply(this);
            ShowPage(_pages.ContainsKey(page) ? page : "파일 연결");
        }

        private void AddPage(FlowLayoutPanel nav, string name, Control page)
        {
            var b = new Button
            {
                Text = name, TextAlign = ContentAlignment.MiddleLeft, Width = Theme.S(144), Height = Theme.S(32), FlatStyle = FlatStyle.Flat,
                Font = Theme.UiFont, Margin = new Padding(0, 0, 0, Theme.S(2)), Padding = new Padding(Theme.S(6), 0, 0, 0)
            };
            b.Click += (s, e) => ShowPage(name);
            nav.Controls.Add(b);
            _nav[name] = b;
            page.Dock = DockStyle.Top;
            page.Visible = false;
            _content.Controls.Add(page);
            _pages[name] = page;
        }

        private void ShowPage(string name)
        {
            foreach (var kv in _pages) kv.Value.Visible = kv.Key == name;
            foreach (var kv in _nav)
            {
                bool on = kv.Key == name;
                kv.Value.FlatAppearance.BorderSize = 0;
                kv.Value.BackColor = on ? Theme.P.Hover : Theme.P.Side;
                kv.Value.ForeColor = Theme.P.Text;
                kv.Value.FlatAppearance.MouseOverBackColor = Theme.P.Hover;
                kv.Value.Font = on ? Theme.UiBold : Theme.UiFont;
            }
        }

        private static TableLayoutPanel Page(string title, string desc)
        {
            var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0) };
            var h = Kit.Lbl(title, null, true); h.Font = Theme.TitleFont;
            Kit.AddRow(t, h, 0);
            if (desc != null) Kit.AddRow(t, Kit.Lbl(desc, "sub"), 10);
            return t;
        }

        private static Control Section(string title)
        {
            var p = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, Theme.S(6), 0, Theme.S(2)) };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var l = Kit.Lbl(title, "accent", true);
            var rule = new Panel { Height = 1, Anchor = AnchorStyles.Left | AnchorStyles.Right, Tag = "rule", Margin = new Padding(Theme.S(8), 0, 0, 0) };
            p.Controls.Add(l, 0, 0); p.Controls.Add(rule, 1, 0);
            return p;
        }

        // ------------------------------------------------------------ pages
        private Control BuildGeneral()
        {
            var s = AppSettings.Current;
            var t = Page("일반", null);
            Kit.AddRow(t, Section("테마"), 2);
            _themeSystem = Kit.Radio("Windows 설정 따르기", s.Theme == "system");
            _themeLight = Kit.Radio("라이트", s.Theme == "light");
            _themeDark = Kit.Radio("다크", s.Theme == "dark");
            Kit.AddRow(t, Kit.Row(_themeSystem, _themeLight, _themeDark), 8);
            Kit.AddRow(t, Section("동작"), 2);
            _openAfter = Kit.Chk("압축을 푼 뒤 폴더 열기", s.OpenFolderAfterExtract);
            _utf8 = Kit.Chk("ZIP 파일 이름을 UTF-8로 저장 (권장)", s.Utf8Names);
            Kit.AddRow(t, _openAfter, 0);
            Kit.AddRow(t, _utf8, 8);
            Kit.AddRow(t, Section("정보"), 2);
            string engine = "";
            try { engine = System.IO.Path.GetFileName(Core.SevenZipLib.Instance.LibraryPath) + " · 지원 형식 " + Core.SevenZipLib.Instance.Formats.Count + "개"; }
            catch (Exception ex) { engine = "엔진을 불러오지 못함: " + ex.Message; }
            Kit.AddRow(t, Kit.Lbl("ArcBox " + Application.ProductVersion + " · 압축 엔진: 7-Zip (" + engine + ")", "sub"), 0);
            return t;
        }

        private Control BuildAssoc()
        {
            var s = AppSettings.Current;
            var t = Page("파일 연결", "체크한 형식은 ArcBox 아이콘으로 표시되고, 더블클릭하면 아래에서 고른 동작을 합니다.");
            Kit.AddRow(t, Section("압축 파일을 더블클릭하면"), 2);
            _dblOpen = Kit.Radio("ArcBox로 열기", s.DoubleClick != "smart" && s.DoubleClick != "folder" && s.DoubleClick != "menu");
            _dblSmart = Kit.Radio("여기에 풀기 (스마트)", s.DoubleClick == "smart");
            _dblFolder = Kit.Radio("폴더로 풀기", s.DoubleClick == "folder");
            _dblMenu = Kit.Radio("메뉴 보이기", s.DoubleClick == "menu");
            Kit.AddRow(t, Kit.Row(_dblOpen, _dblSmart, _dblFolder, _dblMenu), 8);
            bool anyUnsupported = false;
            foreach (var group in Formats.All.GroupBy(f => f.Group))
            {
                Kit.AddRow(t, Section(group.Key), 2);
                var grid = new TableLayoutPanel { ColumnCount = 5, AutoSize = true, Dock = DockStyle.Fill };
                for (int i = 0; i < 5; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
                int n = 0;
                foreach (var f in group)
                {
                    bool supported = Formats.EngineSupports(f.Ext);
                    if (!supported) anyUnsupported = true;
                    var cb = Kit.Chk(f.Label + (supported ? "" : " *"), s.Associated.Contains(f.Ext));
                    _assoc[f.Ext] = cb;
                    grid.Controls.Add(cb, n % 5, n / 5);
                    n++;
                }
                Kit.AddRow(t, grid, 6);
            }
            if (anyUnsupported)
                Kit.AddRow(t, Kit.Lbl("* 표시 형식은 아이콘·연결만 되고, 해제 엔진은 업데이트로 추가될 예정입니다.", "sub"), 6);

            var all = Kit.Btn("모두 선택"); var none = Kit.Btn("모두 해제"); var rec = Kit.Btn("권장 설정");
            all.Margin = none.Margin = rec.Margin = new Padding(0, 0, Theme.S(6), 0);
            all.Click += (o, e) => { foreach (var c in _assoc.Values) c.Checked = true; };
            none.Click += (o, e) => { foreach (var c in _assoc.Values) c.Checked = false; };
            rec.Click += (o, e) => { foreach (var f in Formats.All) _assoc[f.Ext].Checked = f.DefaultOn; };
            var link = new LinkLabel { Text = "Windows 기본 앱 설정 열기", AutoSize = true, Font = Theme.UiFont, Margin = new Padding(Theme.S(12), Theme.S(7), 0, 0) };
            link.LinkClicked += (o, e) =>
            {
                try { Kit.ShellOpen("ms-settings:defaultapps?registeredAppUser=ArcBox"); }
                catch { try { Kit.ShellOpen("ms-settings:defaultapps"); } catch { } }
            };
            Kit.AddRow(t, Kit.Row(all, none, rec, link), 6);
            _userChoiceNote = Kit.Lbl("", "accent");
            _userChoiceNote.MaximumSize = new Size(Theme.S(600), 0);
            Kit.AddRow(t, _userChoiceNote, 0);
            UpdateUserChoiceNote();
            return t;
        }

        private void UpdateUserChoiceNote()
        {
            var over = Registration.OverriddenByUserChoice(AppSettings.Current);
            _userChoiceNote.Text = over.Count == 0 ? "" :
                "Windows에서 다른 프로그램을 직접 기본값으로 고른 형식: " + string.Join(", ", over.Select(x => x.ToUpperInvariant()).Take(12)) +
                (over.Count > 12 ? " 외" : "") + "\n이 형식은 위 링크에서 ArcBox를 기본 앱으로 선택하세요. (Windows 정책상 프로그램이 대신 바꿀 수 없습니다)";
        }

        private Control BuildMenu()
        {
            var s = AppSettings.Current;
            var t = Page("탐색기 메뉴", "파일을 오른쪽 클릭했을 때 나오는 메뉴입니다. Windows 11에서는 '더 많은 옵션 표시' 안에 나타납니다.");
            _menuOn = Kit.Chk("탐색기 오른쪽 클릭 메뉴 사용", s.ShellMenu);
            Kit.AddRow(t, _menuOn, 6);
            Kit.AddRow(t, Section("압축 파일 메뉴 (ArcBox로 풀기)"), 2);
            foreach (var it in ShellMenu.AllItems.Where(i => i.ForArchives)) Kit.AddRow(t, _menuItems[it.Id] = Kit.Chk(it.Text, s.MenuItems.Contains(it.Id)), 0);
            Kit.AddRow(t, Section("모든 파일·폴더 메뉴 (ArcBox로 압축)"), 2);
            foreach (var it in ShellMenu.AllItems.Where(i => !i.ForArchives)) Kit.AddRow(t, _menuItems[it.Id] = Kit.Chk(it.Text, s.MenuItems.Contains(it.Id)), 0);
            _menuOn.CheckedChanged += (o, e) => { foreach (var c in _menuItems.Values) c.Enabled = _menuOn.Checked; };
            foreach (var c in _menuItems.Values) c.Enabled = _menuOn.Checked;
            return t;
        }

        private bool Apply()
        {
            var s = AppSettings.Current;
            string theme = _themeLight.Checked ? "light" : _themeDark.Checked ? "dark" : "system";
            bool themeChanged = theme != s.Theme;
            s.Theme = theme;
            s.OpenFolderAfterExtract = _openAfter.Checked;
            s.Utf8Names = _utf8.Checked;
            s.ShellMenu = _menuOn.Checked;
            s.DoubleClick = _dblSmart.Checked ? "smart" : _dblFolder.Checked ? "folder" : _dblMenu.Checked ? "menu" : "open";
            s.Associated = new HashSet<string>(_assoc.Where(kv => kv.Value.Checked).Select(kv => kv.Key));
            s.MenuItems = new HashSet<string>(_menuItems.Where(kv => kv.Value.Checked).Select(kv => kv.Key));
            s.Save();
            try
            {
                Cursor = Cursors.WaitCursor;
                Registration.Apply(s);
            }
            catch (Exception ex)
            {
                Kit.Error(this, "Windows 등록 정보를 저장하지 못했습니다.\n" + ex.Message);
                return false;
            }
            finally { Cursor = Cursors.Default; }
            UpdateUserChoiceNote();
            if (themeChanged)
            {
                Theme.SetMode(theme);
                Theme.Apply(this);
                foreach (var n in _nav.Keys) if (_pages[n].Visible) ShowPage(n);
            }
            return true;
        }
    }
}

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace ArcBox.UI
{
    /// <summary>Small popup shown at the mouse when an archive is double-clicked (setting "메뉴 보이기").</summary>
    public static class QuickMenu
    {
        /// <returns>The command verb to run ("/open", "/x", ...), or "" when the menu was dismissed.</returns>
        public static string Choose(string archive)
        {
            string result = "";
            string name = archive == null ? "" : Path.GetFileName(archive);

            // invisible owner window so the menu gets focus and closes on an outside click
            using (var owner = new Form
            {
                FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Size = new Size(1, 1), Location = Cursor.Position, Opacity = 0, TopMost = true, Text = "ArcBox",
            })
            {
                var m = new ContextMenuStrip { Renderer = new Theme.FlatRenderer(), Font = Theme.UiFont, ShowImageMargin = false };
                if (name.Length > 0)
                {
                    var head = new ToolStripLabel(name.Length > 48 ? name.Substring(0, 45) + "…" : name) { Font = Theme.UiBold, ForeColor = Theme.P.Sub };
                    m.Items.Add(head);
                    m.Items.Add(new ToolStripSeparator());
                }
                void Add(string text, string verb, bool bold = false)
                {
                    var i = new ToolStripMenuItem(text);
                    if (bold) i.Font = Theme.UiBold;
                    i.Click += (o, e) => result = verb;
                    m.Items.Add(i);
                }
                Add("ArcBox로 열기", "/open", true);
                m.Items.Add(new ToolStripSeparator());
                Add("여기에 풀기 (스마트)", "/x");
                Add("폴더로 풀기", "/xf");
                Add("압축 풀기…", "/xd");
                m.Items.Add(new ToolStripSeparator());
                Add("압축 파일 테스트", "/t");

                m.Closed += (o, e) => owner.BeginInvoke((Action)owner.Close);
                owner.Shown += (o, e) =>
                {
                    owner.Activate();
                    m.Show(owner, Point.Empty);
                };
                Application.Run(owner);
                m.Dispose();
            }
            return result;
        }
    }
}

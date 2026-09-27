using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace KosherExplorer
{
    class BaseDialog : Form
    {
        public BaseDialog()
        {
            RightToLeft = RightToLeft.Yes; RightToLeftLayout = true;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Color.White;
        }

        protected Button AddButton(string text, int x, int y, int w = 100, DialogResult r = DialogResult.None)
        {
            var b = new Button { Text = text, DialogResult = r };
            b.SetBounds(x, y, w, 30);
            Controls.Add(b);
            return b;
        }

        protected Label AddLabel(string text, int x, int y, int w, int h = 22)
        {
            var l = new Label { Text = text };
            l.SetBounds(x, y, w, h);
            Controls.Add(l);
            return l;
        }
    }

    class PasswordForm : BaseDialog
    {
        static int failures;
        static DateTime lockedUntil;

        readonly TextBox p1 = new TextBox { UseSystemPasswordChar = true };
        readonly TextBox p2 = new TextBox { UseSystemPasswordChar = true };
        readonly Label err = new Label { ForeColor = Color.Firebrick };
        readonly bool setMode;
        readonly AppConfig cfg;

        /// <summary>setMode: choose a new password (twice). Otherwise verify the existing one.</summary>
        public PasswordForm(AppConfig cfg, bool setMode, string title = null)
        {
            this.cfg = cfg; this.setMode = setMode;
            Text = title ?? (setMode ? "הגדרת סיסמת מנהל" : "כניסת מנהל");
            ClientSize = new Size(380, setMode ? 220 : 170);
            int y = 16;
            AddLabel(setMode ? "בחרו סיסמת מנהל (לפחות 4 תווים):" : "הקלידו את סיסמת המנהל:", 16, y, 348);
            y += 26; p1.SetBounds(16, y, 348, 26); Controls.Add(p1); y += 36;
            if (setMode)
            {
                AddLabel("הקלידו שוב לאימות:", 16, y, 348); y += 26;
                p2.SetBounds(16, y, 348, 26); Controls.Add(p2); y += 36;
            }
            err.SetBounds(16, y, 348, 22); Controls.Add(err); y += 28;
            var ok = AddButton("אישור", 158, y, 100);
            var cancel = AddButton("ביטול", 264, y, 100, DialogResult.Cancel);
            AcceptButton = ok; CancelButton = cancel;
            ok.Click += (s, e) => Submit();
            ClientSize = new Size(380, y + 44);
        }

        void Submit()
        {
            if (setMode)
            {
                if (p1.Text.Length < 4) { err.Text = "הסיסמה קצרה מדי."; return; }
                if (p1.Text != p2.Text) { err.Text = "הסיסמאות אינן תואמות."; return; }
                Pwd.Set(cfg, p1.Text);
                DialogResult = DialogResult.OK;
                return;
            }
            if (DateTime.Now < lockedUntil)
            {
                err.Text = "יותר מדי ניסיונות. נסו שוב בעוד " + Math.Ceiling((lockedUntil - DateTime.Now).TotalSeconds) + " שניות.";
                return;
            }
            Cursor = Cursors.WaitCursor;
            bool good = Pwd.Verify(cfg, p1.Text);
            Cursor = Cursors.Default;
            if (good) { failures = 0; DialogResult = DialogResult.OK; return; }
            failures++;
            p1.SelectAll(); p1.Focus();
            if (failures >= 5) { lockedUntil = DateTime.Now.AddSeconds(60); failures = 0; err.Text = "יותר מדי ניסיונות. המתינו דקה."; }
            else err.Text = "סיסמה שגויה.";
        }

        /// <summary>Asks for the admin password (or to create one on first use). True = admin.</summary>
        public static bool Authenticate(IWin32Window owner, AppConfig cfg)
        {
            if (!Pwd.IsSet(cfg))
            {
                MessageBox.Show(owner, "זו הפעם הראשונה שנכנסים להגדרות.\nבחרו עכשיו סיסמת מנהל. בלעדיה אי אפשר לשנות את ההגדרות.",
                    "סייר כשר", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
                using (var f = new PasswordForm(cfg, true))
                {
                    if (f.ShowDialog(owner) != DialogResult.OK) return false;
                    ConfigStore.Save(cfg);
                    return true;
                }
            }
            using (var f = new PasswordForm(cfg, false)) return f.ShowDialog(owner) == DialogResult.OK;
        }
    }

    class SettingsForm : BaseDialog
    {
        readonly AppConfig cfg;
        readonly List<RootDef> roots;
        readonly ListView lvRoots = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable };
        readonly ListBox lbExcl = new ListBox { HorizontalScrollbar = true, SelectionMode = SelectionMode.MultiExtended };
        readonly CheckBox chkKiosk = new CheckBox { Text = "מצב קיוסק: מסך מלא, בלי שורת כותרת", AutoSize = true };
        readonly CheckBox chkExitPwd = new CheckBox { Text = "סגירת התוכנה דורשת סיסמה", AutoSize = true };
        readonly CheckBox chkDelete = new CheckBox { Text = "לאפשר מחיקה בהתקנים (לסל המחזור של ההתקן)", AutoSize = true };
        readonly CheckBox chkAuto = new CheckBox { Text = "להפעיל את התוכנה עם עליית Windows", AutoSize = true };
        readonly CheckBox chkAllDrives = new CheckBox { Text = "להציג גם כוננים קבועים נוספים כהתקנים (לא מומלץ)", AutoSize = true };
        readonly TextBox txtTitle = new TextBox();
        readonly Label lblExcl = new Label();
        public bool ExitRequested;

        public SettingsForm(AppConfig cfg)
        {
            this.cfg = cfg;
            roots = cfg.Roots.Select(r => new RootDef { Path = r.Path, Title = r.Title, Exclusions = r.Exclusions.ToList() }).ToList();
            Text = "הגדרות מנהל — סייר כשר " + Application.ProductVersion;
            ClientSize = new Size(760, 640);

            var g1 = new GroupBox { Text = "תיקיות אב שיוצגו לציבור (קריאה והעתקה בלבד)" };
            g1.SetBounds(12, 10, 736, 220);
            lvRoots.SetBounds(12, 26, 580, 180);
            lvRoots.Columns.Add("שם תצוגה", 180);
            lvRoots.Columns.Add("נתיב", 380);
            var bAdd = Btn(g1, "הוספה…", 604, 26);
            var bRen = Btn(g1, "שינוי שם…", 604, 62);
            var bUp = Btn(g1, "למעלה", 604, 98);
            var bDown = Btn(g1, "למטה", 604, 134);
            var bDel = Btn(g1, "הסרה", 604, 170);
            g1.Controls.Add(lvRoots);
            Controls.Add(g1);

            var g2 = new GroupBox { Text = "החרגות בתוך התיקיה שנבחרה (לא יוצגו ולא יועתקו)" };
            g2.SetBounds(12, 238, 736, 170);
            lbExcl.SetBounds(12, 26, 580, 132);
            var bExDir = Btn(g2, "החרגת תיקיה…", 604, 26);
            var bExFile = Btn(g2, "החרגת קבצים…", 604, 62);
            var bExDel = Btn(g2, "הסרת החרגה", 604, 98);
            lblExcl.SetBounds(604, 134, 120, 24); lblExcl.ForeColor = Color.DimGray;
            g2.Controls.Add(lbExcl); g2.Controls.Add(lblExcl);
            Controls.Add(g2);

            var g3 = new GroupBox { Text = "אפשרויות" };
            g3.SetBounds(12, 416, 736, 170);
            var lt = new Label { Text = "כותרת החלון:", AutoSize = true, Location = new Point(12, 28) };
            txtTitle.SetBounds(110, 25, 300, 26);
            chkKiosk.Location = new Point(12, 60);
            chkExitPwd.Location = new Point(12, 86);
            chkDelete.Location = new Point(12, 112);
            chkAuto.Location = new Point(380, 60);
            chkAllDrives.Location = new Point(380, 86);
            var bPwd = new Button { Text = "שינוי סיסמה…" }; bPwd.SetBounds(380, 110, 140, 30);
            g3.Controls.AddRange(new Control[] { lt, txtTitle, chkKiosk, chkExitPwd, chkDelete, chkAuto, chkAllDrives, bPwd });
            Controls.Add(g3);

            var bExit = AddButton("סגירת התוכנה", 12, 598, 140);
            var bSave = AddButton("שמירה", 542, 598, 100);
            var bCancel = AddButton("ביטול", 648, 598, 100, DialogResult.Cancel);
            CancelButton = bCancel;

            txtTitle.Text = cfg.WindowTitle;
            chkKiosk.Checked = cfg.Kiosk;
            chkExitPwd.Checked = cfg.PasswordToExit;
            chkDelete.Checked = cfg.AllowDeviceDelete;
            chkAllDrives.Checked = cfg.AllDriveTypes;
            try { chkAuto.Checked = ConfigStore.AutoStart; } catch { }

            FillRoots();
            lvRoots.SelectedIndexChanged += (s, e) => FillExcl();

            bAdd.Click += (s, e) => AddRoot();
            bRen.Click += (s, e) =>
            {
                var r = Sel(); if (r == null) return;
                string t = InputBox.Show(this, "שם תצוגה", "השם שיוצג לציבור עבור התיקיה:", r.Title);
                if (!string.IsNullOrWhiteSpace(t)) { r.Title = t.Trim(); FillRoots(roots.IndexOf(r)); }
            };
            bUp.Click += (s, e) => MoveRoot(-1);
            bDown.Click += (s, e) => MoveRoot(1);
            bDel.Click += (s, e) => { var r = Sel(); if (r != null) { roots.Remove(r); FillRoots(); } };
            bExDir.Click += (s, e) => AddExclDir();
            bExFile.Click += (s, e) => AddExclFiles();
            bExDel.Click += (s, e) =>
            {
                var r = Sel(); if (r == null) return;
                foreach (var x in lbExcl.SelectedItems.Cast<string>().ToList()) r.Exclusions.Remove(x);
                FillExcl();
            };
            bPwd.Click += (s, e) =>
            {
                var tmp = new AppConfig();
                using (var f = new PasswordForm(tmp, true, "שינוי סיסמת מנהל"))
                    if (f.ShowDialog(this) == DialogResult.OK)
                    {
                        cfg.PwdHash = tmp.PwdHash; cfg.PwdSalt = tmp.PwdSalt; cfg.PwdIter = tmp.PwdIter;
                        ConfigStore.Save(cfg);
                        MessageBox.Show(this, "הסיסמה שונתה.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
                    }
            };
            bSave.Click += (s, e) => Save();
            bExit.Click += (s, e) => { ExitRequested = true; DialogResult = DialogResult.Abort; };
        }

        static Button Btn(Control parent, string text, int x, int y)
        {
            var b = new Button { Text = text };
            b.SetBounds(x, y, 120, 30);
            parent.Controls.Add(b);
            return b;
        }

        RootDef Sel() => lvRoots.SelectedItems.Count > 0 ? (RootDef)lvRoots.SelectedItems[0].Tag : null;

        void FillRoots(int select = 0)
        {
            lvRoots.Items.Clear();
            foreach (var r in roots)
            {
                var it = new ListViewItem(r.Title) { Tag = r };
                it.SubItems.Add(r.Path);
                if (!Directory.Exists(r.Path)) it.ForeColor = Color.Firebrick;
                lvRoots.Items.Add(it);
            }
            if (lvRoots.Items.Count > 0) lvRoots.Items[Math.Max(0, Math.Min(select, lvRoots.Items.Count - 1))].Selected = true;
            FillExcl();
        }

        void FillExcl()
        {
            lbExcl.Items.Clear();
            var r = Sel();
            if (r != null) foreach (var x in r.Exclusions) lbExcl.Items.Add(x);
            lblExcl.Text = r == null ? "" : r.Exclusions.Count + " החרגות";
        }

        void AddRoot()
        {
            using (var d = new FolderBrowserDialog { Description = "בחרו תיקיה שתוצג לציבור", ShowNewFolderButton = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string p = PathUtil.Norm(d.SelectedPath);
                string sys = PathUtil.Norm(Path.GetPathRoot(p));
                if (string.Equals(p, sys, StringComparison.OrdinalIgnoreCase) &&
                    MessageBox.Show(this, "בחרתם כונן שלם. כל התוכן שלו יוצג לציבור. להמשיך?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) != DialogResult.Yes) return;
                if (roots.Any(r => PathUtil.IsUnder(p, r.Path) || PathUtil.IsUnder(r.Path, p)))
                {
                    Msg("התיקיה חופפת לתיקיה שכבר ברשימה (אחת מכילה את השנייה).");
                    return;
                }
                roots.Add(new RootDef { Path = p, Title = Path.GetFileName(p.TrimEnd('\\')) is string n && n.Length > 0 ? n : p });
                FillRoots(roots.Count - 1);
            }
        }

        void MoveRoot(int dir)
        {
            var r = Sel(); if (r == null) return;
            int i = roots.IndexOf(r), j = i + dir;
            if (j < 0 || j >= roots.Count) return;
            roots.RemoveAt(i); roots.Insert(j, r);
            FillRoots(j);
        }

        void AddExclDir()
        {
            var r = Sel(); if (r == null) { Msg("בחרו קודם תיקיית אב ברשימה העליונה."); return; }
            using (var d = new FolderBrowserDialog { Description = "בחרו תת-תיקיה שלא תוצג (בתוך " + r.Title + ")", SelectedPath = r.Path, ShowNewFolderButton = false })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                AddExcl(r, d.SelectedPath);
            }
        }

        void AddExclFiles()
        {
            var r = Sel(); if (r == null) { Msg("בחרו קודם תיקיית אב ברשימה העליונה."); return; }
            using (var d = new OpenFileDialog { Title = "בחרו קבצים שלא יוצגו", InitialDirectory = r.Path, Multiselect = true, Filter = "כל הקבצים|*.*" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                foreach (var f in d.FileNames) AddExcl(r, f);
            }
        }

        void AddExcl(RootDef r, string p)
        {
            p = PathUtil.Norm(p);
            if (!PathUtil.IsUnder(p, r.Path) || PathUtil.Same(p, r.Path)) { Msg("אפשר להחריג רק פריטים שנמצאים בתוך \"" + r.Title + "\"."); return; }
            if (!r.Exclusions.Any(x => PathUtil.Same(x, p))) r.Exclusions.Add(p);
            FillExcl();
        }

        void Save()
        {
            cfg.Roots = roots;
            cfg.WindowTitle = string.IsNullOrWhiteSpace(txtTitle.Text) ? "סייר כשר" : txtTitle.Text.Trim();
            cfg.Kiosk = chkKiosk.Checked;
            cfg.PasswordToExit = chkExitPwd.Checked;
            cfg.AllowDeviceDelete = chkDelete.Checked;
            cfg.AllDriveTypes = chkAllDrives.Checked;
            try { ConfigStore.Save(cfg); }
            catch (Exception ex) { Msg("שמירת ההגדרות נכשלה:\n" + ex.Message); return; }
            try { if (ConfigStore.AutoStart != chkAuto.Checked) ConfigStore.AutoStart = chkAuto.Checked; } catch { }
            DialogResult = DialogResult.OK;
        }

        void Msg(string m) => MessageBox.Show(this, m, Text, MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
    }

    static class InputBox
    {
        public static string Show(IWin32Window owner, string title, string prompt, string value)
        {
            using (var f = new BaseDialog { Text = title, ClientSize = new Size(380, 130) })
            {
                var l = new Label { Text = prompt }; l.SetBounds(16, 14, 348, 22);
                var t = new TextBox { Text = value }; t.SetBounds(16, 40, 348, 26);
                var ok = new Button { Text = "אישור", DialogResult = DialogResult.OK }; ok.SetBounds(158, 84, 100, 30);
                var c = new Button { Text = "ביטול", DialogResult = DialogResult.Cancel }; c.SetBounds(264, 84, 100, 30);
                f.Controls.AddRange(new Control[] { l, t, ok, c });
                f.AcceptButton = ok; f.CancelButton = c;
                return f.ShowDialog(owner) == DialogResult.OK ? t.Text : null;
            }
        }
    }

    /// <summary>Destination picker that can only see one device.</summary>
    class DevicePicker : BaseDialog
    {
        readonly TreeView tree = new TreeView { HideSelection = false, RightToLeftLayout = true, ShowLines = false };
        readonly Guard guard;
        public string Selected;

        public DevicePicker(Guard guard, List<Device> devices, Device preferred, string lastFolder, int fileCount)
        {
            this.guard = guard;
            Text = "העתקה להתקן";
            FormBorderStyle = FormBorderStyle.Sizable; MinimumSize = new Size(360, 380);
            ClientSize = new Size(420, 480);
            var l = new Label { Text = "לאן להעתיק " + (fileCount == 1 ? "את הפריט" : "את " + fileCount + " הפריטים") + "?", Dock = DockStyle.Top, Height = 34, Padding = new Padding(8, 10, 8, 0) };
            var il = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
            il.Images.Add("dir", Native.ExtIcon("dir", true) ?? new Bitmap(16, 16));
            tree.ImageList = il;
            tree.Dock = DockStyle.Fill;
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48 };
            var bNew = new Button { Text = "תיקיה חדשה" }; bNew.SetBounds(8, 9, 110, 30);
            var ok = new Button { Text = "העתקה לכאן", DialogResult = DialogResult.OK }; ok.SetBounds(196, 9, 110, 30);
            var c = new Button { Text = "ביטול", DialogResult = DialogResult.Cancel }; c.SetBounds(312, 9, 100, 30);
            ok.Anchor = c.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bottom.Controls.AddRange(new Control[] { bNew, ok, c });
            Controls.Add(tree); Controls.Add(bottom); Controls.Add(l);
            AcceptButton = ok; CancelButton = c;
            Native.ExplorerTheme(tree);

            foreach (var d in devices)
            {
                il.Images.Add(d.Root, Native.PathIcon(d.Root, true) ?? new Bitmap(16, 16));
                var n = new TreeNode(d.Display) { Tag = d.Root, ImageKey = d.Root, SelectedImageKey = d.Root };
                n.Nodes.Add(new TreeNode("..."));
                tree.Nodes.Add(n);
            }
            tree.BeforeExpand += (s, e) => LoadChildren(e.Node);
            Shown += (s, e) =>
            {
                var start = tree.Nodes.Cast<TreeNode>().FirstOrDefault(n => preferred != null && (string)n.Tag == preferred.Root) ?? (tree.Nodes.Count > 0 ? tree.Nodes[0] : null);
                if (start == null) return;
                start.Expand();
                tree.SelectedNode = start;
                if (lastFolder != null && Directory.Exists(lastFolder) && PathUtil.IsUnder(lastFolder, (string)start.Tag))
                    SelectPath(start, lastFolder);
            };
            ok.Click += (s, e) => { Selected = tree.SelectedNode?.Tag as string; if (Selected == null) DialogResult = DialogResult.None; };
            bNew.Click += (s, e) =>
            {
                var n = tree.SelectedNode; if (n == null) return;
                string name = InputBox.Show(this, "תיקיה חדשה", "שם התיקיה:", "תיקיה חדשה");
                if (name == null) return;
                string err = PathUtil.ValidateName(name.Trim());
                if (err != null) { MessageBox.Show(this, err); return; }
                try
                {
                    string p = PathUtil.UniqueName((string)n.Tag, name.Trim());
                    if (!guard.DeviceAllowed(p)) return;
                    Directory.CreateDirectory(p);
                    n.Nodes.Clear(); n.Nodes.Add(new TreeNode("...")); n.Collapse(); n.Expand();
                    SelectPath(n, p);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message); }
            };
        }

        void LoadChildren(TreeNode n)
        {
            if (n.Nodes.Count != 1 || n.Nodes[0].Tag != null) return;
            n.Nodes.Clear();
            string dir = (string)n.Tag;
            var dev = guard.DeviceOf(dir);
            try
            {
                foreach (var e in Lister.List(dir, fi => fi is DirectoryInfo && guard.ShowEntry(fi, null, dev?.Root))
                                        .OrderBy(x => x.Name, Comparer<string>.Create(Native.StrCmpLogicalW)))
                {
                    var c = new TreeNode(e.Name) { Tag = e.Path, ImageKey = "dir", SelectedImageKey = "dir" };
                    c.Nodes.Add(new TreeNode("..."));
                    n.Nodes.Add(c);
                }
            }
            catch { }
        }

        void SelectPath(TreeNode from, string path)
        {
            var n = from;
            while (n != null && !PathUtil.Same((string)n.Tag, path))
            {
                n.Expand();
                n = n.Nodes.Cast<TreeNode>().FirstOrDefault(c => c.Tag is string p && PathUtil.IsUnder(path, p));
            }
            if (n != null) { tree.SelectedNode = n; n.EnsureVisible(); }
        }
    }
}

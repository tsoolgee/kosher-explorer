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
        readonly CheckedListBox clbDrives = new CheckedListBox { CheckOnClick = true, IntegralHeight = false };
        List<InternalDrive> internalDrives;
        public bool ExitRequested;

        class DriveRow
        {
            public InternalDrive D; public bool Connected;
            public override string ToString() => D.Name + (Connected ? "" : "  (לא מחובר כעת)");
        }

        void FillDrives()
        {
            internalDrives = internalDrives ?? cfg.InternalDrives.Select(x => new InternalDrive { Serial = x.Serial, Name = x.Name }).ToList();
            // keep what's already ticked in the list before rebuilding it
            if (clbDrives.Items.Count > 0)
            {
                internalDrives = clbDrives.CheckedItems.Cast<DriveRow>().Select(r => r.D).ToList();
            }
            clbDrives.Items.Clear();
            var seen = new HashSet<string>();
            List<Device> found;
            try { found = DeviceScanner.Scan(cfg, true); } catch { found = new List<Device>(); }
            foreach (var d in found.Where(d => d.Serial != null))
            {
                seen.Add(d.Serial);
                var mine = internalDrives.FirstOrDefault(x => x.Serial == d.Serial);
                string name = d.Display + (string.IsNullOrEmpty(d.Model) ? "" : " — " + d.Model) + ", " + PathUtil.Size(d.Total);
                if (mine != null) mine.Name = name;
                clbDrives.Items.Add(new DriveRow { D = mine ?? new InternalDrive { Serial = d.Serial, Name = name }, Connected = true }, mine != null);
            }
            foreach (var x in internalDrives.Where(x => !seen.Contains(x.Serial)))
                clbDrives.Items.Add(new DriveRow { D = x, Connected = false }, true);
        }

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
            var bExDir = Btn(g2, "החרגת תיקיות…", 604, 26);
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

            var g4 = new GroupBox { Text = "כוננים פנימיים: כוננים מסומנים לא יוצגו לציבור כהתקן (למשל דיסק USB שמחובר קבוע)" };
            g4.SetBounds(12, 594, 736, 150);
            clbDrives.SetBounds(12, 26, 580, 112);
            var bDrvRefresh = Btn(g4, "רענון רשימה", 604, 26);
            var lblDrv = new Label { Text = "הכונן מזוהה לפי המספר הסידורי שלו, כך ששינוי אות לא משנה.", ForeColor = Color.DimGray };
            lblDrv.SetBounds(604, 62, 124, 76);
            g4.Controls.Add(clbDrives); g4.Controls.Add(lblDrv);
            Controls.Add(g4);
            FillDrives();
            bDrvRefresh.Click += (s, e) => FillDrives();

            var bExit = AddButton("סגירת התוכנה", 12, 756, 140);
            var bSave = AddButton("שמירה", 542, 756, 100);
            var bCancel = AddButton("ביטול", 648, 756, 100, DialogResult.Cancel);
            ClientSize = new Size(760, 798);

            // The settings no longer fit on small/scaled screens, which hid the Save button.
            // Groups go into a scrolling area; the buttons stay pinned at the bottom.
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            ClientSize = new Size(760 + SystemInformation.VerticalScrollBarWidth, 798);
            var bar = new Panel { Width = 760, Dock = DockStyle.Bottom, Height = 50, BackColor = Color.FromArgb(245, 245, 245) };   // real width before docking, or right-anchored buttons drift off
            foreach (var g in new Control[] { g1, g2, g3, g4 }) { Controls.Remove(g); content.Controls.Add(g); }
            content.Controls.Add(new Label { Location = new Point(0, 744), Size = new Size(1, 8) });   // bottom margin
            foreach (var b in new[] { bExit, bSave, bCancel })
            {
                Controls.Remove(b);
                b.Top = 10;
                b.Anchor = b == bExit ? AnchorStyles.Top | AnchorStyles.Left : AnchorStyles.Top | AnchorStyles.Right;
                bar.Controls.Add(b);
            }
            Controls.Add(content);
            Controls.Add(bar);
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(560, 360);
            Load += (s, e) =>
            {
                var wa = Screen.FromControl(Owner ?? this).WorkingArea;
                if (Height > wa.Height) { Height = wa.Height; Top = wa.Top; }
                if (Width > wa.Width) { Width = wa.Width; Left = wa.Left; }
            };
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
            if (!Directory.Exists(r.Path)) { Msg("התיקיה לא נמצאה."); return; }
            var dirExcl = r.Exclusions.Where(Directory.Exists).ToList();
            using (var d = new FolderCheckPicker(r, dirExcl))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                // folders: replace with what is ticked now; file exclusions stay as they are
                r.Exclusions.RemoveAll(x => dirExcl.Any(y => PathUtil.Same(x, y)));
                foreach (var p in d.Checked)
                    if (!r.Exclusions.Any(x => PathUtil.Same(x, p))) r.Exclusions.Add(p);
                FillExcl();
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
            cfg.InternalDrives = clbDrives.CheckedItems.Cast<DriveRow>().Select(r => r.D).ToList();
            try { ConfigStore.Save(cfg); }
            catch (Exception ex) { Msg("שמירת ההגדרות נכשלה:\n" + ex.Message); return; }
            try { if (ConfigStore.AutoStart != chkAuto.Checked) ConfigStore.AutoStart = chkAuto.Checked; } catch { }
            DialogResult = DialogResult.OK;
        }

        void Msg(string m) => MessageBox.Show(this, m, Text, MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
    }

    /// <summary>Tree of a root's subfolders with checkboxes: tick any number of folders to exclude.</summary>
    class FolderCheckPicker : BaseDialog
    {
        readonly TreeView tree = new TreeView { CheckBoxes = true, HideSelection = false, RightToLeftLayout = true, Dock = DockStyle.Fill };
        public List<string> Checked = new List<string>();

        public FolderCheckPicker(RootDef r, List<string> existing)
        {
            Text = "החרגת תיקיות — " + r.Title;
            FormBorderStyle = FormBorderStyle.Sizable; MinimumSize = new Size(380, 400);
            ClientSize = new Size(480, 560);
            var l = new Label { Text = "סמנו את כל התיקיות שלא יוצגו לציבור (אפשר כמה), לחצו \"אישור\", ובחלון ההגדרות לחצו \"שמירה\".", Dock = DockStyle.Top, Height = 52, Padding = new Padding(8, 10, 8, 0) };
            var il = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
            il.Images.Add("dir", Native.ExtIcon("dir", true) ?? new Bitmap(16, 16));
            tree.ImageList = il;
            var bottom = new Panel { Width = ClientSize.Width, Dock = DockStyle.Bottom, Height = 48 };   // real width before docking, or right-anchored buttons drift off
            var lblCount = new Label { AutoSize = true, Location = new Point(8, 16), ForeColor = Color.DimGray };
            var ok = new Button { Text = "אישור", DialogResult = DialogResult.OK }; ok.SetBounds(262, 9, 100, 30);
            var c = new Button { Text = "ביטול", DialogResult = DialogResult.Cancel }; c.SetBounds(368, 9, 100, 30);
            ok.Anchor = c.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bottom.Controls.AddRange(new Control[] { lblCount, ok, c });
            Controls.Add(tree); Controls.Add(bottom); Controls.Add(l);
            AcceptButton = ok; CancelButton = c;

            var root = new TreeNode(r.Title) { Tag = PathUtil.Norm(r.Path), ImageKey = "dir", SelectedImageKey = "dir" };
            root.Nodes.Add(new TreeNode("..."));
            tree.Nodes.Add(root);
            tree.BeforeExpand += (s, e) => LoadChildren(e.Node);
            tree.BeforeCheck += (s, e) => { if (e.Node == root) e.Cancel = true; };   // the root itself can't be excluded
            tree.AfterCheck += (s, e) => lblCount.Text = CheckedNodes(tree.Nodes).Count() + " תיקיות מסומנות";
            root.Expand();
            // open the tree down to every existing exclusion and tick it
            foreach (var p in existing)
            {
                var n = root;
                while (n != null && !PathUtil.Same((string)n.Tag, p))
                {
                    LoadChildren(n); n.Expand();   // load now: BeforeExpand does not fire before the window exists
                    n = n.Nodes.Cast<TreeNode>().FirstOrDefault(x => x.Tag is string xp && PathUtil.IsUnder(p, xp));
                }
                if (n != null && n != root) n.Checked = true;
            }
            lblCount.Text = CheckedNodes(tree.Nodes).Count() + " תיקיות מסומנות";
            ok.Click += (s, e) => Checked = CheckedNodes(tree.Nodes).Select(n => (string)n.Tag).ToList();
            Shown += (s, e) => Native.ExplorerTheme(tree);
        }

        static IEnumerable<TreeNode> CheckedNodes(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Checked && n.Tag is string) yield return n;
                foreach (var c in CheckedNodes(n.Nodes)) yield return c;
            }
        }

        static void LoadChildren(TreeNode n)
        {
            if (n.Nodes.Count != 1 || n.Nodes[0].Tag != null) return;
            n.Nodes.Clear();
            try
            {
                foreach (var d in new DirectoryInfo((string)n.Tag).EnumerateDirectories()
                                     .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0)
                                     .OrderBy(d => d.Name, Comparer<string>.Create(Native.StrCmpLogicalW)))
                {
                    var c = new TreeNode(d.Name) { Tag = d.FullName, ImageKey = "dir", SelectedImageKey = "dir" };
                    if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) c.ForeColor = Color.Gray;
                    try { if (d.EnumerateDirectories().Any()) c.Nodes.Add(new TreeNode("...")); } catch { }
                    n.Nodes.Add(c);
                }
            }
            catch { }
        }
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
            var bottom = new Panel { Width = ClientSize.Width, Dock = DockStyle.Bottom, Height = 48 };   // real width before docking, or right-anchored buttons drift off
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
                    n.Nodes.Clear(); n.Nodes.Add(new TreeNode("...")); LoadChildren(n); n.Expand();
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
                LoadChildren(n); n.Expand();
                n = n.Nodes.Cast<TreeNode>().FirstOrDefault(c => c.Tag is string p && PathUtil.IsUnder(path, p));
            }
            if (n != null) { tree.SelectedNode = n; n.EnsureVisible(); }
        }
    }
}

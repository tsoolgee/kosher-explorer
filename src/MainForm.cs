using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KosherExplorer
{
    enum LocKind { Home, Public, Device, Trash, Search }

    class Loc
    {
        public LocKind Kind;
        public string Path;        // folder (Public/Device), device root (Trash)
        public string SearchText;
        public Loc Base;           // for Search
        public LocKind Effective => Kind == LocKind.Search ? (Base.Kind == LocKind.Home ? LocKind.Public : Base.Kind) : Kind;
        public static Loc Home() => new Loc { Kind = LocKind.Home };
        public bool SameAs(Loc o) => o != null && o.Kind == Kind && o.SearchText == SearchText &&
                                     (Path == null ? o.Path == null : o.Path != null && PathUtil.Same(Path, o.Path));
    }

    class ExplorerList : ListView
    {
        public event Action<ListViewItem> ItemDoubleClicked;
        public ExplorerList() { DoubleBuffered = true; }

        protected override void WndProc(ref Message m)
        {
            // With checkboxes on, the native double-click toggles the item's check, which (synced to selection)
            // deselects it before ItemActivate runs. Handle double-clicks on items ourselves.
            if (m.Msg == 0x203 /*WM_LBUTTONDBLCLK*/)
            {
                int lp = m.LParam.ToInt32();
                var hit = HitTest(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                if (hit.Item != null && hit.Location != ListViewHitTestLocations.StateImage)
                {
                    hit.Item.Selected = true;
                    hit.Item.Focused = true;
                    ItemDoubleClicked?.Invoke(hit.Item);
                    return;
                }
            }
            base.WndProc(ref m);
        }
    }

    class MainForm : Form
    {
        const string DragFormat = "KosherExplorer.Paths";
        readonly AppConfig cfg;
        readonly Guard guard;
        List<Device> devices = new List<Device>();
        readonly float S;

        // navigation
        Loc current = Loc.Home();
        readonly Stack<Loc> back = new Stack<Loc>(), fwd = new Stack<Loc>();
        List<Entry> entries = new List<Entry>();

        // ui
        readonly ToolStrip nav = new ToolStrip(), crumbs = new ToolStrip(), cmd = new ToolStrip();
        readonly TextBox search = new TextBox { BorderStyle = BorderStyle.FixedSingle };
        readonly SplitContainer split = new SplitContainer(), split2 = new SplitContainer();
        readonly TreeView tree = new TreeView();
        readonly ExplorerList list = new ExplorerList();
        readonly PreviewPane pane = new PreviewPane();
        readonly StatusStrip status = new StatusStrip();
        readonly ToolStripStatusLabel stCount = new ToolStripStatusLabel(), stSel = new ToolStripStatusLabel(),
            stMsg = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft }, stFree = new ToolStripStatusLabel();
        readonly ImageList smallIL = new ImageList { ColorDepth = ColorDepth.Depth32Bit };
        ImageList largeIL;
        int largeSize;
        readonly ContextMenuStrip listMenu = new ContextMenuStrip(), treeMenu = new ContextMenuStrip();

        ToolStripButton bBack, bFwd, bUp, bRefresh, bSend, bCut, bCopy, bPaste, bRename, bDelete,
            bRestore, bEmpty, bEject, bChecks, bPreview, bDetails, bSelAll, bSelNone, bGear;
        ToolStripDropDownButton bView, bSort, bNew;
        ToolStripLabel lblReadOnly;

        TreeNode nHome, nPublic, nWork, nDevices;

        // the folder on screen is watched so files saved from Word etc. show up by themselves
        FileSystemWatcher watcher;
        readonly System.Windows.Forms.Timer watchTimer = new System.Windows.Forms.Timer { Interval = 700 };
        bool busy;
        bool suppressTree, syncingCheck, editingLabel, allowClose;

        // clipboard (internal only — nothing from outside the program can be pasted)
        List<string> clip = new List<string>();
        bool clipCut;

        // sort
        string sortKey = "name";
        bool sortAsc = true;
        string[] colKeys = new string[0];

        // devices
        readonly System.Windows.Forms.Timer deviceTimer = new System.Windows.Forms.Timer { Interval = 900 };
        readonly Dictionary<string, string> lastDeviceFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string lastDevice;

        // thumbnails
        readonly BlockingCollection<ThumbReq> thumbQueue = new BlockingCollection<ThumbReq>();
        volatile int thumbGen;
        readonly Dictionary<string, List<ListViewItem>> pendingIcons = new Dictionary<string, List<ListViewItem>>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> requestedExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        class ThumbReq { public int Gen, Size, Flags; public string Key, Path; }

        CancellationTokenSource searchCts;

        static readonly string[] ViewNames = { "XL", "L", "M", "S", "List", "Details", "Tiles" };
        static readonly string[] ViewTitles = { "סמלים גדולים מאוד", "סמלים גדולים", "סמלים בינוניים", "סמלים קטנים", "רשימה", "פרטים", "אריחים" };

        public MainForm(AppConfig cfg)
        {
            this.cfg = cfg;
            guard = new Guard(cfg) { Devices = () => devices };
            S = DeviceDpi / 96f;
            Glyphs.Scale = S;
            Text = cfg.WindowTitle;
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            Font = new Font("Segoe UI", 9.5f);
            BackColor = Color.White;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            MinimumSize = new Size((int)(760 * S), (int)(480 * S));
            Size = new Size((int)(1200 * S), (int)(760 * S));
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = false;

            BuildUi();
            ApplyWindowMode();

            var th = new Thread(ThumbLoop) { IsBackground = true, Name = "thumbs" };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();

            deviceTimer.Tick += (s, e) => { deviceTimer.Stop(); RescanDevices(); };
            Load += (s, e) =>
            {
                devices = DeviceScanner.Scan(cfg);
                BuildTree();
                Navigate(Loc.Home(), false);
                Task.Run(CleanOpenTemp);
                if (cfg.Roots.Count == 0) Status("ברוכים הבאים! כדי להתחיל, לחצו על ⚙ ובחרו את התיקיות שיוצגו לציבור.");
            };
            FormClosing += OnClosing;
        }

        // ============================================================ UI
        ToolStripButton Btn(ToolStrip ts, string text, char glyph, Action a, string tip = null, bool showText = true, Color? color = null)
        {
            var b = new ToolStripButton(text)
            {
                Image = Glyphs.Get(glyph, color ?? Color.FromArgb(40, 40, 40)),
                DisplayStyle = showText ? ToolStripItemDisplayStyle.ImageAndText : ToolStripItemDisplayStyle.Image,
                ToolTipText = tip ?? text,
                ImageScaling = ToolStripItemImageScaling.None,
                Padding = new Padding(3, 2, 3, 2)
            };
            if (b.Image == null) b.DisplayStyle = ToolStripItemDisplayStyle.Text;
            b.Click += (s, e) => a();
            ts.Items.Add(b);
            return b;
        }

        void BuildUi()
        {
            var renderer = new ToolStripProfessionalRenderer(new LightColors()) { RoundedEdges = false };

            // --- top row: nav buttons + breadcrumbs + search
            foreach (var ts in new[] { nav, crumbs, cmd })
            {
                ts.GripStyle = ToolStripGripStyle.Hidden;
                ts.Renderer = renderer;
                ts.BackColor = Color.White;
                ts.RightToLeft = RightToLeft.Yes;
                ts.Padding = new Padding((int)(6 * S), (int)(3 * S), (int)(6 * S), (int)(3 * S));
            }
            bBack = Btn(nav, "הקודם", Glyphs.Back, GoBack, "הקודם (Alt+←)", false);
            bFwd = Btn(nav, "הבא", Glyphs.Forward, GoForward, "הבא (Alt+→)", false);
            bUp = Btn(nav, "למעלה", Glyphs.Up, GoUp, "תיקיית האב (Alt+↑)", false);
            bRefresh = Btn(nav, "רענון", Glyphs.Refresh, RefreshView, "רענון (F5)", false);
            nav.Dock = DockStyle.Fill;
            nav.AutoSize = false;

            crumbs.Dock = DockStyle.Fill;
            crumbs.AutoSize = false;
            crumbs.CanOverflow = true;
            crumbs.BackColor = Color.FromArgb(248, 248, 248);
            crumbs.Renderer = new ToolStripProfessionalRenderer(new LightColors(Color.FromArgb(248, 248, 248))) { RoundedEdges = false };

            search.Dock = DockStyle.Fill;
            search.Margin = new Padding((int)(4 * S), (int)(7 * S), (int)(8 * S), (int)(4 * S));
            search.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; StartSearch(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; CancelSearch(); }
            };
            search.HandleCreated += (s, e) => Native.SetCue(search, "חיפוש בתיקיה הנוכחית");

            var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = (int)(40 * S), ColumnCount = 3, RowCount = 1, BackColor = Color.White };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130 * S));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240 * S));
            nav.Margin = crumbs.Margin = Padding.Empty;
            crumbs.Margin = new Padding(0, (int)(4 * S), 0, (int)(4 * S));
            top.Controls.Add(nav, 0, 0);
            top.Controls.Add(crumbs, 1, 0);
            top.Controls.Add(search, 2, 0);

            // --- command bar
            bSend = Btn(cmd, "העתקה להתקן", Glyphs.Send, SendToDevice, "העתקת הפריטים שנבחרו לנגן / דיסק און קי", true, Color.FromArgb(0, 120, 90));
            bSend.Font = new Font(Font, FontStyle.Bold);
            bSend.ForeColor = Color.FromArgb(0, 100, 75);
            cmd.Items.Add(new ToolStripSeparator());
            bNew = new ToolStripDropDownButton("חדש") { Image = Glyphs.Get(Glyphs.Add, Color.FromArgb(40, 40, 40)), ImageScaling = ToolStripItemImageScaling.None, ToolTipText = "יצירת תיקיה או מסמך חדש כאן" };
            cmd.Items.Add(bNew);
            bNew.DropDownOpening += (s, e) => FillNewMenu(bNew.DropDownItems);
            FillNewMenu(bNew.DropDownItems);
            bCut = Btn(cmd, "גזירה", Glyphs.Cut, () => ToClip(true), "גזירה (Ctrl+X)", false);
            bCopy = Btn(cmd, "העתקה", Glyphs.Copy, () => ToClip(false), "העתקה (Ctrl+C)", false);
            bPaste = Btn(cmd, "הדבקה", Glyphs.Paste, Paste, "הדבקה (Ctrl+V)", false);
            bRename = Btn(cmd, "שינוי שם", Glyphs.Rename, Rename, "שינוי שם (F2)", false);
            bDelete = Btn(cmd, "מחיקה", Glyphs.Delete, () => Delete(false), "מחיקה לסל המחזור של ההתקן (Delete)", false);
            bRestore = Btn(cmd, "שחזור", Glyphs.Restore, RestoreSelected, "שחזור הפריטים שנבחרו למקומם");
            bEmpty = Btn(cmd, "ריקון הסל", Glyphs.Delete, EmptyTrash, "מחיקה סופית של כל מה שבסל");
            bEject = Btn(cmd, "הוצאה בטוחה", Glyphs.Usb, () => { var d = CurrentDevice(); if (d != null) Eject(d.Root); }, "הוצאה בטוחה של ההתקן לפני ניתוק");
            cmd.Items.Add(new ToolStripSeparator());

            bSort = new ToolStripDropDownButton("מיון") { Image = Glyphs.Get(Glyphs.Sort, Color.FromArgb(40, 40, 40)), ImageScaling = ToolStripItemImageScaling.None };
            bView = new ToolStripDropDownButton("תצוגה") { Image = Glyphs.Get(Glyphs.View, Color.FromArgb(40, 40, 40)), ImageScaling = ToolStripItemImageScaling.None };
            cmd.Items.Add(bSort); cmd.Items.Add(bView);
            bSort.DropDownOpening += (s, e) => FillSortMenu(bSort.DropDownItems);
            bView.DropDownOpening += (s, e) => FillViewMenu(bView.DropDownItems);
            FillSortMenu(bSort.DropDownItems); FillViewMenu(bView.DropDownItems);
            cmd.Items.Add(new ToolStripSeparator());
            bSelAll = Btn(cmd, "בחירת הכל", Glyphs.SelectAll, SelectAll, "בחירת הכל (Ctrl+A)", false);
            bSelNone = Btn(cmd, "ביטול בחירה", Glyphs.SelectNone, SelectNone, "ביטול הבחירה", false);
            bChecks = Btn(cmd, "תיבות סימון", Glyphs.Check, () => { cfg.CheckBoxes = !cfg.CheckBoxes; ApplyViewMode(); SavePrefs(); }, "הצגת תיבות סימון ליד הפריטים", false);
            bPreview = Btn(cmd, "תצוגה מקדימה", Glyphs.Preview, () => SetPane(cfg.Pane == "Preview" ? "None" : "Preview"), "חלונית תצוגה מקדימה", false);
            bDetails = Btn(cmd, "פרטים", Glyphs.Details, () => SetPane(cfg.Pane == "Details" ? "None" : "Details"), "חלונית פרטים (Alt+Enter)", false);

            bGear = Btn(cmd, "הגדרות", Glyphs.Settings, OpenSettings, "הגדרות מנהל (דורש סיסמה)", false);
            bGear.Alignment = ToolStripItemAlignment.Right;
            lblReadOnly = new ToolStripLabel("  תיקיה לציבור: העתקה בלבד  ")
            {
                Image = Glyphs.Get(Glyphs.Lock, Color.FromArgb(150, 110, 0), 14),
                ForeColor = Color.FromArgb(150, 110, 0),
                Alignment = ToolStripItemAlignment.Right,
                ImageScaling = ToolStripItemImageScaling.None
            };
            cmd.Items.Add(lblReadOnly);
            cmd.Dock = DockStyle.Top;
            cmd.AutoSize = true;
            var cmdLine = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(230, 230, 230) };

            // --- tree
            int small = (int)Math.Round(16 * S);
            smallIL.ImageSize = new Size(small, small);
            AddSmall("dir", Native.ExtIcon("dir", true));
            AddSmall("home", Glyphs.Get(Glyphs.Home, Color.FromArgb(0, 95, 184), 16));
            AddSmall("public", Glyphs.Get(Glyphs.Shield, Color.FromArgb(150, 110, 0), 16));
            AddSmall("devices", Glyphs.Get(Glyphs.Usb, Color.FromArgb(0, 120, 90), 16));
            AddSmall("work", Glyphs.Get(Glyphs.Rename, Color.FromArgb(0, 95, 184), 16));
            AddSmall("trash", Glyphs.Get(Glyphs.Delete, Color.FromArgb(90, 90, 90), 16));
            AddSmall("search", Glyphs.Get(Glyphs.Search, Color.FromArgb(40, 40, 40), 16));
            tree.ImageList = smallIL;
            tree.Dock = DockStyle.Fill;
            tree.BorderStyle = BorderStyle.None;
            tree.HideSelection = false;
            tree.ShowLines = false;
            tree.HotTracking = true;
            tree.FullRowSelect = true;
            tree.RightToLeftLayout = true;
            tree.ItemHeight = (int)(26 * S);
            tree.Indent = (int)(14 * S);
            tree.AllowDrop = true;
            tree.HandleCreated += (s, e) => Native.ExplorerTheme(tree);
            tree.BeforeExpand += (s, e) => LoadTreeChildren(e.Node);
            tree.AfterSelect += (s, e) => { if (!suppressTree && e.Node?.Tag is Loc l) Navigate(l); };
            tree.NodeMouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Right) tree.SelectedNode = e.Node;
                else if (e.Node == tree.SelectedNode && e.Node.Tag is Loc l && !l.SameAs(current)) Navigate(l);
            };
            tree.DragOver += TreeDragOver;
            tree.DragDrop += TreeDragDrop;
            tree.ContextMenuStrip = treeMenu;
            treeMenu.RightToLeft = RightToLeft.Yes;
            treeMenu.Opening += TreeMenuOpening;

            // --- list
            list.Dock = DockStyle.Fill;
            list.BorderStyle = BorderStyle.None;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.AllowColumnReorder = false;
            list.LabelEdit = true;
            list.AllowDrop = true;
            list.RightToLeftLayout = true;
            list.SmallImageList = smallIL;
            list.HandleCreated += (s, e) => Native.ExplorerTheme(list);
            list.ItemActivate += (s, e) => OpenSelected();
            list.ItemDoubleClicked += it => OpenItem(it);
            list.ColumnClick += (s, e) => { if (e.Column < colKeys.Length) SortBy(colKeys[e.Column], null); };
            list.ItemSelectionChanged += (s, e) =>
            {
                if (!syncingCheck && list.CheckBoxes) { syncingCheck = true; e.Item.Checked = e.IsSelected; syncingCheck = false; }
                selectionTimer.Stop(); selectionTimer.Start();
            };
            list.ItemChecked += (s, e) =>
            {
                if (syncingCheck) return;
                syncingCheck = true; e.Item.Selected = e.Item.Checked; syncingCheck = false;
            };
            list.BeforeLabelEdit += (s, e) =>
            {
                var en = list.Items[e.Item].Tag as Entry;
                if (en == null || en.Trash != null || current.Effective != LocKind.Device || !guard.DeviceAllowed(en.Path)) { e.CancelEdit = true; return; }
                editingLabel = true;
            };
            list.AfterLabelEdit += AfterLabelEdit;
            list.ItemDrag += ListItemDrag;
            list.DragOver += ListDragOver;
            list.DragDrop += ListDragDrop;
            list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Back && !editingLabel) { e.SuppressKeyPress = true; GoBack(); }
            };
            list.MouseWheel += (s, e) =>
            {
                if ((ModifierKeys & Keys.Control) == 0 || current.Kind == LocKind.Home) return;
                string[] order = { "Details", "List", "S", "M", "L", "XL" };
                int i = Array.IndexOf(order, cfg.ViewMode); if (i < 0) i = 0;
                i = Math.Max(0, Math.Min(order.Length - 1, i + (e.Delta > 0 ? 1 : -1)));
                if (order[i] != cfg.ViewMode) { cfg.ViewMode = order[i]; ApplyViewMode(); SavePrefs(); }
            };
            list.ContextMenuStrip = listMenu;
            listMenu.RightToLeft = RightToLeft.Yes;
            listMenu.Opening += ListMenuOpening;
            selectionTimer.Tick += (s, e) => { selectionTimer.Stop(); OnSelectionChanged(); };
            statusClear.Tick += (s, e) => { statusClear.Stop(); stMsg.Text = ""; };
            watchTimer.Tick += (s, e) => { watchTimer.Stop(); OnFolderChanged(); };

            pane.Dock = DockStyle.Fill;
            pane.TypeOf = e => e == "dir" ? Native.TypeName("", true) : Native.TypeName(e, false);

            // --- splits: [tree | [list | pane]]
            split.Dock = DockStyle.Fill;
            split.FixedPanel = FixedPanel.Panel1;
            split.SplitterWidth = (int)(5 * S);
            split.BackColor = Color.FromArgb(235, 235, 235);
            split.Panel1.BackColor = Color.White; split.Panel2.BackColor = Color.White;
            split.Panel1.Controls.Add(tree);
            split.Panel1.Padding = new Padding((int)(4 * S), (int)(4 * S), 0, 0);
            split2.Dock = DockStyle.Fill;
            split2.FixedPanel = FixedPanel.Panel2;
            split2.SplitterWidth = (int)(5 * S);
            split2.BackColor = Color.FromArgb(235, 235, 235);
            split2.Panel1.BackColor = Color.White; split2.Panel2.BackColor = Color.White;
            split2.Panel1.Controls.Add(list);
            split2.Panel2.Controls.Add(pane);
            split.Panel2.Controls.Add(split2);
            split.SplitterMoved += (s, e) => { cfg.NavWidth = (int)(split.SplitterDistance / S); };
            split2.SplitterMoved += (s, e) => { cfg.PaneWidth = (int)((split2.Width - split2.SplitterDistance) / S); };

            // --- status
            status.RightToLeft = RightToLeft.Yes;
            status.BackColor = Color.FromArgb(248, 248, 248);
            status.SizingGrip = false;
            status.Items.AddRange(new ToolStripItem[] { stCount, stSel, stMsg, stFree });

            Controls.Add(split);
            Controls.Add(cmdLine);
            Controls.Add(cmd);
            Controls.Add(top);
            Controls.Add(status);

            Shown += (s, e) =>
            {
                try { split.SplitterDistance = (int)(Math.Max(160, cfg.NavWidth) * S); } catch { }
                SetPane(cfg.Pane);
                ApplyViewMode();
                list.Focus();
            };
        }

        readonly System.Windows.Forms.Timer selectionTimer = new System.Windows.Forms.Timer { Interval = 120 };

        void AddSmall(string key, Image img)
        {
            if (img == null) img = new Bitmap(smallIL.ImageSize.Width, smallIL.ImageSize.Height);
            smallIL.Images.Add(key, img.Width == smallIL.ImageSize.Width ? img : Native.Fit(img, smallIL.ImageSize.Width, true));
        }

        class LightColors : ProfessionalColorTable
        {
            readonly Color bg;
            public LightColors() : this(Color.White) { }
            public LightColors(Color bg) { this.bg = bg; UseSystemColors = false; }
            public override Color ToolStripGradientBegin => bg;
            public override Color ToolStripGradientMiddle => bg;
            public override Color ToolStripGradientEnd => bg;
            public override Color ToolStripBorder => bg;
            public override Color ButtonSelectedHighlight => Color.FromArgb(229, 243, 255);
            public override Color ButtonSelectedGradientBegin => Color.FromArgb(229, 243, 255);
            public override Color ButtonSelectedGradientMiddle => Color.FromArgb(229, 243, 255);
            public override Color ButtonSelectedGradientEnd => Color.FromArgb(229, 243, 255);
            public override Color ButtonSelectedBorder => Color.FromArgb(204, 232, 255);
            public override Color ButtonPressedGradientBegin => Color.FromArgb(204, 232, 255);
            public override Color ButtonPressedGradientMiddle => Color.FromArgb(204, 232, 255);
            public override Color ButtonPressedGradientEnd => Color.FromArgb(204, 232, 255);
            public override Color ButtonCheckedGradientBegin => Color.FromArgb(204, 232, 255);
            public override Color ButtonCheckedGradientMiddle => Color.FromArgb(204, 232, 255);
            public override Color ButtonCheckedGradientEnd => Color.FromArgb(204, 232, 255);
            public override Color ButtonCheckedHighlight => Color.FromArgb(204, 232, 255);
            public override Color MenuItemSelected => Color.FromArgb(229, 243, 255);
            public override Color MenuItemBorder => Color.FromArgb(204, 232, 255);
            public override Color StatusStripGradientBegin => Color.FromArgb(248, 248, 248);
            public override Color StatusStripGradientEnd => Color.FromArgb(248, 248, 248);
            public override Color SeparatorDark => Color.FromArgb(225, 225, 225);
            public override Color SeparatorLight => bg;
        }

        void ApplyWindowMode()
        {
            Text = cfg.WindowTitle;
            if (cfg.Kiosk)
            {
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Normal;
                WindowState = FormWindowState.Maximized;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                WindowState = FormWindowState.Maximized;
            }
        }

        void SetPane(string mode)
        {
            cfg.Pane = mode;
            bool show = mode != "None";
            split2.Panel2Collapsed = !show;
            if (show)
            {
                try { split2.SplitterDistance = Math.Max(200, split2.Width - (int)(Math.Max(220, cfg.PaneWidth) * S)); } catch { }
                pane.DetailsOnly = mode == "Details";
                OnSelectionChanged();
            }
            else pane.ReleaseFiles();
            bPreview.Checked = mode == "Preview";
            bDetails.Checked = mode == "Details";
            SavePrefs();
        }

        void SavePrefs()
        {
            try { ConfigStore.Save(cfg); } catch { }
        }

        // ============================================================ navigation
        void Navigate(Loc loc, bool push = true)
        {
            if (!Allowed(loc)) { Status("המיקום אינו זמין."); loc = Loc.Home(); }
            List<Entry> items;
            Cursor = Cursors.WaitCursor;
            try { items = LoadEntries(loc); }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                Status("לא ניתן לפתוח: " + ex.Message);
                if (loc.Kind != LocKind.Home) Navigate(Loc.Home(), push);
                return;
            }
            finally { Cursor = Cursors.Default; }

            if (push && !loc.SameAs(current)) { back.Push(current); fwd.Clear(); }
            if (loc.Kind != LocKind.Search) CancelSearchSilently();
            current = loc;
            entries = items;
            if (loc.Kind == LocKind.Device) { var d = guard.DeviceOf(loc.Path); if (d != null) { lastDeviceFolder[d.Root] = loc.Path; lastDevice = d.Root; } }
            Populate();
            UpdateCrumbs();
            SyncTree();
            UpdateCommands();
            Watch(loc.Kind == LocKind.Public || loc.Kind == LocKind.Device ? loc.Path : null);
        }

        void Watch(string dir)
        {
            if (watcher != null && dir != null && PathUtil.Same(watcher.Path, dir)) return;
            StopWatching();
            if (dir == null) return;
            try
            {
                watcher = new FileSystemWatcher(dir) { IncludeSubdirectories = false, SynchronizingObject = this,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                FileSystemEventHandler h = (s, e) => { watchTimer.Stop(); watchTimer.Start(); };
                watcher.Created += h; watcher.Deleted += h; watcher.Changed += h;
                watcher.Renamed += (s, e) => { watchTimer.Stop(); watchTimer.Start(); };
                watcher.Error += (s, e) => StopWatching();
                watcher.EnableRaisingEvents = true;
            }
            catch { StopWatching(); }
        }

        void StopWatching()
        {
            watchTimer.Stop();
            if (watcher == null) return;
            try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch { }
            watcher = null;
        }

        /// <summary>Something changed in the folder on screen (e.g. Word saved a document): reload if what's shown differs.</summary>
        void OnFolderChanged()
        {
            if (current.Kind != LocKind.Public && current.Kind != LocKind.Device) return;
            if (busy || editingLabel || OwnedForms.Length > 0) { watchTimer.Start(); return; }
            List<Entry> fresh;
            try { fresh = Allowed(current) ? LoadEntries(current) : null; } catch { fresh = null; }
            if (fresh == null) { Navigate(UpOf(current) ?? Loc.Home(), false); return; }
            string Sig(IEnumerable<Entry> l) => string.Join("|", l.Select(x => x.Name + "*" + x.Size + "*" + x.Modified.Ticks).OrderBy(x => x, StringComparer.Ordinal));
            if (Sig(fresh) == Sig(entries)) return;
            string top = (list.View == View.Details || list.View == View.List) && list.TopItem?.Tag is Entry te ? te.Path : null;
            RefreshView();
            if (top != null)
                foreach (ListViewItem it in list.Items)
                    if (it.Tag is Entry en && PathUtil.Same(en.Path, top)) { try { list.TopItem = it; } catch { } break; }
        }

        bool Allowed(Loc l)
        {
            switch (l.Kind)
            {
                case LocKind.Home: return true;
                case LocKind.Public: return guard.PublicAllowed(l.Path) && Directory.Exists(l.Path);
                case LocKind.Device: return guard.DeviceAllowed(l.Path) && Directory.Exists(l.Path);
                case LocKind.Trash: return guard.IsSpaceRoot(l.Path);
                case LocKind.Search: return l.Base != null && Allowed(l.Base);
            }
            return false;
        }

        List<Entry> LoadEntries(Loc l)
        {
            switch (l.Kind)
            {
                case LocKind.Public:
                case LocKind.Device:
                    return Lister.List(l.Path, FilterFor(l.Path));
                case LocKind.Trash:
                    return Trash.List(l.Path).Select(t =>
                    {
                        FileSystemInfo fi = Directory.Exists(t.ItemPath) ? (FileSystemInfo)new DirectoryInfo(t.ItemPath) : new FileInfo(t.ItemPath);
                        var e = Lister.ToEntry(fi);
                        e.Trash = t;
                        e.Location = Path.Combine(l.Path, Path.GetDirectoryName(t.OriginalRel) ?? "");
                        e.Modified = t.Deleted;
                        return e;
                    }).ToList();
                case LocKind.Search:
                    return entries;   // filled by the search task
            }
            return new List<Entry>();
        }

        Func<FileSystemInfo, bool> FilterFor(string anyPath) => guard.FilterFor(anyPath);

        Loc LocFor(string path)
        {
            if (guard.RootOf(path) != null) return new Loc { Kind = LocKind.Public, Path = path };
            if (guard.DeviceOf(path) != null) return new Loc { Kind = LocKind.Device, Path = path };
            return null;
        }

        void GoBack()
        {
            while (back.Count > 0)
            {
                var l = back.Pop();
                if (!Allowed(l)) continue;
                fwd.Push(current);
                Navigate(l, false);
                return;
            }
        }

        void GoForward()
        {
            while (fwd.Count > 0)
            {
                var l = fwd.Pop();
                if (!Allowed(l)) continue;
                back.Push(current);
                Navigate(l, false);
                return;
            }
        }

        void GoUp()
        {
            var up = UpOf(current);
            if (up != null) Navigate(up);
        }

        Loc UpOf(Loc l)
        {
            switch (l.Kind)
            {
                case LocKind.Search: return l.Base;
                case LocKind.Trash: return new Loc { Kind = LocKind.Device, Path = l.Path };
                case LocKind.Public:
                    {
                        var r = guard.RootOf(l.Path);
                        if (r == null || PathUtil.Same(r.Path, l.Path)) return Loc.Home();
                        return new Loc { Kind = LocKind.Public, Path = Path.GetDirectoryName(PathUtil.Norm(l.Path)) };
                    }
                case LocKind.Device:
                    {
                        var d = guard.DeviceOf(l.Path);
                        if (d == null || PathUtil.Same(d.Root, l.Path)) return Loc.Home();
                        return new Loc { Kind = LocKind.Device, Path = Path.GetDirectoryName(PathUtil.Norm(l.Path)) };
                    }
            }
            return null;
        }

        void RefreshView()
        {
            if (current.Kind == LocKind.Search) { Populate(); return; }
            var sel = new HashSet<string>(SelectedEntries().Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
            Navigate(current, false);
            if (sel.Count > 0)
                foreach (ListViewItem it in list.Items)
                    if (it.Tag is Entry e && sel.Contains(e.Path)) it.Selected = true;
        }

        // ============================================================ list population
        void ConfigureColumns()
        {
            list.Columns.Clear();
            int W(int px) => (int)(px * S);
            switch (current.Kind)
            {
                case LocKind.Home:
                    list.Columns.Add("שם", W(260)); list.Columns.Add("פרטים", W(260));
                    colKeys = new string[0];
                    break;
                case LocKind.Trash:
                    list.Columns.Add("שם", W(260)); list.Columns.Add("מיקום מקורי", W(240));
                    list.Columns.Add("תאריך מחיקה", W(140)); list.Columns.Add("גודל", W(90), HorizontalAlignment.Left);
                    colKeys = new[] { "name", "loc", "date", "size" };
                    break;
                case LocKind.Search:
                    list.Columns.Add("שם", W(260)); list.Columns.Add("מיקום", W(280));
                    list.Columns.Add("תאריך שינוי", W(140)); list.Columns.Add("סוג", W(140)); list.Columns.Add("גודל", W(90), HorizontalAlignment.Left);
                    colKeys = new[] { "name", "loc", "date", "type", "size" };
                    break;
                default:
                    list.Columns.Add("שם", W(320)); list.Columns.Add("תאריך שינוי", W(140));
                    list.Columns.Add("סוג", W(160)); list.Columns.Add("גודל", W(90), HorizontalAlignment.Left);
                    colKeys = new[] { "name", "date", "type", "size" };
                    break;
            }
        }

        void Populate()
        {
            pane.ReleaseFiles();
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                list.Groups.Clear();
                pendingIcons.Clear();
                requestedExt.Clear();
                Interlocked.Increment(ref thumbGen);
                while (thumbQueue.TryTake(out _)) { }
                ConfigureViewForLocation();
                ConfigureColumns();

                if (current.Kind == LocKind.Home) { PopulateHome(); return; }

                var items = new List<ListViewItem>(entries.Count);
                foreach (var e in entries) items.Add(MakeItem(e));
                list.ListViewItemSorter = null;
                list.Items.AddRange(items.ToArray());
                ApplySort();
                if (entries.Count == 0)
                {
                    // explorer-like empty hint
                    var hint = new ListViewItem(current.Kind == LocKind.Search ? "לא נמצאו פריטים." :
                                                current.Kind == LocKind.Trash ? "הסל ריק." : "התיקיה ריקה.") { ForeColor = Color.Gray };
                    list.Items.Add(hint);
                }
            }
            finally
            {
                list.EndUpdate();
                UpdateStatus();
                OnSelectionChanged();
            }
        }

        ListViewItem MakeItem(Entry e)
        {
            var it = new ListViewItem(e.Name) { Tag = e };
            it.ImageKey = IconKey(e, it);
            string date = e.Modified.ToString("dd/MM/yyyy HH:mm");
            string type = Native.TypeName(e.Ext, e.IsDir);
            string size = e.IsDir ? "" : PathUtil.Size(e.Size);
            switch (current.Kind)
            {
                case LocKind.Trash:
                    it.SubItems.Add(e.Location); it.SubItems.Add(date); it.SubItems.Add(size); break;
                case LocKind.Search:
                    it.SubItems.Add(e.Location); it.SubItems.Add(date); it.SubItems.Add(type); it.SubItems.Add(size); break;
                default:
                    it.SubItems.Add(date); it.SubItems.Add(type); it.SubItems.Add(size); break;
            }
            if (clipCut && clip.Any(c => PathUtil.Same(c, e.Path))) it.ForeColor = Color.Gray;
            return it;
        }

        void PopulateHome()
        {
            var gPub = new ListViewGroup("pub", "תיקיות לציבור");
            var gWork = new ListViewGroup("work", "תיקיות עבודה (קריאה וכתיבה)");
            var gDev = new ListViewGroup("dev", "התקנים מחוברים (נגנים, דיסק און קי)");
            list.Groups.Add(gPub);
            if (cfg.WorkRoots.Count > 0) list.Groups.Add(gWork);
            list.Groups.Add(gDev);
            EnsureLarge("L:dir", () => Native.ShellImage(cfg.Roots.FirstOrDefault(r => Directory.Exists(r.Path))?.Path ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows), largeSize, Native.SIIGBF_ICONONLY) ?? Native.ExtIcon("dir", false));
            foreach (var r in cfg.Roots)
            {
                bool ok = Directory.Exists(r.Path);
                var it = new ListViewItem(r.Title, gPub) { Tag = ok ? new Loc { Kind = LocKind.Public, Path = r.Path } : null, ImageKey = "L:dir" };
                it.SubItems.Add(ok ? "קריאה והעתקה בלבד" : "התיקיה לא נמצאה");
                if (!ok) it.ForeColor = Color.Firebrick;
                list.Items.Add(it);
            }
            if (cfg.Roots.Count == 0)
            {
                var it = new ListViewItem("לא הוגדרו תיקיות", gPub) { ForeColor = Color.Gray, ImageKey = "L:dir" };
                it.SubItems.Add("מנהל: לחצו על ⚙ כדי לבחור תיקיות לציבור");
                list.Items.Add(it);
            }
            foreach (var r in cfg.WorkRoots)
            {
                bool ok = Directory.Exists(r.Path);
                string key = "L:work:" + r.Path;
                if (ok) EnsureLarge(key, () => Native.ShellImage(r.Path, largeSize, Native.SIIGBF_ICONONLY) ?? Native.ExtIcon("dir", false));
                var it = new ListViewItem(r.Title, gWork) { Tag = ok ? new Loc { Kind = LocKind.Device, Path = PathUtil.Norm(r.Path) } : null, ImageKey = ok ? key : "L:dir" };
                it.SubItems.Add(ok ? "קריאה, כתיבה ויצירת מסמכים" : "התיקיה לא נמצאה");
                if (!ok) it.ForeColor = Color.Firebrick;
                list.Items.Add(it);
            }
            foreach (var d in devices)
            {
                DeviceScanner.Refresh(d);
                string key = "L:drv:" + d.Root;
                EnsureLarge(key, () => Native.ShellImage(d.Root, largeSize, Native.SIIGBF_ICONONLY) ?? Native.PathIcon(d.Root, false));
                var it = new ListViewItem(d.Display, gDev) { Tag = new Loc { Kind = LocKind.Device, Path = d.Root }, ImageKey = key };
                it.SubItems.Add(PathUtil.Size(d.Free) + " פנויים מתוך " + PathUtil.Size(d.Total));
                list.Items.Add(it);
            }
            if (devices.Count == 0)
            {
                EnsureLarge("L:usb", () => Native.Fit(Glyphs.Get(Glyphs.Usb, Color.Silver, 40), largeSize, true));
                var it = new ListViewItem("לא מחובר התקן", gDev) { ForeColor = Color.Gray, ImageKey = "L:usb" };
                it.SubItems.Add("חברו נגן או דיסק און קי והוא יופיע כאן");
                list.Items.Add(it);
            }
        }

        void EnsureLarge(string key, Func<Image> make)
        {
            if (largeIL.Images.ContainsKey(key)) return;
            Image img = null;
            try { img = make(); } catch { }
            if (img == null) img = new Bitmap(largeSize, largeSize);
            largeIL.Images.Add(key, img.Width == largeSize && img.Height == largeSize ? img : Native.Fit(img, largeSize, img.Width < 64));
        }

        bool LargeMode => list.View == View.LargeIcon || list.View == View.Tile;

        static readonly HashSet<string> thumbExt = new HashSet<string>(
            PreviewPane.ImageExt.Concat(PreviewPane.VideoExt).Concat(new[] { ".webp", ".heic", ".pdf" }), StringComparer.OrdinalIgnoreCase);

        string IconKey(Entry e, ListViewItem it)
        {
            string ext = e.IsDir ? "dir" : (string.IsNullOrEmpty(e.Ext) ? "." : e.Ext);
            if (!LargeMode)
            {
                if (!smallIL.Images.ContainsKey(ext)) AddSmall(ext, Native.ExtIcon(ext, true));
                return ext;
            }
            // large: placeholder from the 32px system icon, then the real icon/thumbnail from the shell
            string ph = "P:" + ext;
            EnsureLarge(ph, () => Native.ExtIcon(ext, false));
            string key;
            if ((!e.IsDir && thumbExt.Contains(ext)) || (e.IsDir && largeSize >= 96)) key = "T:" + e.Path;
            else key = "E:" + ext;
            if (largeIL.Images.ContainsKey(key)) return key;
            if (!pendingIcons.TryGetValue(key, out var waiting)) pendingIcons[key] = waiting = new List<ListViewItem>();
            waiting.Add(it);
            if (key.StartsWith("T:") || requestedExt.Add(ext))
                thumbQueue.Add(new ThumbReq
                {
                    Gen = thumbGen, Key = key, Path = e.Path, Size = largeSize,
                    Flags = key.StartsWith("T:") ? Native.SIIGBF_RESIZETOFIT : Native.SIIGBF_ICONONLY
                });
            return ph;
        }

        void ThumbLoop()
        {
            foreach (var r in thumbQueue.GetConsumingEnumerable())
            {
                if (r.Gen != thumbGen) continue;
                Bitmap bmp = Native.ShellImage(r.Path, r.Size, r.Flags);
                if (bmp == null) continue;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (r.Gen != thumbGen || r.Size != largeSize || largeIL == null) { bmp.Dispose(); return; }
                        if (!largeIL.Images.ContainsKey(r.Key)) largeIL.Images.Add(r.Key, bmp);
                        if (pendingIcons.TryGetValue(r.Key, out var items))
                        {
                            foreach (var it in items) if (it.ListView != null) it.ImageKey = r.Key;
                            pendingIcons.Remove(r.Key);
                        }
                    }));
                }
                catch { bmp.Dispose(); }
            }
        }

        // ============================================================ view modes
        void ConfigureViewForLocation()
        {
            View v; int size;
            if (current.Kind == LocKind.Home) { v = View.Tile; size = 48; }
            else
            {
                switch (cfg.ViewMode)
                {
                    case "XL": v = View.LargeIcon; size = 192; break;
                    case "L": v = View.LargeIcon; size = 96; break;
                    case "M": v = View.LargeIcon; size = 48; break;
                    case "S": v = View.SmallIcon; size = 48; break;
                    case "List": v = View.List; size = 48; break;
                    case "Tiles": v = View.Tile; size = 48; break;
                    default: v = View.Details; size = 48; break;
                }
            }
            size = (int)Math.Round(size * S);
            if (size > 256) size = 256;
            if (largeIL == null || largeSize != size)
            {
                var old = largeIL;
                largeIL = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(size, size) };
                largeSize = size;
                list.LargeImageList = largeIL;
                old?.Dispose();
            }
            bool checks = cfg.CheckBoxes && v != View.Tile && current.Kind != LocKind.Home;
            if (list.CheckBoxes != checks && !checks) list.CheckBoxes = false;
            if (list.View != v) list.View = v;
            if (list.CheckBoxes != checks) list.CheckBoxes = checks;
            list.ShowGroups = current.Kind == LocKind.Home;
            if (v == View.Tile) list.TileSize = new Size((int)(320 * S), (int)(Math.Max(56, largeSize + 12)));
            bChecks.Checked = cfg.CheckBoxes;
        }

        void ApplyViewMode()
        {
            if (current.Kind == LocKind.Search) { var sel = SelectedEntries(); Populate(); return; }
            RefreshView();
        }

        void FillViewMenu(ToolStripItemCollection items)
        {
            items.Clear();
            for (int i = 0; i < ViewNames.Length; i++)
            {
                string name = ViewNames[i];
                var mi = new ToolStripMenuItem(ViewTitles[i]) { Checked = cfg.ViewMode == name, Enabled = current.Kind != LocKind.Home };
                mi.Click += (s, e) => { cfg.ViewMode = name; ApplyViewMode(); SavePrefs(); };
                items.Add(mi);
            }
            items.Add(new ToolStripSeparator());
            var cb = new ToolStripMenuItem("תיבות סימון") { Checked = cfg.CheckBoxes };
            cb.Click += (s, e) => { cfg.CheckBoxes = !cfg.CheckBoxes; ApplyViewMode(); SavePrefs(); };
            items.Add(cb);
            var pv = new ToolStripMenuItem("חלונית תצוגה מקדימה") { Checked = cfg.Pane == "Preview" };
            pv.Click += (s, e) => SetPane(cfg.Pane == "Preview" ? "None" : "Preview");
            var dt = new ToolStripMenuItem("חלונית פרטים") { Checked = cfg.Pane == "Details" };
            dt.Click += (s, e) => SetPane(cfg.Pane == "Details" ? "None" : "Details");
            items.Add(pv); items.Add(dt);
        }

        void FillSortMenu(ToolStripItemCollection items)
        {
            items.Clear();
            var opts = new List<(string, string)> { ("name", "שם"), ("date", current.Kind == LocKind.Trash ? "תאריך מחיקה" : "תאריך שינוי"), ("type", "סוג"), ("size", "גודל") };
            if (current.Kind == LocKind.Search || current.Kind == LocKind.Trash) opts.Add(("loc", "מיקום"));
            foreach (var o in opts)
            {
                var k = o.Item1;
                var mi = new ToolStripMenuItem(o.Item2) { Checked = sortKey == k, Enabled = current.Kind != LocKind.Home };
                mi.Click += (s, e) => SortBy(k, sortKey == k ? !sortAsc : true);
                items.Add(mi);
            }
            items.Add(new ToolStripSeparator());
            var a = new ToolStripMenuItem("סדר עולה") { Checked = sortAsc };
            a.Click += (s, e) => SortBy(sortKey, true);
            var d = new ToolStripMenuItem("סדר יורד") { Checked = !sortAsc };
            d.Click += (s, e) => SortBy(sortKey, false);
            items.Add(a); items.Add(d);
        }

        void SortBy(string key, bool? asc)
        {
            if (current.Kind == LocKind.Home) return;
            sortAsc = asc ?? (sortKey == key ? !sortAsc : true);
            sortKey = key;
            ApplySort();
        }

        void ApplySort()
        {
            if (current.Kind == LocKind.Home) return;
            list.ListViewItemSorter = new ItemSorter(sortKey, sortAsc);
            list.Sort();
            int col = Array.IndexOf(colKeys, sortKey);
            if (list.View == View.Details) Native.SetSortArrow(list, col, sortAsc);
        }

        class ItemSorter : System.Collections.IComparer
        {
            readonly string key; readonly bool asc;
            public ItemSorter(string key, bool asc) { this.key = key; this.asc = asc; }
            public int Compare(object x, object y)
            {
                var a = ((ListViewItem)x).Tag as Entry; var b = ((ListViewItem)y).Tag as Entry;
                if (a == null || b == null) return a == null ? (b == null ? 0 : 1) : -1;
                if (a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;   // folders first, like Explorer
                int c = 0;
                switch (key)
                {
                    case "date": c = a.Modified.CompareTo(b.Modified); break;
                    case "size": c = a.Size.CompareTo(b.Size); break;
                    case "type": c = string.Compare(Native.TypeName(a.Ext, a.IsDir), Native.TypeName(b.Ext, b.IsDir), StringComparison.CurrentCultureIgnoreCase); break;
                    case "loc": c = Native.StrCmpLogicalW(a.Location ?? "", b.Location ?? ""); break;
                }
                if (c == 0) c = Native.StrCmpLogicalW(a.Name, b.Name);
                return asc ? c : -c;
            }
        }

        // ============================================================ crumbs / status / commands
        void UpdateCrumbs()
        {
            crumbs.SuspendLayout();
            crumbs.Items.Clear();
            void Crumb(string text, Loc target, Image img = null)
            {
                if (crumbs.Items.Count > 0) crumbs.Items.Add(new ToolStripLabel("›") { ForeColor = Color.Gray, Margin = new Padding(0) });
                var b = new ToolStripButton(text) { Image = img, ImageScaling = ToolStripItemImageScaling.None, Margin = new Padding(1) };
                if (target != null) b.Click += (s, e) => Navigate(target); else b.Enabled = false;
                crumbs.Items.Add(b);
            }
            Crumb("בית", Loc.Home(), Glyphs.Get(Glyphs.Home, Color.FromArgb(0, 95, 184), 14));
            Loc l = current.Kind == LocKind.Search ? current.Base : current;
            if (l.Kind == LocKind.Public)
            {
                var r = guard.RootOf(l.Path);
                if (r != null)
                {
                    Crumb(r.Title, new Loc { Kind = LocKind.Public, Path = r.Path });
                    string acc = PathUtil.Norm(r.Path);
                    foreach (var part in PathUtil.Relative(l.Path, r.Path).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        acc = Path.Combine(acc, part);
                        Crumb(part, new Loc { Kind = LocKind.Public, Path = acc });
                    }
                }
            }
            else if (l.Kind == LocKind.Device || l.Kind == LocKind.Trash)
            {
                var d = guard.DeviceOf(l.Path);
                if (d != null)
                {
                    Crumb(d.Display, new Loc { Kind = LocKind.Device, Path = d.Root });
                    if (l.Kind == LocKind.Trash) Crumb("סל מחזור", l);
                    else
                    {
                        string acc = d.Root;
                        foreach (var part in PathUtil.Relative(l.Path, d.Root).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            acc = Path.Combine(acc, part);
                            Crumb(part, new Loc { Kind = LocKind.Device, Path = acc });
                        }
                    }
                }
            }
            if (current.Kind == LocKind.Search) Crumb("תוצאות חיפוש: \"" + current.SearchText + "\"", null);
            crumbs.ResumeLayout();
        }

        void UpdateStatus()
        {
            int n = entries.Count;
            stCount.Text = current.Kind == LocKind.Home ? "" : n + " פריטים";
            var sel = SelectedEntries();
            if (sel.Count > 0)
            {
                long sz = sel.Where(e => !e.IsDir).Sum(e => e.Size);
                stSel.Text = "   " + sel.Count + " נבחרו" + (sz > 0 ? "  (" + PathUtil.Size(sz) + ")" : "");
            }
            else stSel.Text = "";
            var d = CurrentDevice();
            if (d != null) { DeviceScanner.Refresh(d); stFree.Text = d.Display + ": " + PathUtil.Size(d.Free) + " פנויים"; }
            else stFree.Text = devices.Count == 0 ? "לא מחובר התקן" : devices.Count == 1 ? "מחובר: " + devices[0].Display : "מחוברים " + devices.Count + " התקנים";
        }

        void Status(string msg)
        {
            stMsg.Text = msg;
            statusClear.Stop(); statusClear.Start();
        }
        readonly System.Windows.Forms.Timer statusClear = CreateStatusTimer();
        static System.Windows.Forms.Timer CreateStatusTimer() => new System.Windows.Forms.Timer { Interval = 8000 };

        /// <summary>The writable space on screen: a device or a work folder.</summary>
        Device CurrentSpace()
        {
            if (current.Effective == LocKind.Device || current.Kind == LocKind.Trash)
                return guard.DeviceOf(current.Kind == LocKind.Search ? current.Base.Path : current.Path);
            return null;
        }

        /// <summary>The physical device on screen (not a work folder).</summary>
        Device CurrentDevice() => CurrentSpace() is Device d && !d.IsFolder ? d : null;

        List<Entry> SelectedEntries() => list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as Entry).Where(e => e != null).ToList();

        void OnSelectionChanged()
        {
            UpdateStatus();
            UpdateCommands();
            if (split2.Panel2Collapsed) return;
            var sel = SelectedEntries();
            if (sel.Count == 1) { pane.ShowEntry(sel[0]); return; }
            if (sel.Count > 1)
            {
                long sz = sel.Where(e => !e.IsDir).Sum(e => e.Size);
                pane.ShowSummary(sel.Count + " פריטים נבחרו", new[] { ("גודל כולל:", PathUtil.Size(sz) + (sel.Any(e => e.IsDir) ? " (ללא תיקיות)" : "")) });
                return;
            }
            if (current.Kind == LocKind.Home && list.SelectedItems.Count == 1 && list.SelectedItems[0].Tag is Loc hl)
            {
                if (hl.Kind == LocKind.Device)
                {
                    var d = guard.DeviceOf(hl.Path);
                    if (d != null && d.IsFolder) pane.ShowSummary(d.Display, new[] { ("הרשאה:", "קריאה, כתיבה ויצירת מסמכים") }, Native.ShellImage(d.Root, 96, Native.SIIGBF_ICONONLY));
                    else if (d != null) pane.ShowSummary(d.Display, new[] { ("סוג:", string.IsNullOrEmpty(d.Model) ? "התקן נשלף" : d.Model), ("מערכת קבצים:", d.Format), ("פנוי:", PathUtil.Size(d.Free)), ("גודל כולל:", PathUtil.Size(d.Total)) }, Native.ShellImage(d.Root, 96, Native.SIIGBF_ICONONLY));
                }
                else
                {
                    var r = guard.RootOf(hl.Path);
                    pane.ShowSummary(r?.Title ?? "", new[] { ("הרשאה:", "קריאה והעתקה בלבד") }, Native.ExtIcon("dir", false));
                }
                return;
            }
            pane.Clear();
        }

        void UpdateCommands()
        {
            var sel = SelectedEntries();
            var eff = current.Effective;
            bool inTrash = current.Kind == LocKind.Trash;
            bool dev = eff == LocKind.Device && !inTrash;
            bool anyOther = OtherDevices().Count > 0;

            bBack.Enabled = back.Count > 0;
            bFwd.Enabled = fwd.Count > 0;
            bUp.Enabled = current.Kind != LocKind.Home;

            bSend.Visible = !inTrash;
            bSend.Enabled = sel.Count > 0 && (eff == LocKind.Public || eff == LocKind.Device) && anyOther;
            bSend.ToolTipText = devices.Count == 0 ? "חברו נגן או דיסק און קי כדי להעתיק אליו" : "העתקת הפריטים שנבחרו להתקן";
            bNew.Visible = bCut.Visible = bPaste.Visible = bRename.Visible = bDelete.Visible = !inTrash && current.Kind != LocKind.Home;
            bCopy.Visible = !inTrash && current.Kind != LocKind.Home;
            bNew.Enabled = dev && current.Kind == LocKind.Device;
            bCut.Enabled = dev && sel.Count > 0;
            bCopy.Enabled = sel.Count > 0 && (eff == LocKind.Public || dev);
            bPaste.Enabled = current.Kind == LocKind.Device && clip.Count > 0;
            bRename.Enabled = dev && sel.Count == 1;
            bDelete.Enabled = dev && sel.Count > 0 && cfg.AllowDeviceDelete;
            bDelete.Visible &= cfg.AllowDeviceDelete;
            bRestore.Visible = bEmpty.Visible = inTrash;
            bRestore.Enabled = sel.Count > 0;
            bEmpty.Enabled = entries.Count > 0;
            bEject.Visible = CurrentDevice() != null;
            lblReadOnly.Visible = eff == LocKind.Public && current.Kind != LocKind.Home;
            bSelAll.Enabled = bSelNone.Enabled = current.Kind != LocKind.Home;
        }

        List<Device> OtherDevices()
        {
            var cd = current.Effective == LocKind.Device ? CurrentDevice() : null;
            return devices.Where(d => cd == null || !PathUtil.Same(d.Root, cd.Root)).ToList();
        }

        // ============================================================ open / select
        void OpenSelected()
        {
            if (list.SelectedItems.Count == 0) return;
            OpenItem(list.FocusedItem != null && list.FocusedItem.Selected ? list.FocusedItem : list.SelectedItems[0]);
        }

        void OpenItem(ListViewItem item)
        {
            var tag = item?.Tag;
            if (tag is Loc l) { Navigate(l); return; }
            if (!(tag is Entry e)) return;
            if (e.Trash != null) return;
            if (e.IsDir)
            {
                var target = LocFor(e.Path);
                if (target != null) Navigate(target);
                return;
            }
            if (guard.CanOpen(e.Ext)) { OpenExternal(e); return; }
            // other files are never handed to other programs — they open in the built-in preview
            ShowPreview(e);
        }

        void ShowPreview(Entry e)
        {
            if (split2.Panel2Collapsed || cfg.Pane == "Details") SetPane("Preview");
            pane.ShowEntry(e, true);
        }

        static readonly string openTemp = Path.Combine(Path.GetTempPath(), "KosherExplorer", "open");

        /// <summary>Opens a file in its own program (Word etc.). A file from a read-only public folder opens as a read-only copy.</summary>
        void OpenExternal(Entry e)
        {
            string path = e.Path;
            bool writable = guard.DeviceAllowed(path);
            if (!writable && !guard.PublicAllowed(path)) return;
            if (!guard.CanOpen(Path.GetExtension(path)) || !File.Exists(path)) return;
            try
            {
                if (!writable)
                {
                    string dir = Path.Combine(openTemp, Guid.NewGuid().ToString("N").Substring(0, 8));
                    Directory.CreateDirectory(dir);
                    string copy = Path.Combine(dir, Path.GetFileName(path));
                    File.Copy(path, copy);
                    File.SetAttributes(copy, FileAttributes.ReadOnly);
                    path = copy;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) });
                Status("נפתח: " + e.Name + (writable ? "" : "  (עותק לקריאה בלבד)"));
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1155 /*no association*/)
            {
                Msg("אין במחשב תוכנה שפותחת קבצי " + e.Ext + ".\nהקובץ מוצג בתצוגה המקדימה.", MessageBoxIcon.Information);
                ShowPreview(e);
            }
            catch (Exception ex) { Msg("פתיחת הקובץ נכשלה:\n" + ex.Message, MessageBoxIcon.Warning); }
        }

        /// <summary>Removes the read-only copies of earlier sessions (files still open in Word are skipped).</summary>
        static void CleanOpenTemp()
        {
            try
            {
                if (!Directory.Exists(openTemp)) return;
                foreach (var d in Directory.GetDirectories(openTemp))
                    try { if (Directory.GetCreationTime(d) < DateTime.Now.AddHours(-12)) FileOps.ForceDelete(d); } catch { }
            }
            catch { }
        }

        void SelectAll()
        {
            list.BeginUpdate();
            foreach (ListViewItem it in list.Items) if (it.Tag is Entry) it.Selected = true;
            list.EndUpdate();
            list.Focus();
        }

        void SelectNone()
        {
            list.BeginUpdate();
            foreach (ListViewItem it in list.SelectedItems.Cast<ListViewItem>().ToList()) it.Selected = false;
            list.EndUpdate();
        }

        // ============================================================ operations
        void ToClip(bool cut)
        {
            var sel = SelectedEntries();
            if (sel.Count == 0) return;
            var eff = current.Effective;
            if (cut && eff != LocKind.Device) { Status("בתיקיות לציבור אפשר רק להעתיק."); return; }
            if (eff != LocKind.Public && eff != LocKind.Device) return;
            clip = sel.Select(e => e.Path).ToList();
            clipCut = cut;
            foreach (ListViewItem it in list.Items) if (it.Tag is Entry en) it.ForeColor = cut && clip.Contains(en.Path) ? Color.Gray : SystemColors.WindowText;
            Status((cut ? "נגזרו " : "הועתקו ללוח ") + clip.Count + " פריטים. עברו לתיקיה בהתקן או בתיקיית עבודה ולחצו הדבקה (Ctrl+V).");
            UpdateCommands();
        }

        void Paste()
        {
            if (current.Kind != LocKind.Device || clip.Count == 0) return;
            string target = current.Path;
            RunCopy(clip, target, clipCut);
            if (clipCut) { clip.Clear(); clipCut = false; }
        }

        void SendToDevice()
        {
            var sel = SelectedEntries();
            if (sel.Count == 0) { Status("בחרו קודם את הקבצים שרוצים להעתיק."); return; }
            var targets = OtherDevices();
            if (targets.Count == 0)
            {
                Msg("לא מחובר נגן או דיסק און קי.\nחברו את ההתקן למחשב ונסו שוב.", MessageBoxIcon.Information);
                return;
            }
            var pref = targets.FirstOrDefault(d => lastDevice != null && PathUtil.Same(d.Root, lastDevice)) ?? targets[0];
            lastDeviceFolder.TryGetValue(pref.Root, out var lastFolder);
            using (var dlg = new DevicePicker(guard, targets, pref, lastFolder, sel.Count))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Selected == null) return;
                var d = guard.DeviceOf(dlg.Selected);
                if (d != null) { lastDevice = d.Root; lastDeviceFolder[d.Root] = dlg.Selected; }
                RunCopy(sel.Select(e => e.Path).ToList(), dlg.Selected, false);
            }
        }

        void RunCopy(List<string> sources, string dest, bool move)
        {
            if (!guard.DeviceAllowed(dest)) { Msg("אפשר להעתיק רק אל התקן מחובר או אל תיקיית עבודה.", MessageBoxIcon.Warning); return; }
            var valid = new List<string>();
            foreach (var s in sources)
            {
                bool okPublic = guard.PublicAllowed(s);
                bool okDevice = guard.DeviceAllowed(s);
                if (move && !okDevice) continue;             // public content can never be moved away
                if ((okPublic || okDevice) && (File.Exists(s) || Directory.Exists(s))) valid.Add(s);
            }
            if (valid.Count == 0) { Status("הפריטים כבר אינם זמינים."); return; }
            var dev = guard.DeviceOf(dest);
            var job = new CopyJob
            {
                Sources = valid, DestDir = dest, Move = move, Filter = FilterFor(valid[0]),
                DestTitle = dev.Display + (PathUtil.Same(dest, dev.Root) ? "" : "\\" + PathUtil.Relative(dest, dev.Root))
            };
            pane.ReleaseFiles();
            ProgressForm pf;
            busy = true;
            try { using (pf = new ProgressForm(job)) pf.ShowDialog(this); }
            finally { busy = false; }
            string msg = pf.Cancelled ? "הפעולה בוטלה." :
                         (move ? "הועברו " : "הועתקו ") + pf.Done + " קבצים" + (pf.Skipped > 0 ? ", דולגו " + pf.Skipped : "") + " אל " + dev.Display + ".";
            Status(msg);
            if (pf.Errors.Count > 0)
                Msg("חלק מהפריטים לא הועתקו:\n\n" + string.Join("\n", pf.Errors.Take(15)) + (pf.Errors.Count > 15 ? "\n..." : ""), MessageBoxIcon.Warning);
            DeviceScanner.Refresh(dev);
            ReloadTreeAt(dest);
            if (move) foreach (var s in valid) ReloadTreeAt(Path.GetDirectoryName(s));
            if (current.Kind != LocKind.Search) RefreshView();
            else UpdateStatus();
        }

        void NewFolder()
        {
            if (current.Kind != LocKind.Device) return;
            try
            {
                string p = PathUtil.UniqueName(current.Path, "תיקיה חדשה");
                if (!guard.DeviceAllowed(p)) return;
                Directory.CreateDirectory(p);
                RefreshView();
                ReloadTreeAt(current.Path);
                EditNew(p);
            }
            catch (Exception ex) { Msg("יצירת התיקיה נכשלה:\n" + ex.Message, MessageBoxIcon.Error); }
        }

        /// <summary>Selects a freshly created item and opens its name for editing, like Explorer.</summary>
        void EditNew(string p)
        {
            foreach (ListViewItem it in list.Items)
                if (it.Tag is Entry e && PathUtil.Same(e.Path, p))
                {
                    list.SelectedItems.Cast<ListViewItem>().ToList().ForEach(x => x.Selected = false);
                    it.Selected = true; it.Focused = true; it.EnsureVisible();
                    list.Focus();
                    it.BeginEdit();
                    break;
                }
        }

        void FillNewMenu(ToolStripItemCollection items)
        {
            items.Clear();
            bool here = current.Kind == LocKind.Device && guard.DeviceAllowed(current.Path);
            var f = new ToolStripMenuItem("תיקיה", Glyphs.Get(Glyphs.NewFolder, Color.FromArgb(40, 40, 40)), (s, e) => NewFolder()) { ShortcutKeyDisplayString = "Ctrl+Shift+N", Enabled = here };
            items.Add(f);
            var types = ShellNew.Available().Where(t => guard.ExtShown(t.Ext, CurrentDevice() != null)).ToList();
            if (types.Count > 0) items.Add(new ToolStripSeparator());
            foreach (var t in types)
            {
                var tt = t;
                Image img = null;
                try { img = Native.ExtIcon(t.Ext, true); } catch { }
                items.Add(new ToolStripMenuItem(t.Name, img, (s, e) => NewFile(tt)) { Enabled = here });
            }
        }

        void NewFile(ShellNew.Kind t)
        {
            if (current.Kind != LocKind.Device) return;
            try
            {
                string p = PathUtil.UniqueName(current.Path, t.Name + " חדש" + t.Ext);
                if (!guard.DeviceAllowed(p)) return;
                ShellNew.Create(t, p);
                RefreshView();
                EditNew(p);
            }
            catch (Exception ex) { Msg("יצירת הקובץ נכשלה:\n" + ex.Message, MessageBoxIcon.Error); }
        }

        void Rename()
        {
            if (list.SelectedItems.Count != 1 || current.Effective != LocKind.Device) return;
            list.Focus();
            list.SelectedItems[0].BeginEdit();
        }

        void AfterLabelEdit(object sender, LabelEditEventArgs e)
        {
            editingLabel = false;
            var it = list.Items[e.Item];
            var en = it.Tag as Entry;
            if (e.Label == null || en == null) return;
            string name = e.Label.Trim();
            e.CancelEdit = true;   // we set the text ourselves after a successful rename
            if (name == en.Name) return;
            string err = PathUtil.ValidateName(name);
            if (err != null) { Msg(err, MessageBoxIcon.Warning); BeginInvoke((Action)(() => it.BeginEdit())); return; }
            string dir = Path.GetDirectoryName(en.Path);
            string target = Path.Combine(dir, name);
            if (!guard.DeviceAllowed(en.Path) || !guard.DeviceAllowed(target)) return;
            bool caseOnly = string.Equals(name, en.Name, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(target) || Directory.Exists(target))) { Msg("כבר קיים פריט בשם \"" + name + "\".", MessageBoxIcon.Warning); return; }
            if (!en.IsDir && !string.Equals(Path.GetExtension(name), en.Ext, StringComparison.OrdinalIgnoreCase) &&
                MessageBox.Show(this, "אם תשנו את סיומת הקובץ, ייתכן שהוא לא ייפתח כמו קודם.\nלשנות בכל זאת?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) != DialogResult.Yes) return;
            try
            {
                pane.ReleaseFiles();
                if (caseOnly)
                {
                    string tmp = Path.Combine(dir, Guid.NewGuid().ToString("N"));
                    if (en.IsDir) { Directory.Move(en.Path, tmp); Directory.Move(tmp, target); }
                    else { File.Move(en.Path, tmp); File.Move(tmp, target); }
                }
                else if (en.IsDir) Directory.Move(en.Path, target);
                else File.Move(en.Path, target);
                en.Path = target; en.Name = name; en.Ext = en.IsDir ? "" : Path.GetExtension(name).ToLowerInvariant();
                it.Text = name;
                if (en.IsDir) ReloadTreeAt(dir);
                OnSelectionChanged();
            }
            catch (Exception ex) { Msg("שינוי השם נכשל:\n" + ex.Message, MessageBoxIcon.Error); }
        }

        void Delete(bool permanent)
        {
            if (current.Effective != LocKind.Device || !cfg.AllowDeviceDelete) return;
            var sel = SelectedEntries().Where(e => guard.DeviceAllowed(e.Path)).ToList();
            if (sel.Count == 0) return;
            string what = sel.Count == 1 ? "את \"" + sel[0].Name + "\"" : "את " + sel.Count + " הפריטים שנבחרו";
            string q = permanent ? "למחוק לצמיתות " + what + "?\nלא ניתן יהיה לשחזר." : "להעביר " + what + " לסל המחזור של ההתקן?";
            if (MessageBox.Show(this, q, Text, MessageBoxButtons.YesNo, permanent ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) != DialogResult.Yes) return;
            pane.ReleaseFiles();
            var errors = new List<string>();
            Cursor = Cursors.WaitCursor;
            foreach (var e in sel)
            {
                try
                {
                    var d = guard.DeviceOf(e.Path);
                    if (permanent) FileOps.ForceDelete(e.Path); else Trash.Put(d.Root, e.Path);
                }
                catch (Exception ex) { errors.Add(e.Name + ": " + ex.Message); }
            }
            Cursor = Cursors.Default;
            if (errors.Count > 0) Msg("חלק מהפריטים לא נמחקו:\n\n" + string.Join("\n", errors.Take(15)), MessageBoxIcon.Warning);
            Status((permanent ? "נמחקו " : "הועברו לסל ") + (sel.Count - errors.Count) + " פריטים.");
            foreach (var dir in sel.Select(e => Path.GetDirectoryName(e.Path)).Distinct(StringComparer.OrdinalIgnoreCase)) ReloadTreeAt(dir);
            if (current.Kind == LocKind.Search)
            {
                entries.RemoveAll(e => sel.Contains(e) && !File.Exists(e.Path) && !Directory.Exists(e.Path));
                Populate();
            }
            else RefreshView();
        }

        void RestoreSelected()
        {
            if (current.Kind != LocKind.Trash) return;
            var sel = SelectedEntries();
            var errors = new List<string>();
            foreach (var e in sel)
            {
                try { Trash.Restore(current.Path, e.Trash); }
                catch (Exception ex) { errors.Add(e.Name + ": " + ex.Message); }
            }
            if (errors.Count > 0) Msg("חלק מהפריטים לא שוחזרו:\n\n" + string.Join("\n", errors.Take(15)), MessageBoxIcon.Warning);
            Status("שוחזרו " + (sel.Count - errors.Count) + " פריטים.");
            ReloadTreeAt(current.Path);
            RefreshView();
        }

        void EmptyTrash()
        {
            if (current.Kind != LocKind.Trash) return;
            var sel = SelectedEntries();
            var targets = sel.Count > 0 ? sel : entries;
            if (targets.Count == 0) return;
            string q = sel.Count > 0 ? "למחוק לצמיתות " + sel.Count + " פריטים מהסל?" : "לרוקן את סל המחזור של ההתקן? כל " + targets.Count + " הפריטים יימחקו לצמיתות.";
            if (MessageBox.Show(this, q, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2,
                    MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) != DialogResult.Yes) return;
            pane.ReleaseFiles();
            foreach (var e in targets.ToList()) try { Trash.Purge(e.Trash); } catch { }
            RefreshView();
        }

        void Eject(string root)
        {
            pane.ReleaseFiles();
            StopWatching();   // an open folder watch keeps the device busy
            Interlocked.Increment(ref thumbGen);
            while (thumbQueue.TryTake(out _)) { }
            if (guard.DeviceOf(current.Kind == LocKind.Search ? current.Base.Path ?? "" : current.Path ?? "") is Device cd && PathUtil.Same(cd.Root, root))
                Navigate(Loc.Home());
            GC.Collect(); GC.WaitForPendingFinalizers();
            try
            {
                dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                dynamic item = sh.NameSpace(17).ParseName(root);
                item.InvokeVerb("Eject");
                Status("ההתקן מוכן לניתוק. אפשר להוציא אותו.");
            }
            catch (Exception ex) { Msg("ההוצאה נכשלה. ייתכן שקובץ מההתקן עדיין פתוח.\n" + ex.Message, MessageBoxIcon.Warning); }
            deviceTimer.Stop(); deviceTimer.Interval = 1500; deviceTimer.Start();
        }

        // ============================================================ search
        void StartSearch()
        {
            string term = search.Text.Trim();
            if (term.Length == 0) { CancelSearch(); return; }
            Loc baseLoc = current.Kind == LocKind.Search ? current.Base : current;
            if (baseLoc.Kind == LocKind.Trash) { Status("אי אפשר לחפש בסל."); return; }
            var dirs = baseLoc.Kind == LocKind.Home
                ? cfg.Roots.Where(r => Directory.Exists(r.Path)).Select(r => r.Path).Concat(guard.Spaces().Select(d => d.Root)).ToList()
                : new List<string> { baseLoc.Path };
            CancelSearchSilently();
            var cts = searchCts = new CancellationTokenSource();
            var results = new List<Entry>();
            var filters = dirs.Select(d => (d, FilterFor(d))).ToList();
            var loc = new Loc { Kind = LocKind.Search, Base = baseLoc, SearchText = term };
            if (baseLoc.Kind == LocKind.Home) loc.Base = Loc.Home();
            Status("מחפש \"" + term + "\"...");
            Cursor = Cursors.AppStarting;
            Task.Run(() =>
            {
                foreach (var f in filters) Lister.Search(f.d, term, f.Item2, results, cts.Token);
            }).ContinueWith(t =>
            {
                if (cts.IsCancellationRequested) return;
                BeginInvoke((Action)(() =>
                {
                    Cursor = Cursors.Default;
                    if (cts != searchCts) return;
                    if (!loc.SameAs(current) || current.Kind != LocKind.Search) { back.Push(current); fwd.Clear(); }
                    current = loc;
                    lock (results) entries = results.ToList();
                    Populate();
                    UpdateCrumbs();
                    SyncTree();
                    UpdateCommands();
                    Status("נמצאו " + entries.Count + " תוצאות" + (entries.Count >= 3000 ? " (מוצגות הראשונות)" : "") + ".");
                }));
            });
        }

        void CancelSearch()
        {
            CancelSearchSilently();
            search.Text = "";
            if (current.Kind == LocKind.Search) Navigate(current.Base);
        }

        void CancelSearchSilently()
        {
            if (searchCts != null) { searchCts.Cancel(); searchCts = null; Cursor = Cursors.Default; }
        }

        // ============================================================ tree
        void BuildTree()
        {
            suppressTree = true;
            tree.BeginUpdate();
            tree.Nodes.Clear();
            nHome = new TreeNode("בית") { Tag = Loc.Home(), ImageKey = "home", SelectedImageKey = "home" };
            nPublic = new TreeNode("תיקיות לציבור") { Tag = Loc.Home(), ImageKey = "public", SelectedImageKey = "public" };
            foreach (var r in cfg.Roots.Where(r => Directory.Exists(r.Path)))
            {
                var n = new TreeNode(r.Title) { Tag = new Loc { Kind = LocKind.Public, Path = r.Path }, ImageKey = "dir", SelectedImageKey = "dir" };
                n.Nodes.Add(new TreeNode("..."));
                nPublic.Nodes.Add(n);
            }
            nWork = new TreeNode("תיקיות עבודה") { Tag = Loc.Home(), ImageKey = "work", SelectedImageKey = "work" };
            foreach (var w in guard.WorkSpaces())
            {
                var n = new TreeNode(w.Display) { Tag = new Loc { Kind = LocKind.Device, Path = w.Root }, ImageKey = "dir", SelectedImageKey = "dir" };
                n.Nodes.Add(new TreeNode("..."));
                nWork.Nodes.Add(n);
            }
            nDevices = new TreeNode("התקנים") { Tag = Loc.Home(), ImageKey = "devices", SelectedImageKey = "devices" };
            FillDeviceNodes();
            tree.Nodes.Add(nHome);
            tree.Nodes.Add(nPublic);
            if (nWork.Nodes.Count > 0) tree.Nodes.Add(nWork);
            tree.Nodes.Add(nDevices);
            nPublic.Expand(); nWork.Expand(); nDevices.Expand();
            tree.EndUpdate();
            suppressTree = false;
        }

        void FillDeviceNodes()
        {
            nDevices.Nodes.Clear();
            foreach (var d in devices)
            {
                string key = "drv:" + d.Root;
                if (!smallIL.Images.ContainsKey(key)) AddSmall(key, Native.PathIcon(d.Root, true));
                var n = new TreeNode(d.Display) { Tag = new Loc { Kind = LocKind.Device, Path = d.Root }, ImageKey = key, SelectedImageKey = key };
                n.Nodes.Add(new TreeNode("..."));
                nDevices.Nodes.Add(n);
            }
            if (devices.Count == 0) nDevices.Nodes.Add(new TreeNode("(לא מחובר התקן)") { ImageKey = "devices", SelectedImageKey = "devices", ForeColor = Color.Gray });
        }

        void LoadTreeChildren(TreeNode n)
        {
            if (!(n.Tag is Loc l) || (l.Kind != LocKind.Public && l.Kind != LocKind.Device)) return;
            if (!(n.Nodes.Count == 1 && n.Nodes[0].Tag == null && n.Nodes[0].Text == "...")) return;
            n.Nodes.Clear();
            try
            {
                if (l.Kind == LocKind.Public ? !guard.PublicAllowed(l.Path) : !guard.DeviceAllowed(l.Path)) return;
                var filter = FilterFor(l.Path);
                foreach (var e in Lister.List(l.Path, fi => fi is DirectoryInfo && filter(fi))
                                        .OrderBy(x => x.Name, Comparer<string>.Create(Native.StrCmpLogicalW)))
                {
                    var c = new TreeNode(e.Name) { Tag = new Loc { Kind = l.Kind, Path = e.Path }, ImageKey = "dir", SelectedImageKey = "dir" };
                    try { if (Directory.EnumerateDirectories(e.Path).Any()) c.Nodes.Add(new TreeNode("...")); } catch { }
                    n.Nodes.Add(c);
                }
                if (l.Kind == LocKind.Device && guard.IsSpaceRoot(l.Path) && cfg.AllowDeviceDelete)
                    n.Nodes.Add(new TreeNode("סל מחזור") { Tag = new Loc { Kind = LocKind.Trash, Path = l.Path }, ImageKey = "trash", SelectedImageKey = "trash" });
            }
            catch { }
        }

        void ReloadTreeAt(string path)
        {
            if (path == null) return;
            var n = FindNode(tree.Nodes, path);
            if (n == null) return;
            bool expanded = n.IsExpanded;
            suppressTree = true;
            n.Nodes.Clear();
            n.Nodes.Add(new TreeNode("..."));
            if (expanded) n.Expand(); else n.Collapse();
            suppressTree = false;
        }

        TreeNode FindNode(TreeNodeCollection nodes, string path)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Tag is Loc l && (l.Kind == LocKind.Public || l.Kind == LocKind.Device) && l.Path != null)
                {
                    if (PathUtil.Same(l.Path, path)) return n;
                    if (!PathUtil.IsUnder(path, l.Path)) continue;
                }
                var f = FindNode(n.Nodes, path);
                if (f != null) return f;
            }
            return null;
        }

        void SyncTree()
        {
            suppressTree = true;
            try
            {
                TreeNode target = null;
                var l = current.Kind == LocKind.Search ? null : current;
                if (l == null) { tree.SelectedNode = null; return; }
                if (l.Kind == LocKind.Home) target = nHome;
                else
                {
                    var group = l.Kind == LocKind.Public ? nPublic : guard.DeviceOf(l.Path)?.IsFolder == true ? nWork : nDevices;
                    TreeNode n = group.Nodes.Cast<TreeNode>().FirstOrDefault(x => x.Tag is Loc xl && xl.Path != null && PathUtil.IsUnder(l.Path, xl.Path));
                    if (l.Kind == LocKind.Trash && n != null)
                    {
                        n.Expand();
                        n = n.Nodes.Cast<TreeNode>().FirstOrDefault(x => x.Tag is Loc xl && xl.Kind == LocKind.Trash) ?? n;
                    }
                    else
                    {
                        while (n != null && !PathUtil.Same(((Loc)n.Tag).Path, l.Path))
                        {
                            n.Expand();
                            var next = n.Nodes.Cast<TreeNode>().FirstOrDefault(x => x.Tag is Loc xl && xl.Kind == l.Kind && PathUtil.IsUnder(l.Path, xl.Path));
                            if (next == null) break;
                            n = next;
                        }
                    }
                    target = n;
                }
                tree.SelectedNode = target;
                target?.EnsureVisible();
            }
            finally { suppressTree = false; }
        }

        void TreeMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            treeMenu.Items.Clear();
            var n = tree.SelectedNode;
            if (!(n?.Tag is Loc l)) { e.Cancel = true; return; }
            treeMenu.Items.Add("פתיחה", null, (s, a) => Navigate(l));
            if (l.Kind == LocKind.Device && guard.IsSpaceRoot(l.Path))
            {
                if (devices.Any(d => PathUtil.Same(d.Root, l.Path)))
                    treeMenu.Items.Add("הוצאה בטוחה", Glyphs.Get(Glyphs.Usb, Color.FromArgb(0, 120, 90)), (s, a) => Eject(l.Path));
                if (cfg.AllowDeviceDelete) treeMenu.Items.Add("סל מחזור", Glyphs.Get(Glyphs.Delete, Color.DimGray), (s, a) => Navigate(new Loc { Kind = LocKind.Trash, Path = l.Path }));
            }
        }

        // ============================================================ devices
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x0219 /*WM_DEVICECHANGE*/)
            {
                int ev = m.WParam.ToInt32();
                if (ev == 0x8000 || ev == 0x8004 || ev == 0x0007)
                {
                    deviceTimer.Stop(); deviceTimer.Interval = 900; deviceTimer.Start();
                }
            }
        }

        bool scanning;
        void RescanDevices()
        {
            if (scanning) { deviceTimer.Start(); return; }
            scanning = true;
            Task.Run(() => DeviceScanner.Scan(cfg)).ContinueWith(t =>
            {
                scanning = false;
                if (t.IsFaulted) return;
                BeginInvoke((Action)(() => ApplyDevices(t.Result)));
            });
        }

        void ApplyDevices(List<Device> fresh)
        {
            var oldRoots = devices.Select(d => d.Root).ToList();
            var newRoots = fresh.Select(d => d.Root).ToList();
            bool changed = !oldRoots.SequenceEqual(newRoots, StringComparer.OrdinalIgnoreCase);
            var added = fresh.Where(d => !oldRoots.Contains(d.Root, StringComparer.OrdinalIgnoreCase)).ToList();
            var removed = oldRoots.Where(r => !newRoots.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
            devices = fresh;
            if (!changed) { UpdateStatus(); return; }

            suppressTree = true;
            tree.BeginUpdate();
            FillDeviceNodes();
            nDevices.Expand();
            tree.EndUpdate();
            suppressTree = false;

            if (added.Count > 0) Status("התחבר: " + string.Join(", ", added.Select(d => d.Display)));
            else if (removed.Count > 0) Status("התנתק: " + string.Join(", ", removed));
            foreach (var r in removed) lastDeviceFolder.Remove(r);
            clip.RemoveAll(c => removed.Any(r => PathUtil.IsUnder(c, r)));

            var l = current.Kind == LocKind.Search ? current.Base : current;
            if ((l.Kind == LocKind.Device || l.Kind == LocKind.Trash) && !Allowed(l))
            {
                pane.ReleaseFiles();
                Navigate(Loc.Home(), false);
                Status("ההתקן נותק.");
            }
            else if (current.Kind == LocKind.Home) Navigate(Loc.Home(), false);
            else { SyncTree(); UpdateCommands(); UpdateStatus(); }
        }

        // ============================================================ drag & drop (internal only)
        void ListItemDrag(object sender, ItemDragEventArgs e)
        {
            var sel = SelectedEntries().Where(x => x.Trash == null).ToList();
            if (sel.Count == 0) return;
            var eff = current.Effective;
            if (eff != LocKind.Public && eff != LocKind.Device) return;
            var data = new DataObject();
            data.SetData(DragFormat, sel.Select(x => x.Path).ToArray());
            DoDragDrop(data, eff == LocKind.Device ? DragDropEffects.Copy | DragDropEffects.Move : DragDropEffects.Copy);
        }

        string[] DragPaths(DragEventArgs e) => e.Data.GetDataPresent(DragFormat) ? e.Data.GetData(DragFormat) as string[] : null;

        DragDropEffects DecideEffect(string[] paths, string target, int keyState)
        {
            if (paths == null || target == null || !guard.DeviceAllowed(target)) return DragDropEffects.None;
            if (paths.Any(p => Directory.Exists(p) && PathUtil.IsUnder(target, p))) return DragDropEffects.None;
            bool fromDevice = guard.DeviceAllowed(paths[0]);
            if (!fromDevice) return DragDropEffects.Copy;
            bool ctrl = (keyState & 8) != 0, shift = (keyState & 4) != 0;
            bool sameVol = string.Equals(Path.GetPathRoot(paths[0]), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
            bool move = sameVol ? !ctrl : shift;
            if (move && paths.All(p => PathUtil.Same(Path.GetDirectoryName(p), target))) return DragDropEffects.None;
            return move ? DragDropEffects.Move : DragDropEffects.Copy;
        }

        string ListDropTarget(DragEventArgs e)
        {
            var pt = list.PointToClient(new Point(e.X, e.Y));
            var it = list.GetItemAt(pt.X, pt.Y);
            if (it?.Tag is Entry en && en.IsDir && en.Trash == null && guard.DeviceAllowed(en.Path)) return en.Path;
            if (it?.Tag is Loc l && l.Kind == LocKind.Device) return l.Path;
            return current.Kind == LocKind.Device ? current.Path : null;
        }

        void ListDragOver(object sender, DragEventArgs e) => e.Effect = DecideEffect(DragPaths(e), ListDropTarget(e), e.KeyState);

        void ListDragDrop(object sender, DragEventArgs e)
        {
            var paths = DragPaths(e); var target = ListDropTarget(e);
            var eff = DecideEffect(paths, target, e.KeyState);
            if (eff == DragDropEffects.None) return;
            BeginInvoke((Action)(() => RunCopy(paths.ToList(), target, eff == DragDropEffects.Move)));
        }

        string TreeDropTarget(DragEventArgs e)
        {
            var pt = tree.PointToClient(new Point(e.X, e.Y));
            var n = tree.GetNodeAt(pt);
            return n?.Tag is Loc l && l.Kind == LocKind.Device ? l.Path : null;
        }

        void TreeDragOver(object sender, DragEventArgs e) => e.Effect = DecideEffect(DragPaths(e), TreeDropTarget(e), e.KeyState);

        void TreeDragDrop(object sender, DragEventArgs e)
        {
            var paths = DragPaths(e); var target = TreeDropTarget(e);
            var eff = DecideEffect(paths, target, e.KeyState);
            if (eff == DragDropEffects.None) return;
            BeginInvoke((Action)(() => RunCopy(paths.ToList(), target, eff == DragDropEffects.Move)));
        }

        // ============================================================ context menu
        void ListMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            listMenu.Items.Clear();
            var sel = SelectedEntries();
            var eff = current.Effective;
            bool dev = eff == LocKind.Device && current.Kind != LocKind.Trash;
            ToolStripMenuItem Add(string text, char glyph, Action a, bool enabled = true, string keys = null)
            {
                var mi = new ToolStripMenuItem(text, Glyphs.Get(glyph, Color.FromArgb(40, 40, 40)), (s, x) => a()) { Enabled = enabled, ShortcutKeyDisplayString = keys };
                listMenu.Items.Add(mi);
                return mi;
            }

            if (current.Kind == LocKind.Home)
            {
                if (list.SelectedItems.Count == 1 && list.SelectedItems[0].Tag is Loc l)
                {
                    Add("פתיחה", Glyphs.Folder, () => Navigate(l));
                    if (l.Kind == LocKind.Device && devices.Any(d => PathUtil.Same(d.Root, l.Path))) Add("הוצאה בטוחה", Glyphs.Usb, () => Eject(l.Path));
                }
                else e.Cancel = true;
                return;
            }
            if (current.Kind == LocKind.Trash)
            {
                if (sel.Count > 0)
                {
                    Add("שחזור", Glyphs.Restore, RestoreSelected);
                    Add("מחיקה לצמיתות", Glyphs.Delete, EmptyTrash);
                }
                else Add("ריקון הסל", Glyphs.Delete, EmptyTrash, entries.Count > 0);
                return;
            }
            if (sel.Count > 0)
            {
                if (sel.Count == 1)
                {
                    var one = sel[0];
                    bool ext = !one.IsDir && guard.CanOpen(one.Ext);
                    Add(one.IsDir || ext ? "פתיחה" : "תצוגה מקדימה", one.IsDir ? Glyphs.Folder : ext ? Glyphs.Forward : Glyphs.Preview, OpenSelected).Font = new Font(listMenu.Font, FontStyle.Bold);
                    if (ext) Add("תצוגה מקדימה", Glyphs.Preview, () => ShowPreview(one));
                }
                var send = Add("העתקה להתקן…", Glyphs.Send, SendToDevice, OtherDevices().Count > 0);
                listMenu.Items.Add(new ToolStripSeparator());
                if (dev) Add("גזירה", Glyphs.Cut, () => ToClip(true), true, "Ctrl+X");
                Add("העתקה", Glyphs.Copy, () => ToClip(false), true, "Ctrl+C");
                if (dev)
                {
                    listMenu.Items.Add(new ToolStripSeparator());
                    Add("שינוי שם", Glyphs.Rename, Rename, sel.Count == 1, "F2");
                    if (cfg.AllowDeviceDelete)
                    {
                        Add("מחיקה", Glyphs.Delete, () => Delete(false), true, "Del");
                        Add("מחיקה לצמיתות", Glyphs.Delete, () => Delete(true), true, "Shift+Del");
                    }
                }
            }
            else
            {
                var v = new ToolStripMenuItem("תצוגה", Glyphs.Get(Glyphs.View, Color.FromArgb(40, 40, 40)));
                FillViewMenu(v.DropDownItems);
                var so = new ToolStripMenuItem("מיון לפי", Glyphs.Get(Glyphs.Sort, Color.FromArgb(40, 40, 40)));
                FillSortMenu(so.DropDownItems);
                listMenu.Items.Add(v); listMenu.Items.Add(so);
                Add("רענון", Glyphs.Refresh, RefreshView, true, "F5");
                listMenu.Items.Add(new ToolStripSeparator());
                if (current.Kind == LocKind.Device)
                {
                    Add("הדבקה", Glyphs.Paste, Paste, clip.Count > 0, "Ctrl+V");
                    var nw = new ToolStripMenuItem("חדש", Glyphs.Get(Glyphs.Add, Color.FromArgb(40, 40, 40)));
                    FillNewMenu(nw.DropDownItems);
                    listMenu.Items.Add(nw);
                }
                Add("בחירת הכל", Glyphs.SelectAll, SelectAll, entries.Count > 0, "Ctrl+A");
            }
        }

        // ============================================================ keyboard
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (editingLabel) return base.ProcessCmdKey(ref msg, keyData);
            bool inText = ActiveControl is TextBox || ActiveControl is RichTextBox || (ActiveControl is ContainerControl cc && cc.ActiveControl is TextBoxBase);
            switch (keyData)
            {
                case Keys.Alt | Keys.Left: GoBack(); return true;
                case Keys.Alt | Keys.Right: GoForward(); return true;
                case Keys.Alt | Keys.Up: GoUp(); return true;
                case Keys.BrowserBack: GoBack(); return true;
                case Keys.BrowserForward: GoForward(); return true;
                case Keys.F5: RefreshView(); return true;
                case Keys.Control | Keys.F:
                case Keys.F3: search.Focus(); search.SelectAll(); return true;
                case Keys.Alt | Keys.Enter: SetPane(cfg.Pane == "Details" ? "None" : "Details"); return true;
                case Keys.Control | Keys.Shift | Keys.N: if (bNew.Enabled) NewFolder(); return true;
            }
            if (!inText)
            {
                switch (keyData)
                {
                    case Keys.Control | Keys.A: SelectAll(); return true;
                    case Keys.Control | Keys.C: if (bCopy.Enabled) ToClip(false); return true;
                    case Keys.Control | Keys.X: if (bCut.Enabled) ToClip(true); return true;
                    case Keys.Control | Keys.V: if (bPaste.Enabled) Paste(); return true;
                    case Keys.F2: if (bRename.Enabled) Rename(); return true;
                    case Keys.Delete: if (current.Kind == LocKind.Trash) EmptyTrash(); else if (bDelete.Enabled) Delete(false); return true;
                    case Keys.Shift | Keys.Delete: if (bDelete.Enabled) Delete(true); return true;
                    case Keys.Escape: if (current.Kind == LocKind.Search) { CancelSearch(); return true; } break;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ============================================================ settings / exit
        void OpenSettings()
        {
            if (!PasswordForm.Authenticate(this, cfg)) return;
            pane.ReleaseFiles();
            using (var f = new SettingsForm(cfg))
            {
                var r = f.ShowDialog(this);
                if (f.ExitRequested) { allowClose = true; Close(); return; }
                if (r != DialogResult.OK) return;
            }
            ApplyWindowMode();
            devices = DeviceScanner.Scan(cfg);
            back.Clear(); fwd.Clear();
            BuildTree();
            Navigate(Loc.Home(), false);
            Status("ההגדרות נשמרו.");
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!allowClose && cfg.PasswordToExit && e.CloseReason == CloseReason.UserClosing)
            {
                if (!PasswordForm.Authenticate(this, cfg)) { e.Cancel = true; return; }
            }
            try { pane.ReleaseFiles(); } catch { }
            thumbQueue.CompleteAdding();
            SavePrefs();
        }

        void Msg(string text, MessageBoxIcon icon) =>
            MessageBox.Show(this, text, Text, MessageBoxButtons.OK, icon, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
    }
}

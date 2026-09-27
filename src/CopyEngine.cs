using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace KosherExplorer
{
    enum Conflict { Replace, Skip, KeepBoth, Cancel }

    class CopyJob
    {
        public List<string> Sources = new List<string>();
        public string DestDir;
        public bool Move;
        public Func<FileSystemInfo, bool> Filter;   // what may be copied (hidden/excluded items never leave)
        public string DestTitle;
    }

    class ProgressForm : Form
    {
        readonly CopyJob job;
        readonly Label lblTitle = new Label(), lblFile = new Label(), lblStats = new Label();
        readonly ProgressBar bar = new ProgressBar { Maximum = 1000 };
        readonly Button btnCancel = new Button { Text = "ביטול" };
        volatile bool cancel;
        public int Done, Skipped;
        public List<string> Errors = new List<string>();
        public bool Cancelled => cancel;

        long totalBytes, doneBytes;
        int totalFiles;
        readonly Stopwatch sw = new Stopwatch();
        Conflict? rememberedConflict;

        public ProgressForm(CopyJob job)
        {
            this.job = job;
            Text = job.Move ? "העברה" : "העתקה";
            RightToLeft = RightToLeft.Yes; RightToLeftLayout = true;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(520, 170);
            lblTitle.SetBounds(16, 14, 488, 24); lblTitle.Font = new Font(Font.FontFamily, 11f);
            lblFile.SetBounds(16, 44, 488, 22); lblFile.AutoEllipsis = true;
            bar.SetBounds(16, 72, 488, 22);
            lblStats.SetBounds(16, 100, 488, 22); lblStats.ForeColor = Color.DimGray;
            btnCancel.SetBounds(404, 130, 100, 30);
            btnCancel.Click += (s, e) => { cancel = true; btnCancel.Enabled = false; lblStats.Text = "מבטל..."; };
            Controls.AddRange(new Control[] { lblTitle, lblFile, bar, lblStats, btnCancel });
            lblTitle.Text = (job.Move ? "מעביר אל " : "מעתיק אל ") + job.DestTitle;
            lblFile.Text = "מכין רשימת קבצים...";
            FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing && !finished) { e.Cancel = true; cancel = true; } };
            Shown += (s, e) => new Thread(Run) { IsBackground = true }.Start();
        }

        bool finished;

        class Item { public string Src, Dst; public bool Dir; public long Size; }

        void Run()
        {
            try { Work(); }
            catch (Exception ex) { lock (Errors) Errors.Add(ex.Message); }
            finally
            {
                finished = true;
                try { BeginInvoke((Action)(() => { DialogResult = DialogResult.OK; Close(); })); } catch { }
            }
        }

        void Work()
        {
            string dest = PathUtil.Norm(job.DestDir);
            // --- build plan ---
            var tops = new List<(string src, string dst)>();
            foreach (var s in job.Sources)
            {
                string src = PathUtil.Norm(s);
                if (Directory.Exists(src) && PathUtil.IsUnder(dest, src))
                {
                    Errors.Add("לא ניתן להעתיק את התיקיה \"" + Path.GetFileName(src) + "\" לתוך עצמה.");
                    continue;
                }
                if (job.Move && PathUtil.Same(Path.GetDirectoryName(src), dest)) { Skipped++; continue; }  // moving onto itself
                tops.Add((src, Path.Combine(dest, Path.GetFileName(src))));
            }

            // fast path: move inside the same volume where the target name is free
            if (job.Move)
            {
                foreach (var t in tops.ToList())
                {
                    if (!SameVolume(t.src, dest) || File.Exists(t.dst) || Directory.Exists(t.dst)) continue;
                    try
                    {
                        if (Directory.Exists(t.src)) Directory.Move(t.src, t.dst); else File.Move(t.src, t.dst);
                        Done++; tops.Remove(t);
                    }
                    catch { }   // fall back to copy+delete below
                }
                if (tops.Count == 0) return;
            }

            var items = new List<Item>();
            foreach (var t in tops) Collect(t.src, t.dst, items);
            if (cancel) return;
            totalFiles = items.Count(i => !i.Dir);
            totalBytes = items.Sum(i => i.Dir ? 0 : i.Size);

            // --- capacity checks ---
            try
            {
                var di = new DriveInfo(Path.GetPathRoot(dest));
                bool sameVol = job.Move && tops.All(t => SameVolume(t.src, dest));
                if (!sameVol && totalBytes > di.AvailableFreeSpace)
                {
                    Errors.Add("אין מספיק מקום פנוי ביעד. נדרש " + PathUtil.Size(totalBytes) + ", פנוי " + PathUtil.Size(di.AvailableFreeSpace) + ".");
                    return;
                }
                if (di.DriveFormat.StartsWith("FAT", StringComparison.OrdinalIgnoreCase))
                {
                    var huge = items.Where(i => !i.Dir && i.Size >= 4L * 1024 * 1024 * 1024).ToList();
                    foreach (var h in huge) { Errors.Add("\"" + Path.GetFileName(h.Src) + "\" גדול מ-4GB ולא נכנס להתקן בפורמט FAT32."); items.Remove(h); }
                }
            }
            catch { }

            sw.Start();
            var copiedTops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int fileNo = 0;
            foreach (var it in items)
            {
                if (cancel) return;
                try
                {
                    if (it.Dir) { Directory.CreateDirectory(it.Dst); continue; }
                    fileNo++;
                    UI(() => lblFile.Text = fileNo + " מתוך " + totalFiles + ":  " + Path.GetFileName(it.Src));
                    string target = it.Dst;
                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        var c = rememberedConflict ?? Ask(it.Src, target);
                        if (c == Conflict.Cancel) { cancel = true; return; }
                        if (c == Conflict.Skip) { Skipped++; doneBytes += it.Size; continue; }
                        if (c == Conflict.KeepBoth) target = PathUtil.UniqueName(Path.GetDirectoryName(target), Path.GetFileName(target));
                        else if (Directory.Exists(target)) { Errors.Add("\"" + Path.GetFileName(target) + "\" קיים כתיקיה."); continue; }
                        else File.SetAttributes(target, FileAttributes.Normal);
                    }
                    if (CopyFile(it.Src, target)) { Done++; copied.Add(it.Src); }
                }
                catch (Exception ex)
                {
                    lock (Errors) Errors.Add(Path.GetFileName(it.Src) + ": " + ex.Message);
                }
            }

            if (job.Move && !cancel)
            {
                // remove only what was actually copied; folders go only if they end up empty
                foreach (var f in copied)
                    try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
                foreach (var it in Enumerable.Reverse(items).Where(i => i.Dir))
                    try { if (!Directory.EnumerateFileSystemEntries(it.Src).Any()) Directory.Delete(it.Src); } catch { }
            }
        }

        readonly List<string> copied = new List<string>();

        void Collect(string src, string dst, List<Item> items)
        {
            if (cancel) return;
            if (Directory.Exists(src))
            {
                items.Add(new Item { Src = src, Dst = dst, Dir = true });
                IEnumerable<FileSystemInfo> kids;
                try { kids = new DirectoryInfo(src).EnumerateFileSystemInfos().ToList(); }
                catch (Exception ex) { Errors.Add(Path.GetFileName(src) + ": " + ex.Message); return; }
                foreach (var k in kids)
                {
                    if (job.Filter != null && !job.Filter(k)) continue;
                    Collect(k.FullName, Path.Combine(dst, k.Name), items);
                }
            }
            else if (File.Exists(src))
            {
                items.Add(new Item { Src = src, Dst = dst, Size = new FileInfo(src).Length });
            }
        }

        bool CopyFile(string src, string dst)
        {
            string tmp = dst;
            bool ok = false;
            var buf = new byte[1 << 20];
            try
            {
                using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan))
                using (var o = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    int n;
                    long last = 0;
                    while ((n = i.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (cancel) return false;
                        o.Write(buf, 0, n);
                        doneBytes += n;
                        if (sw.ElapsedMilliseconds - last > 120) { last = sw.ElapsedMilliseconds; Report(); }
                    }
                    o.Flush(true);
                }
                var fi = new FileInfo(src);
                File.SetLastWriteTime(tmp, fi.LastWriteTime);
                ok = true;
                Report();
                return true;
            }
            finally
            {
                if (!ok) try { File.Delete(tmp); } catch { }
            }
        }

        void Report()
        {
            long tb = totalBytes, db = doneBytes;
            double secs = sw.Elapsed.TotalSeconds;
            UI(() =>
            {
                bar.Value = tb > 0 ? (int)Math.Min(1000, db * 1000 / tb) : 0;
                if (secs > 0.5 && db > 0)
                {
                    double rate = db / secs;
                    double left = (tb - db) / Math.Max(rate, 1);
                    lblStats.Text = PathUtil.Size(db) + " מתוך " + PathUtil.Size(tb) + "   ·   " + PathUtil.Size((long)rate) + "/שנ'   ·   " + Remaining(left);
                }
            });
        }

        static string Remaining(double s)
        {
            if (s < 60) return "נותרו כמה שניות";
            if (s < 3600) return "נותרו כ-" + Math.Ceiling(s / 60) + " דקות";
            return "נותרו כ-" + (s / 3600).ToString("0.#") + " שעות";
        }

        Conflict Ask(string src, string dst)
        {
            Conflict res = Conflict.Skip;
            Invoke((Action)(() =>
            {
                using (var f = new ConflictForm(src, dst))
                {
                    f.ShowDialog(this);
                    res = f.Choice;
                    if (f.ForAll && res != Conflict.Cancel) rememberedConflict = res;
                }
            }));
            return res;
        }

        void UI(Action a) { try { BeginInvoke(a); } catch { } }

        static bool SameVolume(string a, string b) =>
            string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);
    }

    class ConflictForm : Form
    {
        public Conflict Choice = Conflict.Skip;
        public bool ForAll => chk.Checked;
        readonly CheckBox chk = new CheckBox { Text = "בצע זאת לכל ההתנגשויות", AutoSize = true };

        public ConflictForm(string src, string dst)
        {
            Text = "הקובץ כבר קיים";
            RightToLeft = RightToLeft.Yes; RightToLeftLayout = true;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(500, 230);
            var s = new FileInfo(src); var d = new FileInfo(dst);
            var lbl = new Label
            {
                Text = "ביעד כבר קיים קובץ בשם \"" + Path.GetFileName(dst) + "\".\n\n" +
                       "הקובץ החדש:   " + PathUtil.Size(s.Length) + ",  " + s.LastWriteTime.ToString("dd/MM/yyyy HH:mm") + "\n" +
                       "הקובץ הקיים:   " + (d.Exists ? PathUtil.Size(d.Length) + ",  " + d.LastWriteTime.ToString("dd/MM/yyyy HH:mm") : "תיקיה"),
                AutoEllipsis = true
            };
            lbl.SetBounds(16, 12, 468, 100);
            chk.Location = new Point(16, 120);
            var b1 = Btn("החלף", Conflict.Replace, 16);
            var b2 = Btn("דלג", Conflict.Skip, 136);
            var b3 = Btn("שמור את שניהם", Conflict.KeepBoth, 256);
            var b4 = Btn("ביטול", Conflict.Cancel, 376);
            Controls.AddRange(new Control[] { lbl, chk, b1, b2, b3, b4 });
            CancelButton = b4;
        }

        Button Btn(string text, Conflict c, int x)
        {
            var b = new Button { Text = text };
            b.SetBounds(x, 180, 110, 32);
            b.Click += (s, e) => { Choice = c; Close(); };
            return b;
        }
    }
}

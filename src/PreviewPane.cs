using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace KosherExplorer
{
    class PreviewPane : Panel
    {
        public static readonly HashSet<string> ImageExt = new HashSet<string> { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico", ".tif", ".tiff" };
        public static readonly HashSet<string> AudioExt = new HashSet<string> { ".mp3", ".wma", ".wav", ".m4a", ".aac", ".flac", ".mid", ".midi", ".amr", ".3gp" };
        public static readonly HashSet<string> VideoExt = new HashSet<string> { ".mp4", ".avi", ".wmv", ".mkv", ".mov", ".mpg", ".mpeg", ".webm", ".m4v", ".flv" };
        public static readonly HashSet<string> TextExt = new HashSet<string> { ".txt", ".log", ".ini", ".csv", ".json", ".xml", ".md", ".srt", ".lrc", ".cue", ".m3u", ".m3u8", ".nfo", ".htm", ".html", ".css", ".js", ".cs", ".py", ".bat" };

        public bool DetailsOnly;
        readonly Label title = new Label { Dock = DockStyle.Top, Height = 54, Font = new Font("Segoe UI Semibold", 11.5f), AutoEllipsis = true, Padding = new Padding(10, 10, 10, 0) };
        readonly PictureBox pic = new PictureBox { Dock = DockStyle.Top, SizeMode = PictureBoxSizeMode.Zoom, Height = 220, BackColor = Color.Transparent };
        readonly RichTextBox text = new RichTextBox { Dock = DockStyle.Top, Height = 260, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(250, 250, 250), DetectUrls = false, WordWrap = true };
        readonly Panel player = new Panel { Dock = DockStyle.Top, Height = 78 };
        readonly Button btnPlay = new Button { FlatStyle = FlatStyle.Flat, Text = "" };
        readonly Button btnStop = new Button { FlatStyle = FlatStyle.Flat, Text = "" };
        readonly TrackBar seek = new TrackBar { TickStyle = TickStyle.None, Maximum = 1000, AutoSize = false, Height = 28 };
        readonly Label time = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray };
        readonly TableLayoutPanel props = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(10, 6, 10, 10) };
        readonly Panel scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        readonly Timer tick = new Timer { Interval = 300 };

        dynamic wmp;
        bool wmpFailed, seeking;
        string currentAudio;
        public Func<string, string> TypeOf = e => e;

        public PreviewPane()
        {
            BackColor = Color.White;
            Padding = new Padding(0);
            props.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            props.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            btnPlay.FlatAppearance.BorderSize = 0; btnStop.FlatAppearance.BorderSize = 0;
            btnPlay.Image = Glyphs.Get(Glyphs.Play, Color.FromArgb(0, 120, 90), 20) ; btnStop.Image = Glyphs.Get(Glyphs.Stop, Color.DimGray, 18);
            if (btnPlay.Image == null) { btnPlay.Text = "▶"; btnStop.Text = "■"; }
            btnPlay.SetBounds(10, 4, 40, 36); btnStop.SetBounds(52, 4, 40, 36);
            seek.SetBounds(96, 8, 200, 28); seek.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            time.SetBounds(12, 44, 280, 22); time.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            player.Controls.AddRange(new Control[] { btnPlay, btnStop, seek, time });
            player.Resize += (s, e) => seek.Width = Math.Max(40, player.Width - 110);
            btnPlay.Click += (s, e) => TogglePlay();
            btnStop.Click += (s, e) => StopAudio();
            seek.MouseDown += (s, e) => seeking = true;
            seek.MouseUp += (s, e) =>
            {
                seeking = false;
                try { double d = wmp.currentMedia.duration; wmp.controls.currentPosition = d * seek.Value / 1000.0; } catch { }
            };
            tick.Tick += (s, e) => UpdateTime();

            scroller.Controls.Add(props);
            scroller.Controls.Add(text);
            scroller.Controls.Add(player);
            scroller.Controls.Add(pic);
            Controls.Add(scroller);
            Controls.Add(title);
            var sep = new Panel { Dock = DockStyle.Right, Width = 1, BackColor = Color.FromArgb(225, 225, 225) };
            Controls.Add(sep);
            Clear();
        }

        public void Clear(string message = null)
        {
            StopAudio(true);
            title.Text = message ?? "";
            SetImage(null);
            pic.Visible = text.Visible = player.Visible = false;
            props.Controls.Clear(); props.RowStyles.Clear(); props.RowCount = 0;
            if (message == null) { title.Text = ""; AddProp("", "בחרו פריט כדי לראות תצוגה מקדימה."); }
        }

        void SetImage(Image img)
        {
            var old = pic.Image; pic.Image = img; old?.Dispose();
        }

        void AddProp(string k, string v)
        {
            if (string.IsNullOrEmpty(v)) return;
            var lk = new Label { Text = k, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 4, 8, 4) };
            var lv = new Label { Text = v, AutoSize = true, MaximumSize = new Size(Math.Max(120, Width - 130), 0), Margin = new Padding(0, 4, 0, 4) };
            props.RowCount++;
            props.Controls.Add(lk, 0, props.RowCount - 1);
            props.Controls.Add(lv, 1, props.RowCount - 1);
        }

        public void ShowSummary(string heading, IEnumerable<(string, string)> rows, Image icon = null)
        {
            Clear(heading);
            if (icon != null) { pic.Height = 110; SetImage(new Bitmap(icon)); pic.Visible = true; }
            foreach (var r in rows) AddProp(r.Item1, r.Item2);
        }

        public void ShowEntry(Entry e, bool autoplay = false)
        {
            Clear(e.Name);
            int w = Math.Max(120, Width - 20);
            pic.Height = Math.Min(260, (int)(w * 0.8));

            if (!DetailsOnly && !e.IsDir && ImageExt.Contains(e.Ext) && e.Size < 40 * 1024 * 1024)
            {
                try { using (var ms = new MemoryStream(File.ReadAllBytes(e.Path))) SetImage(new Bitmap(Image.FromStream(ms))); pic.Visible = true; } catch { }
            }
            if (pic.Image == null)
            {
                var img = Native.ShellImage(e.Path, 256) ?? Native.ExtIcon(e.IsDir ? "dir" : e.Ext, false);
                if (img != null) { SetImage(img); pic.Visible = true; if (DetailsOnly || (!VideoExt.Contains(e.Ext) && !ImageExt.Contains(e.Ext))) pic.Height = 130; }
            }
            if (!DetailsOnly && !e.IsDir && TextExt.Contains(e.Ext))
            {
                text.Text = ReadText(e.Path);
                text.RightToLeft = HasHebrew(text.Text) ? RightToLeft.Yes : RightToLeft.No;
                text.Visible = true;
            }
            if (!DetailsOnly && !e.IsDir && AudioExt.Contains(e.Ext) && EnsurePlayer())
            {
                currentAudio = e.Path;
                player.Visible = true;
                time.Text = "";
                seek.Value = 0;
                SetPlayIcon(false);
                if (autoplay) TogglePlay();
            }

            AddProp("סוג:", TypeOf(e.IsDir ? "dir" : e.Ext));
            if (e.IsDir)
            {
                try
                {
                    var di = new DirectoryInfo(e.Path);
                    int files = 0, dirs = 0;
                    foreach (var x in di.EnumerateFileSystemInfos())
                    {
                        if ((x.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        if (x is DirectoryInfo) dirs++; else files++;
                    }
                    AddProp("תוכן:", files + " קבצים, " + dirs + " תיקיות");
                }
                catch { }
            }
            else AddProp("גודל:", PathUtil.Size(e.Size));
            if (e.Trash != null)
            {
                AddProp("מיקום מקורי:", e.Location);
                AddProp("נמחק:", e.Trash.Deleted.ToString("dd/MM/yyyy HH:mm"));
            }
            else if (e.Location != null) AddProp("מיקום:", e.Location);
            AddProp("תאריך שינוי:", e.Modified.ToString("dd/MM/yyyy HH:mm"));
            AddProp("תאריך יצירה:", e.Created.ToString("dd/MM/yyyy HH:mm"));
            if (!e.IsDir && (AudioExt.Contains(e.Ext) || VideoExt.Contains(e.Ext) || ImageExt.Contains(e.Ext))) MediaProps(e);
        }

        void MediaProps(Entry e)
        {
            try
            {
                var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                dynamic sh = shell;
                dynamic folder = sh.NameSpace(Path.GetDirectoryName(e.Path));
                dynamic item = folder?.ParseName(e.Name);
                if (item == null) return;
                string P(string name) { try { var v = item.ExtendedProperty(name); return v == null ? null : Convert.ToString(v); } catch { return null; } }
                string dur = null;
                try { var d = item.ExtendedProperty("System.Media.Duration"); if (d != null) dur = TimeSpan.FromTicks((long)Convert.ToUInt64(d)).ToString(@"h\:mm\:ss").TrimStart('0', ':'); } catch { }
                AddProp("משך:", dur);
                AddProp("כותרת:", P("System.Title"));
                AddProp("אמן:", P("System.Music.Artist") ?? P("System.Music.AlbumArtist"));
                AddProp("אלבום:", P("System.Music.AlbumTitle"));
                string br = null;
                try { var b = item.ExtendedProperty("System.Audio.EncodingBitrate"); if (b != null) br = (Convert.ToInt64(b) / 1000) + " kbps"; } catch { }
                AddProp("קצב סיביות:", br);
                string dims = P("System.Image.Dimensions");
                if (dims == null && P("System.Video.FrameWidth") is string fw && P("System.Video.FrameHeight") is string fh) dims = fw + " x " + fh;
                AddProp("ממדים:", dims);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
            catch { }
        }

        static string ReadText(string path)
        {
            try
            {
                byte[] b;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    b = new byte[Math.Min(fs.Length, 64 * 1024)];
                    int n = 0; while (n < b.Length) { int r = fs.Read(b, n, b.Length - n); if (r <= 0) break; n += r; }
                }
                if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
                if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
                int start = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
                try { return new UTF8Encoding(false, true).GetString(b, start, b.Length - start); }
                catch (DecoderFallbackException)
                {
                    // file may have been cut mid-character at 64KB; retry trimmed, else fall back to Hebrew ANSI
                    try { return new UTF8Encoding(false, true).GetString(b, start, Math.Max(0, b.Length - start - 3)); }
                    catch { return Encoding.GetEncoding(1255).GetString(b); }
                }
            }
            catch (Exception ex) { return "לא ניתן להציג: " + ex.Message; }
        }

        static bool HasHebrew(string s) => s.Take(2000).Any(c => c >= '֐' && c <= '׿');

        // ---------- audio (Windows Media Player, headless) ----------
        bool EnsurePlayer()
        {
            if (wmp != null) return true;
            if (wmpFailed) return false;
            try
            {
                var t = Type.GetTypeFromProgID("WMPlayer.OCX");
                if (t == null) { wmpFailed = true; return false; }
                wmp = Activator.CreateInstance(t);
                wmp.settings.autoStart = false;
                wmp.settings.volume = 100;
                return true;
            }
            catch { wmpFailed = true; return false; }
        }

        void TogglePlay()
        {
            if (wmp == null || currentAudio == null) return;
            try
            {
                int st = wmp.playState;   // 3 playing, 2 paused
                if (st == 3) { wmp.controls.pause(); SetPlayIcon(false); return; }
                if (st != 2 || !string.Equals((string)wmp.URL, currentAudio, StringComparison.OrdinalIgnoreCase))
                    wmp.URL = currentAudio;
                wmp.controls.play();
                SetPlayIcon(true);
                tick.Start();
            }
            catch { }
        }

        void SetPlayIcon(bool playing)
        {
            var g = Glyphs.Get(playing ? Glyphs.Pause : Glyphs.Play, Color.FromArgb(0, 120, 90), 20);
            if (g != null) btnPlay.Image = g; else btnPlay.Text = playing ? "❚❚" : "▶";
        }

        public void StopAudio(bool release = false)
        {
            tick.Stop();
            try { if (wmp != null) { wmp.controls.stop(); if (release) wmp.close(); } } catch { }
            if (release) currentAudio = null;
            SetPlayIcon(false);
            seek.Value = 0;
            time.Text = "";
        }

        /// <summary>Releases any file handle that could block ejecting a device.</summary>
        public void ReleaseFiles()
        {
            StopAudio(true);
            SetImage(null);
        }

        void UpdateTime()
        {
            try
            {
                double pos = wmp.controls.currentPosition;
                double dur = wmp.currentMedia != null ? (double)wmp.currentMedia.duration : 0;
                int st = wmp.playState;
                if (st == 1 || st == 8) { SetPlayIcon(false); tick.Stop(); seek.Value = 0; time.Text = ""; return; }
                if (!seeking && dur > 0) seek.Value = (int)Math.Max(0, Math.Min(1000, pos * 1000 / dur));
                time.Text = Fmt(pos) + " / " + Fmt(dur);
            }
            catch { }
        }

        static string Fmt(double s)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, s));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        public bool IsPlayingFrom(string root) => currentAudio != null && PathUtil.IsUnder(currentAudio, root);

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { StopAudio(true); } catch { } }
            base.Dispose(disposing);
        }
    }
}

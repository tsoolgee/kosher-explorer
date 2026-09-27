using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KosherExplorer
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO sfi, uint cb, uint flags);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] public static extern int StrCmpLogicalW(string a, string b);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref HDITEM l);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string idList);

        const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_LARGEICON = 0x0,
                   SHGFI_TYPENAME = 0x400, SHGFI_USEFILEATTRIBUTES = 0x10;
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

        static readonly Dictionary<string, string> typeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>System icon by extension only (no disk access). key: ".mp3" / "dir".</summary>
        public static Bitmap ExtIcon(string ext, bool small)
        {
            bool dir = ext == "dir";
            var sfi = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | (small ? SHGFI_SMALLICON : SHGFI_LARGEICON);
            SHGetFileInfo(dir ? "folder" : "file" + ext, dir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
                ref sfi, (uint)Marshal.SizeOf(sfi), flags);
            return TakeIcon(sfi.hIcon);
        }

        /// <summary>Real icon of an existing path (used for drives).</summary>
        public static Bitmap PathIcon(string path, bool small)
        {
            var sfi = new SHFILEINFO();
            SHGetFileInfo(path, 0, ref sfi, (uint)Marshal.SizeOf(sfi), SHGFI_ICON | (small ? SHGFI_SMALLICON : SHGFI_LARGEICON));
            return TakeIcon(sfi.hIcon);
        }

        static Bitmap TakeIcon(IntPtr h)
        {
            if (h == IntPtr.Zero) return null;
            try { using (var ic = Icon.FromHandle(h)) return ic.ToBitmap(); }
            finally { DestroyIcon(h); }
        }

        public static string TypeName(string ext, bool dir)
        {
            if (dir) return "תיקיית קבצים";
            string key = string.IsNullOrEmpty(ext) ? "." : ext;
            lock (typeNames)
            {
                if (typeNames.TryGetValue(key, out var t)) return t;
                var sfi = new SHFILEINFO();
                SHGetFileInfo("file" + ext, FILE_ATTRIBUTE_NORMAL, ref sfi, (uint)Marshal.SizeOf(sfi), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
                t = string.IsNullOrEmpty(sfi.szTypeName) ? (ext.TrimStart('.').ToUpperInvariant() + " קובץ") : sfi.szTypeName;
                typeNames[key] = t;
                return t;
            }
        }

        // ---------- Explorer thumbnails (IShellItemImageFactory) ----------
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        public const int SIIGBF_RESIZETOFIT = 0, SIIGBF_ICONONLY = 4, SIIGBF_THUMBNAILONLY = 8;

        /// <summary>Returns a size×size canvas with the shell image centred, or null. Call from an STA thread.</summary>
        public static Bitmap ShellImage(string path, int size, int flags = SIIGBF_RESIZETOFIT)
        {
            IShellItemImageFactory f = null;
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f);
                if (f.GetImage(new SIZE { cx = size, cy = size }, flags, out var hbm) != 0 || hbm == IntPtr.Zero) return null;
                try
                {
                    using (var raw = FromHBitmap(hbm)) return Fit(raw, size);
                }
                finally { DeleteObject(hbm); }
            }
            catch { return null; }
            finally { if (f != null) Marshal.ReleaseComObject(f); }
        }

        static Bitmap FromHBitmap(IntPtr h)
        {
            using (var bmp = Image.FromHbitmap(h))
            {
                if (Image.GetPixelFormatSize(bmp.PixelFormat) < 32) return new Bitmap(bmp);
                var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, bmp.PixelFormat);
                try
                {
                    var bytes = new byte[data.Stride * data.Height];
                    Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                    bool alpha = false;
                    for (int i = 3; i < bytes.Length; i += 4) if (bytes[i] != 0) { alpha = true; break; }
                    if (!alpha) return new Bitmap(bmp);
                    var res = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppPArgb);
                    var rd = res.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    for (int y = 0; y < bmp.Height; y++)
                        Marshal.Copy(bytes, y * data.Stride, rd.Scan0 + y * rd.Stride, bmp.Width * 4);
                    res.UnlockBits(rd);
                    return res;
                }
                finally { bmp.UnlockBits(data); }
            }
        }

        /// <summary>Scales an image down (never up beyond 1:1 for small icons) into a square transparent canvas.</summary>
        public static Bitmap Fit(Image img, int size, bool allowUpscale = false)
        {
            var canvas = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(canvas))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                double k = Math.Min((double)size / img.Width, (double)size / img.Height);
                if (!allowUpscale) k = Math.Min(k, 1.0);
                int w = Math.Max(1, (int)(img.Width * k)), hh = Math.Max(1, (int)(img.Height * k));
                g.DrawImage(img, (size - w) / 2, (size - hh) / 2, w, hh);
            }
            return canvas;
        }

        // ---------- ListView sort arrows / theme ----------
        [StructLayout(LayoutKind.Sequential)]
        struct HDITEM
        {
            public int mask, cxy; public IntPtr pszText, hbm; public int cchTextMax, fmt; public IntPtr lParam;
            public int iImage, iOrder; public uint type; public IntPtr pvFilter; public uint state;
        }

        public static void SetSortArrow(ListView lv, int column, bool asc)
        {
            if (!lv.IsHandleCreated) return;
            IntPtr header = SendMessage(lv.Handle, 0x101F /*LVM_GETHEADER*/, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < lv.Columns.Count; i++)
            {
                var item = new HDITEM { mask = 4 /*HDI_FORMAT*/ };
                SendMessage(header, 0x120B /*HDM_GETITEMW*/, (IntPtr)i, ref item);
                item.fmt &= ~(0x400 | 0x200);
                if (i == column) item.fmt |= asc ? 0x400 : 0x200;
                SendMessage(header, 0x120C /*HDM_SETITEMW*/, (IntPtr)i, ref item);
            }
        }

        public static void ExplorerTheme(Control c)
        {
            try { SetWindowTheme(c.Handle, "Explorer", null); } catch { }
        }

        public static void SetCue(TextBox tb, string text)
        {
            SendMessage(tb.Handle, 0x1501 /*EM_SETCUEBANNER*/, (IntPtr)1, text);
        }
    }

    /// <summary>Toolbar glyphs drawn from the Windows icon font (Win10/11); falls back to no image.</summary>
    static class Glyphs
    {
        static readonly string font = PickFont();
        static readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();
        public static float Scale = 1f;

        static string PickFont()
        {
            using (var fonts = new InstalledFontCollection())
            {
                var names = fonts.Families.Select(f => f.Name).ToList();
                if (names.Contains("Segoe Fluent Icons")) return "Segoe Fluent Icons";
                if (names.Contains("Segoe MDL2 Assets")) return "Segoe MDL2 Assets";
            }
            return null;
        }

        public const char Back = '', Forward = '', Up = '', Refresh = '', Home = '',
            NewFolder = '', Cut = '', Copy = '', Paste = '', Rename = '', Delete = '',
            View = '', Sort = '', Settings = '', Search = '', Check = '', Preview = '',
            Details = '', SelectAll = '', SelectNone = '', Usb = '', Send = '',
            Play = '', Pause = '', Stop = '', Restore = '', Lock = '', Folder = '',
            Shield = '', Eject = '', Exit = '', Add = '', Remove = '';

        public static Bitmap Get(char c, Color color, int px = 16)
        {
            if (font == null) return null;
            int size = (int)Math.Round(px * Scale);
            string key = c + "|" + color.ToArgb() + "|" + size;
            if (cache.TryGetValue(key, out var b)) return b;
            b = new Bitmap(size + 4, size + 4, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            using (var f = new Font(font, size * 0.75f, GraphicsUnit.Point))
            using (var br = new SolidBrush(color))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(c.ToString(), f, br, new RectangleF(0, 0, b.Width, b.Height + 1), fmt);
            }
            cache[key] = b;
            return b;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace KosherExplorer
{
    public class RootDef
    {
        public string Path { get; set; }
        public string Title { get; set; }
        public List<string> Exclusions { get; set; } = new List<string>();
    }

    /// <summary>A drive the admin marked as part of the computer; identified by volume serial so a letter change doesn't matter.</summary>
    public class InternalDrive
    {
        public string Serial { get; set; }
        public string Name { get; set; }
    }

    public class AppConfig
    {
        public List<RootDef> Roots { get; set; } = new List<RootDef>();
        public string PwdHash { get; set; }
        public string PwdSalt { get; set; }
        public int PwdIter { get; set; }
        public string WindowTitle { get; set; } = "סייר כשר";
        public bool AllowDeviceDelete { get; set; } = true;
        public bool Kiosk { get; set; }
        public bool PasswordToExit { get; set; }
        public bool AllDriveTypes { get; set; }   // also list non-USB secondary drives as devices (off = USB/removable only)
        public List<InternalDrive> InternalDrives { get; set; } = new List<InternalDrive>();   // never shown as devices
        // per-station view preferences
        public string ViewMode { get; set; } = "Details";
        public bool CheckBoxes { get; set; } = true;
        public string Pane { get; set; } = "Preview";
        public int NavWidth { get; set; } = 250;
        public int PaneWidth { get; set; } = 300;
    }

    static class ConfigStore
    {
        public static string FilePath
        {
            get
            {
                string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                if (File.Exists(beside)) return beside;   // portable mode
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KosherExplorer", "config.json");
            }
        }

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var c = new JavaScriptSerializer().Deserialize<AppConfig>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (c != null)
                    {
                        c.Roots = c.Roots ?? new List<RootDef>();
                        c.InternalDrives = c.InternalDrives ?? new List<InternalDrive>();
                        foreach (var r in c.Roots) r.Exclusions = r.Exclusions ?? new List<string>();
                        return c;
                    }
                }
            }
            catch { }
            return new AppConfig();
        }

        public static void Save(AppConfig c)
        {
            string p = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            string tmp = p + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(c), new UTF8Encoding(false));
            if (File.Exists(p)) File.Replace(tmp, p, null); else File.Move(tmp, p);
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static bool AutoStart
        {
            get { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k?.GetValue("KosherExplorer") != null; }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("KosherExplorer", "\"" + System.Windows.Forms.Application.ExecutablePath + "\"");
                    else k.DeleteValue("KosherExplorer", false);
                }
            }
        }
    }

    static class Pwd
    {
        public static bool IsSet(AppConfig c) => !string.IsNullOrEmpty(c.PwdHash);

        public static void Set(AppConfig c, string pwd)
        {
            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            c.PwdIter = 200000;
            c.PwdSalt = Convert.ToBase64String(salt);
            c.PwdHash = Convert.ToBase64String(Derive(pwd, salt, c.PwdIter));
        }

        public static bool Verify(AppConfig c, string pwd)
        {
            if (!IsSet(c) || pwd == null) return false;
            var want = Convert.FromBase64String(c.PwdHash);
            var got = Derive(pwd, Convert.FromBase64String(c.PwdSalt), c.PwdIter);
            int diff = want.Length ^ got.Length;
            for (int i = 0; i < Math.Min(want.Length, got.Length); i++) diff |= want[i] ^ got[i];
            return diff == 0;
        }

        static byte[] Derive(string pwd, byte[] salt, int iter)
        {
            using (var k = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(pwd), salt, iter, HashAlgorithmName.SHA256))
                return k.GetBytes(32);
        }
    }

    static class PathUtil
    {
        public static string Norm(string p)
        {
            var f = Path.GetFullPath(p);
            if (f.Length > 3) f = f.TrimEnd('\\');
            return f;
        }

        public static bool Same(string a, string b) => string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>child == parent or child is inside parent.</summary>
        public static bool IsUnder(string child, string parent)
        {
            child = Norm(child); parent = Norm(parent);
            if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)) return true;
            string pp = parent.EndsWith("\\") ? parent : parent + "\\";
            return child.StartsWith(pp, StringComparison.OrdinalIgnoreCase);
        }

        public static string Relative(string child, string parent)
        {
            child = Norm(child); parent = Norm(parent);
            if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)) return "";
            string pp = parent.EndsWith("\\") ? parent : parent + "\\";
            return child.Substring(pp.Length);
        }

        static readonly char[] bad = Path.GetInvalidFileNameChars();
        static readonly string[] reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                                              "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        public static string ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "השם לא יכול להיות ריק.";
            if (name.IndexOfAny(bad) >= 0) return "שם קובץ לא יכול להכיל את התווים:  \\ / : * ? \" < > |";
            if (name.Trim('.', ' ').Length == 0) return "שם לא חוקי.";
            if (name.EndsWith(" ") || name.EndsWith(".")) return "השם לא יכול להסתיים ברווח או בנקודה.";
            string stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
            if (reserved.Contains(stem)) return "זה שם שמור של Windows.";
            if (name.Length > 200) return "השם ארוך מדי.";
            return null;
        }

        public static string UniqueName(string dir, string name)
        {
            string candidate = Path.Combine(dir, name);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            bool isDir = Directory.Exists(candidate);
            string stem = isDir ? name : Path.GetFileNameWithoutExtension(name);
            string ext = isDir ? "" : Path.GetExtension(name);
            for (int i = 2; ; i++)
            {
                candidate = Path.Combine(dir, stem + " (" + i + ")" + ext);
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
        }

        public static string Size(long b)
        {
            if (b < 1024) return b + " בתים";
            string[] u = { "KB", "MB", "GB", "TB" };
            double v = b; int i = -1;
            do { v /= 1024; i++; } while (v >= 1024 && i < u.Length - 1);
            return (v >= 100 ? v.ToString("0") : v >= 10 ? v.ToString("0.#") : v.ToString("0.##")) + " " + u[i];
        }
    }

    public class Device
    {
        public string Root;      // "E:\"
        public string Label;
        public string Model;
        public string Format;
        public long Free, Total;
        public string Serial;
        public string Display => (string.IsNullOrWhiteSpace(Label) ? (string.IsNullOrWhiteSpace(Model) ? "התקן נשלף" : Model) : Label)
                                 + " (" + Root.TrimEnd('\\') + ")";
    }

    static class DeviceScanner
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern bool GetVolumeInformation(string root, StringBuilder name, int nameSize, out uint serial, out uint maxComp, out uint flags, StringBuilder fs, int fsSize);

        public static string SerialOf(string root)
        {
            try { return GetVolumeInformation(root, null, 0, out uint s, out _, out _, null, 0) ? s.ToString("X8") : null; }
            catch { return null; }
        }

        /// <summary>includeInternal: also return drives the admin marked as internal (for the settings screen).</summary>
        public static List<Device> Scan(AppConfig cfg, bool includeInternal = false)
        {
            var usb = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);  // "E:" -> model
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT DeviceID, Model FROM Win32_DiskDrive WHERE InterfaceType='USB'"))
                    foreach (ManagementObject disk in s.Get())
                    {
                        string id = (string)disk["DeviceID"], model = (disk["Model"] as string ?? "").Replace(" USB Device", "").Trim();
                        using (var ps = new ManagementObjectSearcher("ASSOCIATORS OF {Win32_DiskDrive.DeviceID='" + id.Replace("\\", "\\\\") + "'} WHERE AssocClass = Win32_DiskDriveToDiskPartition"))
                            foreach (ManagementObject part in ps.Get())
                                using (var ls = new ManagementObjectSearcher("ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + part["DeviceID"] + "'} WHERE AssocClass = Win32_LogicalDiskToPartition"))
                                    foreach (ManagementObject ld in ls.Get()) usb[(string)ld["Name"]] = model;
                    }
            }
            catch { }

            string sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var list = new List<Device>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    string letter = d.Name.TrimEnd('\\');
                    bool isUsb = usb.ContainsKey(letter);
                    bool ok = d.DriveType == DriveType.Removable || isUsb ||
                              (cfg.AllDriveTypes && d.DriveType == DriveType.Fixed);
                    if (!ok || !d.IsReady) continue;
                    if (string.Equals(d.Name, sys, StringComparison.OrdinalIgnoreCase)) continue;
                    // never expose a drive that hosts a public root as a writable device
                    if (cfg.Roots.Any(r => PathUtil.IsUnder(r.Path, d.Name))) continue;
                    string serial = SerialOf(d.Name);
                    if (!includeInternal && serial != null && cfg.InternalDrives.Any(x => x.Serial == serial)) continue;
                    list.Add(new Device
                    {
                        Serial = serial,
                        Root = d.Name,
                        Label = d.VolumeLabel,
                        Model = isUsb ? usb[letter] : "",
                        Format = d.DriveFormat,
                        Free = d.AvailableFreeSpace,
                        Total = d.TotalSize
                    });
                }
                catch { }
            }
            return list;
        }

        public static void Refresh(Device d)
        {
            try { var di = new DriveInfo(d.Root); d.Free = di.AvailableFreeSpace; d.Total = di.TotalSize; } catch { }
        }
    }

    /// <summary>The only gatekeeper deciding which paths may be shown or written.</summary>
    class Guard
    {
        readonly AppConfig cfg;
        public Func<List<Device>> Devices;
        public Guard(AppConfig c) { cfg = c; }

        public RootDef RootOf(string path)
        {
            try
            {
                foreach (var r in cfg.Roots)
                    if (!string.IsNullOrEmpty(r.Path) && PathUtil.IsUnder(path, r.Path)) return r;
            }
            catch { }
            return null;
        }

        public bool IsExcluded(RootDef r, string path) => r.Exclusions.Any(e => PathUtil.IsUnder(path, e));

        /// <summary>Path may be viewed as public content: inside a root, not excluded, no hidden/link hop in between.</summary>
        public bool PublicAllowed(string path)
        {
            var r = RootOf(path);
            if (r == null || IsExcluded(r, path)) return false;
            try
            {
                string root = PathUtil.Norm(r.Path);
                for (string p = PathUtil.Norm(path); !string.Equals(p, root, StringComparison.OrdinalIgnoreCase); p = Path.GetDirectoryName(p))
                {
                    if (p == null) return false;
                    var a = File.GetAttributes(p);
                    if ((a & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) return false;
                }
                return true;
            }
            catch { return false; }
        }

        public Device DeviceOf(string path)
        {
            try { return Devices().FirstOrDefault(d => PathUtil.IsUnder(path, d.Root)); } catch { return null; }
        }

        /// <summary>Path is a writable location on a connected device (outside its trash).</summary>
        public bool DeviceAllowed(string path)
        {
            var d = DeviceOf(path);
            return d != null && !Trash.IsTrashPath(d.Root, path);
        }

        /// <summary>Filter used while listing / copying: skip hidden, system, links and excluded items.</summary>
        public bool ShowEntry(FileSystemInfo fi, RootDef root, string deviceRoot)
        {
            var a = fi.Attributes;
            if ((a & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) return false;
            if (root != null && IsExcluded(root, fi.FullName)) return false;
            if (deviceRoot != null && Trash.IsTrashPath(deviceRoot, fi.FullName)) return false;
            return true;
        }
    }

    public class Entry
    {
        public string Path, Name, Ext;
        public bool IsDir;
        public long Size;
        public DateTime Modified, Created;
        public string Location;           // search results / trash: original location
        public TrashItem Trash;
    }

    static class Lister
    {
        public static List<Entry> List(string dir, Func<FileSystemInfo, bool> filter)
        {
            var res = new List<Entry>();
            var di = new DirectoryInfo(dir);
            using (var e = di.EnumerateFileSystemInfos().GetEnumerator())
            {
                while (true)
                {
                    FileSystemInfo fi;
                    try { if (!e.MoveNext()) break; fi = e.Current; }
                    catch (UnauthorizedAccessException) { break; }
                    catch (IOException) { break; }
                    try
                    {
                        if (!filter(fi)) continue;
                        res.Add(ToEntry(fi));
                    }
                    catch { }
                }
            }
            return res;
        }

        public static Entry ToEntry(FileSystemInfo fi)
        {
            bool dir = fi is DirectoryInfo;
            return new Entry
            {
                Path = fi.FullName,
                Name = fi.Name,
                IsDir = dir,
                Ext = dir ? "" : fi.Extension.ToLowerInvariant(),
                Size = dir ? -1 : ((FileInfo)fi).Length,
                Modified = fi.LastWriteTime,
                Created = fi.CreationTime
            };
        }

        public static void Search(string dir, string term, Func<FileSystemInfo, bool> filter, List<Entry> into, CancellationToken ct, int max = 3000)
        {
            var queue = new Queue<string>();
            queue.Enqueue(dir);
            while (queue.Count > 0 && !ct.IsCancellationRequested && into.Count < max)
            {
                string cur = queue.Dequeue();
                IEnumerable<FileSystemInfo> items;
                try { items = new DirectoryInfo(cur).EnumerateFileSystemInfos().ToList(); } catch { continue; }
                foreach (var fi in items)
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        if (!filter(fi)) continue;
                        if (fi.Name.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0)
                        {
                            var en = ToEntry(fi);
                            en.Location = System.IO.Path.GetDirectoryName(fi.FullName);
                            lock (into) into.Add(en);
                        }
                        if (fi is DirectoryInfo) queue.Enqueue(fi.FullName);
                    }
                    catch { }
                }
            }
        }

        public static long DirSize(string dir)
        {
            long s = 0;
            try
            {
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)) s += f.Length;
            }
            catch { }
            return s;
        }
    }

    public class TrashItem
    {
        public string Id, InfoFile, SlotDir, ItemPath, OriginalRel;
        public DateTime Deleted;
    }

    /// <summary>Per-device recycle bin: hidden folder at the device root; nothing leaves the device.</summary>
    static class Trash
    {
        public const string DirName = ".kosher-trash";

        public static string DirFor(string deviceRoot) => Path.Combine(deviceRoot, DirName);
        public static bool IsTrashPath(string deviceRoot, string p) => PathUtil.IsUnder(p, DirFor(deviceRoot));

        static string Ensure(string deviceRoot)
        {
            string t = DirFor(deviceRoot);
            if (!Directory.Exists(t))
            {
                var di = Directory.CreateDirectory(t);
                di.Attributes |= FileAttributes.Hidden | FileAttributes.System;
            }
            return t;
        }

        public static void Put(string deviceRoot, string path)
        {
            string t = Ensure(deviceRoot);
            string id = DateTime.Now.ToString("yyyyMMddHHmmssfff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string slot = Path.Combine(t, id);
            string info = Path.Combine(t, id + ".info");
            Directory.CreateDirectory(slot);
            File.WriteAllText(info, PathUtil.Relative(path, deviceRoot) + "\n" + DateTime.Now.ToString("o"), new UTF8Encoding(false));
            try
            {
                string dest = Path.Combine(slot, Path.GetFileName(path));
                if (Directory.Exists(path)) Directory.Move(path, dest); else File.Move(path, dest);
            }
            catch
            {
                try { Directory.Delete(slot, true); File.Delete(info); } catch { }
                throw;
            }
        }

        public static List<TrashItem> List(string deviceRoot)
        {
            var res = new List<TrashItem>();
            string t = DirFor(deviceRoot);
            if (!Directory.Exists(t)) return res;
            foreach (var info in Directory.GetFiles(t, "*.info"))
            {
                try
                {
                    var lines = File.ReadAllLines(info, Encoding.UTF8);
                    string id = Path.GetFileNameWithoutExtension(info);
                    string slot = Path.Combine(t, id);
                    if (!Directory.Exists(slot)) { File.Delete(info); continue; }
                    string item = Directory.EnumerateFileSystemEntries(slot).FirstOrDefault();
                    if (item == null) { Directory.Delete(slot, true); File.Delete(info); continue; }
                    res.Add(new TrashItem
                    {
                        Id = id, InfoFile = info, SlotDir = slot, ItemPath = item,
                        OriginalRel = lines.Length > 0 ? lines[0] : Path.GetFileName(item),
                        Deleted = lines.Length > 1 && DateTime.TryParse(lines[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : File.GetCreationTime(info)
                    });
                }
                catch { }
            }
            return res;
        }

        public static string Restore(string deviceRoot, TrashItem ti)
        {
            string target = Path.Combine(deviceRoot, ti.OriginalRel);
            if (!PathUtil.IsUnder(target, deviceRoot) || IsTrashPath(deviceRoot, target))
                target = Path.Combine(deviceRoot, Path.GetFileName(ti.ItemPath));
            string parent = Path.GetDirectoryName(target);
            Directory.CreateDirectory(parent);
            target = PathUtil.UniqueName(parent, Path.GetFileName(target));
            if (Directory.Exists(ti.ItemPath)) Directory.Move(ti.ItemPath, target); else File.Move(ti.ItemPath, target);
            Directory.Delete(ti.SlotDir, true);
            File.Delete(ti.InfoFile);
            return target;
        }

        public static void Purge(TrashItem ti)
        {
            FileOps.ForceDelete(ti.SlotDir);
            File.Delete(ti.InfoFile);
        }
    }

    static class FileOps
    {
        public static void ForceDelete(string path)
        {
            if (Directory.Exists(path))
            {
                foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                foreach (var d in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(d, FileAttributes.Directory);
                Directory.Delete(path, true);
            }
            else if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
    }
}

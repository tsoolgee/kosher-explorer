using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace KosherExplorer
{
    /// <summary>Explorer's "New ▸" documents, read from the registry's ShellNew entries (only types whose program is installed).</summary>
    static class ShellNew
    {
        public class Kind
        {
            public string Ext, Name;
            public string NullFile;   // non-null: create an empty file
            public string Template;   // full path of a template file to copy
            public byte[] Data;       // bytes to write
        }

        static readonly string[] wanted = { ".docx", ".doc", ".rtf", ".xlsx", ".pptx", ".odt", ".ods", ".txt" };

        public static List<Kind> Available()
        {
            var res = new List<Kind>();
            foreach (var ext in wanted)
            {
                try { var k = Find(ext); if (k != null) res.Add(k); } catch { }
            }
            return res;
        }

        static Kind Find(string ext)
        {
            using (var e = Registry.ClassesRoot.OpenSubKey(ext))
            {
                if (e == null) return null;
                string progId = e.GetValue("") as string;
                RegistryKey sn = null;
                try
                {
                    if (!string.IsNullOrEmpty(progId)) sn = e.OpenSubKey(progId + "\\ShellNew");
                    if (sn == null) sn = e.OpenSubKey("ShellNew");
                    if (sn == null) return ext == ".txt" ? new Kind { Ext = ext, Name = Native.TypeName(ext, false), NullFile = "" } : null;
                    var k = new Kind { Ext = ext, Name = Native.TypeName(ext, false) };
                    if (sn.GetValue("FileName") is string fn && Resolve(fn) is string t) k.Template = t;
                    else if (sn.GetValue("Data") is byte[] b) k.Data = b;
                    else if (sn.GetValue("Data") is string ds) k.Data = System.Text.Encoding.Default.GetBytes(ds);
                    else if (sn.GetValue("NullFile") != null) k.NullFile = "";
                    else return null;   // "Command" and other kinds run programs; not offered
                    return k;
                }
                finally { sn?.Dispose(); }
            }
        }

        static string Resolve(string fn)
        {
            fn = Environment.ExpandEnvironmentVariables(fn);
            if (Path.IsPathRooted(fn)) return File.Exists(fn) ? fn : null;
            foreach (var dir in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ShellNew"),
                Environment.GetFolderPath(Environment.SpecialFolder.Templates),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonTemplates)
            })
            {
                try { string p = Path.Combine(dir, fn); if (File.Exists(p)) return p; } catch { }
            }
            return null;
        }

        public static void Create(Kind k, string path)
        {
            if (k.Template != null) { File.Copy(k.Template, path, false); File.SetAttributes(path, FileAttributes.Normal); }
            else using (var fs = new FileStream(path, FileMode.CreateNew)) { if (k.Data != null) fs.Write(k.Data, 0, k.Data.Length); }
            File.SetLastWriteTime(path, DateTime.Now);   // a template keeps its own date otherwise
        }
    }
}

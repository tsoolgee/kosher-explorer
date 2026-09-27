using System;
using System.Threading;
using System.Windows.Forms;

namespace KosherExplorer
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            using (var mutex = new Mutex(true, @"Local\KosherExplorer.SingleInstance", out bool first))
            {
                if (!first) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => ShowError(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowError(e.ExceptionObject as Exception);
                Application.Run(new MainForm(ConfigStore.Load()));
            }
        }

        static void ShowError(Exception ex)
        {
            try
            {
                MessageBox.Show("אירעה שגיאה:\n" + ex?.Message, "סייר כשר", MessageBoxButtons.OK, MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            }
            catch { }
        }
    }
}

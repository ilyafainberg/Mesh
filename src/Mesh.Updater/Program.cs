using System;
using System.Windows.Forms;

namespace Mesh.Updater
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (!UpdateOptions.TryParse(args, out var options, out var error))
            {
                MessageBox.Show(
                    error ?? "The update command was incomplete.",
                    "Mesh updater",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 2;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new UpdaterForm(options!))
            {
                Application.Run(form);
                return form.ExitCode;
            }
        }
    }
}

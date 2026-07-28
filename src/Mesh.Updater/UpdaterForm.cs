using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace Mesh.Updater
{
    internal sealed class UpdaterForm : Form
    {
        private readonly UpdateOptions options;
        private readonly Label titleLabel;
        private readonly Label statusLabel;
        private readonly PictureBox logo;
        private readonly string logPath;
        private readonly object logSync = new object();
        private bool allowUserClose;

        public UpdaterForm(UpdateOptions options)
        {
            this.options = options;
            logPath = CreateLogPath();

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(199, 245, 249);
            ClientSize = new Size(720, 220);
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "MeshUpdater";
            ShowIcon = true;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            Text = "Updating Mesh";
            TopMost = true;

            logo = new PictureBox
            {
                BackColor = Color.Transparent,
                Image = LoadLogo(),
                Location = new Point(38, 41),
                Size = new Size(142, 142),
                SizeMode = PictureBoxSizeMode.Zoom,
                TabStop = false
            };
            titleLabel = new Label
            {
                AutoSize = false,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 39F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(48, 82, 88),
                Location = new Point(205, 59),
                Size = new Size(485, 76),
                Text = "Updating Mesh...",
                TextAlign = ContentAlignment.MiddleLeft
            };
            statusLabel = new Label
            {
                AutoEllipsis = true,
                AutoSize = false,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(42, 61, 65),
                Location = new Point(211, 145),
                Size = new Size(450, 27),
                Text = "Status: Preparing update",
                TextAlign = ContentAlignment.TopLeft
            };

            Controls.Add(logo);
            Controls.Add(titleLabel);
            Controls.Add(statusLabel);
            Shown += OnShown;
            Click += OnFailureClick;
            titleLabel.Click += OnFailureClick;
            statusLabel.Click += OnFailureClick;
            logo.Click += OnFailureClick;
        }

        public int ExitCode { get; private set; }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width <= 0 || Height <= 0) return;
            var handle = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, Height, Height);
            try { Region = Region.FromHrgn(handle); }
            finally { DeleteObject(handle); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Color.FromArgb(68, 91, 96), 1F))
            using (var path = RoundedRectangle(
                new RectangleF(0.5F, 0.5F, ClientSize.Width - 1F, ClientSize.Height - 1F),
                ClientSize.Height - 2F))
                e.Graphics.DrawPath(pen, path);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!allowUserClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) logo.Image?.Dispose();
            base.Dispose(disposing);
        }

        private async void OnShown(object? sender, EventArgs e)
        {
            Log("Updater started for Mesh " + options.Version + ".");
            try
            {
                var workflow = new UpdateWorkflow(SetStatus, Log);
                await workflow.RunAsync(options, logPath + ".installer.log");
                ExitCode = 0;
                allowUserClose = true;
                Close();
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException
                || ex is InvalidOperationException || ex is UnauthorizedAccessException
                || ex is System.ComponentModel.Win32Exception || ex is CryptographicException)
            {
                ExitCode = 1;
                Log("Update failed: " + ex);
                var restarted = UpdateWorkflow.TryStartMesh(options.MeshExePath, Log);
                titleLabel.Text = "Update failed";
                titleLabel.Font = new Font("Segoe UI", 31F, FontStyle.Bold, GraphicsUnit.Point);
                statusLabel.ForeColor = Color.FromArgb(139, 36, 36);
                SetStatus(restarted
                    ? "Update failed. Mesh was restarted. Click to close"
                    : "Update failed. Start Mesh manually, then click to close");
                allowUserClose = true;
                Cursor = Cursors.Hand;
            }
        }

        private void SetStatus(string status)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(SetStatus), status);
                return;
            }
            statusLabel.Text = "Status: " + status;
            Log(status);
        }

        private void OnFailureClick(object? sender, EventArgs e)
        {
            if (allowUserClose && ExitCode != 0) Close();
        }

        private void Log(string message)
        {
            try
            {
                lock (logSync)
                    File.AppendAllText(logPath, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Trace.WriteLine("Mesh updater logging failed: " + ex.Message);
            }
        }

        private static string CreateLogPath()
        {
            try
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mesh", "Logs");
                Directory.CreateDirectory(directory);
                return Path.Combine(directory, "updater-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Trace.WriteLine("Mesh updater could not create its normal log directory: " + ex.Message);
                return Path.Combine(Path.GetTempPath(), "mesh-updater-" + Guid.NewGuid().ToString("N") + ".log");
            }
        }

        private static Image LoadLogo()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Mesh.Updater.Logo.png"))
            {
                if (stream == null) throw new InvalidOperationException("The Mesh updater logo is missing.");
                using (var image = Image.FromStream(stream))
                    return new Bitmap(image);
            }
        }

        private static GraphicsPath RoundedRectangle(RectangleF bounds, float diameter)
        {
            var size = Math.Min(diameter, Math.Min(bounds.Width, bounds.Height));
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, size, size, 180, 90);
            path.AddArc(bounds.Right - size, bounds.Top, size, size, 270, 90);
            path.AddArc(bounds.Right - size, bounds.Bottom - size, size, size, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - size, size, size, 90, 90);
            path.CloseFigure();
            return path;
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr handle);
    }
}

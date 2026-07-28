using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Mesh.Updater
{
    internal sealed class UpdateWorkflow
    {
        private const string ExpectedPublisher = "Feincraft";
        private readonly Action<string> report;
        private readonly Action<string> log;

        public UpdateWorkflow(Action<string> report, Action<string> log)
        {
            this.report = report;
            this.log = log;
        }

        public async Task RunAsync(UpdateOptions options, string installerLogPath)
        {
            report("Verifying the update");
            using var installerLock = File.Open(
                options.InstallerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actualHash = await ComputeSha256Async(options.InstallerPath).ConfigureAwait(true);
            if (!string.Equals(actualHash, options.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded installer checksum changed before installation.");

            if (!AuthenticodeVerifier.IsTrusted(options.InstallerPath, out var trustResult))
                throw new InvalidDataException(
                    "The downloaded installer does not have a trusted signature (0x"
                    + trustResult.ToString("X8", CultureInfo.InvariantCulture) + ").");
            if (!AuthenticodeVerifier.IsSignedByPublisher(options.InstallerPath, ExpectedPublisher))
                throw new InvalidDataException("The downloaded installer is not signed by Feincraft.");
            report("Closing Mesh");
            try
            {
                using (var quitEvent = EventWaitHandle.OpenExisting(options.QuitEventName))
                {
                    if (!quitEvent.Set())
                        throw new InvalidOperationException("Mesh did not accept the shutdown request.");
                }
            }
            catch (WaitHandleCannotBeOpenedException ex)
            {
                throw new InvalidOperationException("Mesh did not expose its update shutdown signal.", ex);
            }
            await WaitForMeshToExitAsync(options).ConfigureAwait(true);

            report("Installing Mesh " + options.Version);
            var installer = Process.Start(new ProcessStartInfo
            {
                FileName = options.InstallerPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NORESTARTAPPLICATIONS /SP- "
                    + "/CLOSEAPPLICATIONS /LOG=" + Quote(installerLogPath),
                WorkingDirectory = Path.GetDirectoryName(options.InstallerPath) ?? Path.GetTempPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (installer == null)
                throw new InvalidOperationException("The Mesh installer could not be started.");

            using (installer)
            {
                await Task.Run(() => installer.WaitForExit()).ConfigureAwait(true);
                if (installer.ExitCode != 0)
                    throw new InvalidOperationException("The Mesh installer exited with code " + installer.ExitCode + ".");
            }

            installerLock.Dispose();
            TryDeletePreparedUpdate(options.CleanupDirectory);

            report("Starting Mesh");
            var meshExe = ResolveMeshExecutable(options.MeshExePath);
            if (meshExe == null)
                throw new FileNotFoundException("Mesh was installed, but Mesh.App.exe could not be found.");
            StartMesh(meshExe);
            await Task.Delay(900).ConfigureAwait(true);
        }

        public static bool TryStartMesh(string preferredPath, Action<string> log)
        {
            try
            {
                var meshExe = ResolveMeshExecutable(preferredPath);
                if (meshExe == null) return false;
                StartMesh(meshExe);
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                log("Could not restart Mesh after an update failure: " + ex);
                return false;
            }
        }

        private async Task WaitForMeshToExitAsync(UpdateOptions options)
        {
            using (var process = FindMatchingMeshProcess(options))
            {
                if (process == null) return;
                if (await WaitForExitAsync(process, TimeSpan.FromSeconds(20)).ConfigureAwait(true)) return;

                report("Finishing Mesh shutdown");
                log("Mesh did not exit within 20 seconds; terminating its process tree.");
                TerminateProcessTree(options.MeshProcessId);
                if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(10)).ConfigureAwait(true))
                    throw new InvalidOperationException("Mesh could not be closed for the update.");
            }
        }

        private Process? FindMatchingMeshProcess(UpdateOptions options)
        {
            try
            {
                var process = Process.GetProcessById(options.MeshProcessId);
                if (process.HasExited)
                {
                    process.Dispose();
                    return null;
                }

                var actualTicks = process.StartTime.ToUniversalTime().Ticks;
                if (actualTicks != options.MeshStartTimeUtcTicks)
                {
                    log("The original Mesh process has exited and its PID was reused; no process was terminated.");
                    process.Dispose();
                    return null;
                }
                return process;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
        {
            return await Task.Run(() =>
            {
                try { return process.WaitForExit((int)timeout.TotalMilliseconds); }
                catch (InvalidOperationException) { return true; }
            }).ConfigureAwait(true);
        }

        private static void TerminateProcessTree(int processId)
        {
            var taskKill = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");
            using (var process = Process.Start(new ProcessStartInfo
            {
                FileName = taskKill,
                Arguments = "/PID " + processId.ToString(CultureInfo.InvariantCulture) + " /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }))
            {
                if (process == null)
                    throw new InvalidOperationException("Windows could not terminate Mesh.");
                process.WaitForExit(10000);
            }
        }

        private static Task<string> ComputeSha256Async(string path)
        {
            return Task.Run(() =>
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.SequentialScan))
                using (var sha = SHA256.Create())
                {
                    var hash = sha.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
                }
            });
        }

        private void TryDeletePreparedUpdate(string cleanupDirectory)
        {
            try
            {
                if (Directory.Exists(cleanupDirectory)) Directory.Delete(cleanupDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log("The installed update cache could not be removed: " + ex);
            }
        }

        private static string? ResolveMeshExecutable(string preferredPath)
        {
            if (File.Exists(preferredPath)) return preferredPath;
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Mesh", "Mesh.App.exe");
            return File.Exists(fallback) ? fallback : null;
        }

        private static void StartMesh(string meshExe)
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = meshExe,
                WorkingDirectory = Path.GetDirectoryName(meshExe) ?? string.Empty,
                UseShellExecute = true
            });
            if (process == null)
                throw new InvalidOperationException("Mesh could not be restarted.");
        }

        private static string Quote(string value) => "\"" + value + "\"";
    }
}

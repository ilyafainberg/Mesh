using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Mesh.Updater
{
    internal sealed class UpdateOptions
    {
        private UpdateOptions(string installerPath, string meshExePath, string cleanupDirectory, string expectedSha256,
            string version, string quitEventName, int meshProcessId, long meshStartTimeUtcTicks)
        {
            InstallerPath = installerPath;
            MeshExePath = meshExePath;
            CleanupDirectory = cleanupDirectory;
            ExpectedSha256 = expectedSha256;
            Version = version;
            QuitEventName = quitEventName;
            MeshProcessId = meshProcessId;
            MeshStartTimeUtcTicks = meshStartTimeUtcTicks;
        }

        public string InstallerPath { get; }
        public string MeshExePath { get; }
        public string CleanupDirectory { get; }
        public string ExpectedSha256 { get; }
        public string Version { get; }
        public string QuitEventName { get; }
        public int MeshProcessId { get; }
        public long MeshStartTimeUtcTicks { get; }

        public static bool TryParse(string[] args, out UpdateOptions? options, out string? error)
        {
            options = null;
            error = null;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    error = "The update command contains an incomplete argument.";
                    return false;
                }

                var name = args[i];
                if (name != "--installer" && name != "--mesh-exe" && name != "--sha256"
                    && name != "--version" && name != "--quit-event" && name != "--cleanup-dir"
                    && name != "--mesh-pid" && name != "--mesh-start-ticks")
                {
                    error = "The update command contains an unknown argument.";
                    return false;
                }
                if (values.ContainsKey(name))
                {
                    error = "The update command contains a duplicate argument.";
                    return false;
                }
                values[name] = args[i + 1];
            }

            if (!TryRequiredPath(values, "--installer", out var installerPath, out error)
                || !TryRequiredPath(values, "--mesh-exe", out var meshExePath, out error)
                || !TryRequiredPath(values, "--cleanup-dir", out var cleanupDirectory, out error))
                return false;

            if (!File.Exists(installerPath)
                || !Path.GetFileName(installerPath).StartsWith("Mesh-Setup", StringComparison.OrdinalIgnoreCase)
                || !installerPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                error = "The downloaded Mesh installer could not be found.";
                return false;
            }
            if (!File.Exists(meshExePath))
            {
                error = "The installed Mesh executable could not be found.";
                return false;
            }
            var cleanupRoot = cleanupDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var installerParent = (Path.GetDirectoryName(installerPath) ?? string.Empty)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!string.Equals(installerParent, cleanupRoot, StringComparison.OrdinalIgnoreCase))
            {
                error = "The update cleanup directory does not contain the installer.";
                return false;
            }
            if (!values.TryGetValue("--sha256", out var sha256)
                || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            {
                error = "The update checksum is invalid.";
                return false;
            }
            if (!values.TryGetValue("--version", out var version) || string.IsNullOrWhiteSpace(version))
            {
                error = "The update version is missing.";
                return false;
            }
            if (!values.TryGetValue("--quit-event", out var quitEventName)
                || !quitEventName.StartsWith(@"Local\MeshUpdateQuit-", StringComparison.Ordinal)
                || quitEventName.Length != @"Local\MeshUpdateQuit-".Length + 32
                || !quitEventName.Substring(@"Local\MeshUpdateQuit-".Length).All(Uri.IsHexDigit))
            {
                error = "The Mesh shutdown signal is invalid.";
                return false;
            }
            if (!values.TryGetValue("--mesh-pid", out var pidText)
                || !int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            {
                error = "The Mesh process identifier is invalid.";
                return false;
            }
            if (!values.TryGetValue("--mesh-start-ticks", out var ticksText)
                || !long.TryParse(ticksText, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0)
            {
                error = "The Mesh process start time is invalid.";
                return false;
            }

            options = new UpdateOptions(installerPath, meshExePath, cleanupDirectory,
                sha256.ToLowerInvariant(), version.Trim(),
                quitEventName, pid, ticks);
            return true;
        }

        private static bool TryRequiredPath(IReadOnlyDictionary<string, string> values, string key,
            out string path, out string? error)
        {
            path = string.Empty;
            error = null;
            if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value))
            {
                error = "The update command contains an invalid path.";
                return false;
            }

            try { path = Path.GetFullPath(value); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                error = "The update command contains an invalid path.";
                return false;
            }
            return true;
        }
    }
}

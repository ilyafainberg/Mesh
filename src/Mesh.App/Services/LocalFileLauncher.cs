using System.Diagnostics;

namespace Mesh.App.Services;

/// <summary>Opens local file links outside the Blazor WebView.</summary>
public static class LocalFileLauncher
{
    private static readonly HashSet<string> UnsafeLocalExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".msi", ".msix", ".appx",
        ".reg", ".lnk", ".url", ".scr", ".vbs", ".js", ".jse", ".wsf"
    };

    public static async Task OpenAsync(string fileUri)
    {
        if (!Uri.TryCreate(fileUri, UriKind.Absolute, out var uri) || !uri.IsFile)
            throw new ArgumentException("The link is not a valid file URI.", nameof(fileUri));

        var path = uri.LocalPath;
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException("The local file or directory was not found.", path);

        if (!OperatingSystem.IsWindows())
        {
            await Launcher.Default.OpenAsync(new OpenFileRequest { File = new ReadOnlyFile(path) });
            return;
        }

        if (Directory.Exists(path))
        {
            OpenExplorer(path, selectFile: false);
            return;
        }

        if (UnsafeLocalExtensions.Contains(Path.GetExtension(path)))
        {
            // Do not execute active content directly from chat or agent-generated output.
            OpenExplorer(path, selectFile: true);
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void OpenExplorer(string path, bool selectFile)
    {
        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        if (selectFile)
            startInfo.ArgumentList.Add("/select,");
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
    }
}

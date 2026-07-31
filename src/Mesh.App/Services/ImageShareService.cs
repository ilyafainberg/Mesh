using Microsoft.JSInterop;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace Mesh.App.Services;

public sealed record ImageShareRequest(string FileName, string MimeType, string Base64Data);

public interface IImageShareService
{
    string ActionLabel { get; }
    Task SaveOrShareAsync(ImageShareRequest request);
}

public sealed class ImageShareService(IJSRuntime js) : IImageShareService
{
    private static readonly TimeSpan NativeShareRetention = TimeSpan.FromMinutes(10);
    private const string StagingPrefix = "mesh-image-share-";

    private ImageShareMode Mode => ImageSharePolicy.Select(
        OperatingSystem.IsAndroid(), OperatingSystem.IsIOS());

    public string ActionLabel => Mode == ImageShareMode.NativeShare ? "Save / Share" : "Save";

    public async Task SaveOrShareAsync(ImageShareRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only image attachments can be shared.", nameof(request));

        if (Mode == ImageShareMode.BrowserDownload)
        {
            await js.InvokeVoidAsync(
                "meshUI.downloadFile",
                SafeFileName(request.FileName),
                request.MimeType,
                request.Base64Data);
            return;
        }

        var bytes = Convert.FromBase64String(request.Base64Data);
        var directory = Path.Combine(FileSystem.CacheDirectory, $"{StagingPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, SafeFileName(request.FileName));

        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Save or share image",
                File = new ShareFile(path)
            });
        }
        finally
        {
            Array.Clear(bytes);
            ScheduleCleanup(directory);
        }
    }

    private static string SafeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(name))
            return "image";

        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return name;
    }

    private static void ScheduleCleanup(string directory)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(NativeShareRetention);
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // The OS may still own the share file; the cache directory is safe to reap later.
            }
        });
    }
}

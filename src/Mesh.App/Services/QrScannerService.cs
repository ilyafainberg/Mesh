using Mesh.App.Components;
using Microsoft.Maui.ApplicationModel;
using ZXing.Net.Maui;

namespace Mesh.App.Services;

public sealed class QrScannerService : IQrScanner
{
    public bool IsSupported =>
        (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()) && BarcodeScanning.IsSupported;

    public async Task<string?> ScanAsync(CancellationToken ct = default)
    {
        if (!IsSupported)
            return null;

        var permission = await Permissions.RequestAsync<Permissions.Camera>();
        if (permission != PermissionStatus.Granted)
            throw new InvalidOperationException(
                "Camera permission is required to scan a link code. Enable Camera for Mesh in system settings, then try again.");

        return await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var root = Application.Current?.Windows.FirstOrDefault()?.Page
                ?? throw new InvalidOperationException("The scanner cannot open because the app window is not ready.");
            var page = new QrScannerPage();
            await root.Navigation.PushModalAsync(page);
            return await page.WaitForResultAsync(ct);
        });
    }
}

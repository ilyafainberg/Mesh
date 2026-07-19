using Mesh.Shared;
using Microsoft.Maui.ApplicationModel;
using ZXing.Net.Maui;

namespace Mesh.App.Components;

public partial class QrScannerPage : ContentPage
{
    private readonly TaskCompletionSource<string?> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int finished;

    public QrScannerPage()
    {
        InitializeComponent();
        CameraView.IsDetecting = true;
    }

    public async Task<string?> WaitForResultAsync(CancellationToken ct)
    {
        using var registration = ct.Register(() =>
            MainThread.BeginInvokeOnMainThread(() => Complete(null)));
        return await completion.Task;
    }

    private void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        var raw = e.Results.FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(raw))
            return;

        CameraView.IsDetecting = false;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (DeepLink.TryParsePairing(raw, out _))
            {
                Complete(raw);
                return;
            }

            StatusLabel.Text = "That QR code is not a valid Mesh device-link. Try another code.";
            CameraView.IsDetecting = true;
        });
    }

    private void OnCancel(object? sender, EventArgs e) => Complete(null);

    protected override bool OnBackButtonPressed()
    {
        Complete(null);
        return true;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (Volatile.Read(ref finished) == 0)
            Complete(null);
    }

    private async void Complete(string? result)
    {
        if (Interlocked.Exchange(ref finished, 1) != 0)
            return;

        CameraView.IsDetecting = false;
        try
        {
            if (Navigation.ModalStack.Contains(this))
                await Navigation.PopModalAsync();
        }
        finally
        {
            completion.TrySetResult(result);
        }
    }
}

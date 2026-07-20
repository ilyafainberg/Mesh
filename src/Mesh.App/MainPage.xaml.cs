using Microsoft.AspNetCore.Components.WebView;
#if IOS
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;
#endif
using Mesh.App.Services;

namespace Mesh.App;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
#if IOS
        // Keep the entire Blazor surface below the status bar, Dynamic Island, and sensor housing.
        On<iOS>().SetUseSafeArea(true);
#endif
    }

    private async void BlazorWebView_UrlLoading(object? sender, UrlLoadingEventArgs e)
    {
        if (!e.Url.IsFile)
            return;

        e.UrlLoadingStrategy = UrlLoadingStrategy.CancelLoad;

        try
        {
            await LocalFileLauncher.OpenAsync(e.Url.AbsoluteUri);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not open file", ex.Message, "OK");
        }
    }
}

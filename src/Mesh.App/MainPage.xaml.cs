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
        // Match the exposed iOS safe areas to the white mobile navigation surface.
        BackgroundColor = Colors.White;
        blazorWebView.BackgroundColor = Colors.White;
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
